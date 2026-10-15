using PonteIPhone.Core.Apple;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Device;

/// <summary>
/// Il telefono come lo usa l'app (decisione del 14/10, dopo i rapporti veri):
/// - per guardare e copiare solo il servizio Apple (<see cref="AppleBackend"/>), che sugli iPhone veri
///   legge migliaia di file in pochi secondi, mentre WPD si piantava o vedeva la memoria vuota;
/// - per cancellare WPD, l'unica via che toglie le foto anche dall'app Foto (con AFC i file sparirebbero
///   ma le foto resterebbero nella libreria del telefono). WPD si apre solo qui, sul telefono giusto
///   (stesso UDID), e i file si ritrovano per nome, dimensione e data; nel dubbio non si cancella niente.
/// </summary>
public sealed class SmartBackend : IDeviceBackend
{
    private readonly AppleBackend _apple;
    private readonly Func<IDeviceBackend> _wpdFactory;
    private IDeviceBackend? _wpd;
    /// <summary>File già elencati (percorso → file): evitano di richiedere i dati al telefono quando si cancella.</summary>
    private readonly Dictionary<string, RemoteFile> _known = new(StringComparer.Ordinal);
    /// <summary>Elenco WPD per nome, letto una volta per telefono e tenuto finché vive questo processo.</summary>
    private Dictionary<string, List<RemoteFile>>? _wpdFiles;
    private string? _wpdFilesFor;
    /// <summary>Perché WPD non è utilizzabile (per non riprovare a ogni gruppo di file).</summary>
    private int _wpdProblem;

    public SmartBackend(AppleBackend apple, Func<IDeviceBackend> wpdFactory)
    {
        _apple = apple;
        _wpdFactory = wpdFactory;
    }

    private IDeviceBackend Wpd => _wpd ??= _wpdFactory();

    /// <summary>Servizio Apple assente (Dispositivi Apple non installato): nessun telefono, l'app lo spiega.</summary>
    public IReadOnlyList<DeviceInfo> ListDevices()
    {
        try { return _apple.ListDevices(); }
        catch (UsbmuxException) { return []; }
    }

    public OpenResult Open(string deviceId)
    {
        try
        {
            return _apple.Open(deviceId);
        }
        catch (AppleUnavailableException e)
        {
            // Di solito: "Autorizza" non ancora toccato su questo PC. L'app mostra "Sblocca l'iPhone".
            var udid = deviceId.StartsWith(AppleBackend.IdPrefix, StringComparison.Ordinal) ? deviceId[AppleBackend.IdPrefix.Length..] : deviceId;
            return new OpenResult(new DeviceInfo(deviceId, "", "Apple Inc.", "iPhone", udid, ""), null, OpenState.LockedOrNotTrusted,
                ["Canale Apple non utilizzabile: " + e.Message]);
        }
    }

    public void Enumerate(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct) =>
        _apple.Enumerate(files =>
        {
            foreach (var f in files) _known[f.ObjectId] = f;
            batch(files);
        }, ct);

    public int CloudOnlyFiles => _apple.CloudOnlyFiles;

    public byte[]? GetThumbnail(string objectId) => _apple.GetThumbnail(objectId);

    public DownloadResult Download(string objectId, string destination, Action<long> progress, CancellationToken ct) =>
        _apple.Download(objectId, destination, progress, ct);

    public IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds)
    {
        var map = WpdFiles();
        if (map is null) return objectIds.Select(id => new DeleteResult(id, false, _wpdProblem)).ToList();

        var results = new List<DeleteResult>(objectIds.Count);
        foreach (var id in objectIds)
        {
            // In un processo nuovo (quello delle cancellazioni) l'elenco non c'è: si chiedono i dati al telefono.
            var file = _known.TryGetValue(id, out var k) ? k : _apple.Describe(id);
            var wpdId = file is null ? null : Match(map, file);
            if (wpdId is null)
            {
                results.Add(new DeleteResult(id, false, DeviceErrors.NoWpdMatch));
                continue;
            }
            var r = Wpd.Delete([wpdId]).FirstOrDefault() ?? new DeleteResult(wpdId, false, DeviceErrors.GenFailure);
            results.Add(r with { ObjectId = id });
        }
        return results;
    }

    /// <summary>Elenco WPD del telefono aperto, o null (con il motivo in <see cref="_wpdProblem"/>).</summary>
    private Dictionary<string, List<RemoteFile>>? WpdFiles()
    {
        var udid = _apple.OpenedUdid;
        if (udid is null) { _wpdProblem = DeviceErrors.WpdUnavailable; return null; }
        if (_wpdFiles is not null && _wpdFilesFor == udid) return _wpdFiles;
        if (_wpdProblem != 0 && _wpdFilesFor == udid) return null;
        _wpdFilesFor = udid;
        try
        {
            var dev = PickWpdDevice(Wpd.ListDevices(), udid, _apple.UsbDeviceCount());
            if (dev is null)
            {
                _wpdProblem = DeviceErrors.WpdAmbiguous;
                return null;
            }
            var opened = Wpd.Open(dev.Id);
            if (!opened.Ready)
            {
                _wpdProblem = DeviceErrors.WpdUnavailable;
                return null;
            }
            var map = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
            Wpd.Enumerate(batch =>
            {
                foreach (var f in batch)
                {
                    if (!map.TryGetValue(f.Name, out var list)) map[f.Name] = list = [];
                    list.Add(f);
                }
            }, CancellationToken.None);
            if (map.Count == 0)
            {
                _wpdProblem = DeviceErrors.WpdUnavailable;
                return null;
            }
            _wpdProblem = 0;
            return _wpdFiles = map;
        }
        catch (DeviceException)
        {
            _wpdProblem = DeviceErrors.WpdUnavailable;
            return null;
        }
    }

    /// <summary>
    /// Lo stesso telefono in WPD. L'ID di Windows contiene il numero di serie USB, che per l'iPhone è l'UDID
    /// (senza trattino). Se non si ritrova, va bene solo se di iPhone ce n'è uno solo da entrambe le parti.
    /// </summary>
    internal static DeviceInfo? PickWpdDevice(IReadOnlyList<DeviceInfo> wpd, string udid, int appleUsbCount)
    {
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var key = Norm(udid);
        var byId = wpd.Where(d => Norm(d.Id).Contains(key, StringComparison.Ordinal) || Norm(d.Serial) == key).ToList();
        if (byId.Count == 1) return byId[0];
        if (byId.Count == 0 && wpd.Count == 1 && appleUsbCount == 1) return wpd[0];
        return null;
    }

    /// <summary>
    /// Lo stesso file in WPD: stesso nome e stessa dimensione; altrimenti stesso nome se è uno solo.
    /// Nel dubbio nessuno: meglio non cancellare che cancellare la foto sbagliata.
    /// </summary>
    internal static string? Match(Dictionary<string, List<RemoteFile>> map, RemoteFile f)
    {
        if (!map.TryGetValue(f.Name, out var byName) || byName.Count == 0) return null;
        var sameSize = byName.Where(w => w.Size == f.Size).ToList();
        if (sameSize.Count == 1) return sameSize[0].ObjectId;
        if (sameSize.Count > 1)
        {
            // Stesso nome e dimensione in due cartelle (contatore ripartito da IMG_0001): decide la data.
            var sameDate = sameSize.Where(w => w.Created is { } c && f.Created is { } d && Math.Abs((c - d).TotalMinutes) < 2).ToList();
            return sameDate.Count == 1 ? sameDate[0].ObjectId : null;
        }
        return byName.Count == 1 ? byName[0].ObjectId : null;
    }

    public void Dispose()
    {
        _apple.Dispose();
        _wpd?.Dispose();
    }
}
