using PonteIPhone.Core.Apple;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Device;

/// <summary>
/// Sceglie la via per parlare con l'iPhone:
/// 1. il servizio Apple (<see cref="AppleBackend"/>): legge anche se Windows ha visto il telefono
///    bloccato o "vuoto", e non perde il filo quando l'iPhone si blocca e si sblocca;
/// 2. se non c'è (Dispositivi Apple non installato, abbinamento non leggibile…), WPD come prima.
/// Le cancellazioni passano sempre da WPD, la via che usa l'app Foto: con AFC si toglierebbero i file
/// ma non le foto dalla libreria del telefono. I file si ritrovano in WPD per nome e dimensione.
/// </summary>
public sealed class SmartBackend : IDeviceBackend
{
    private readonly IDeviceBackend _apple;
    private readonly Func<IDeviceBackend> _wpdFactory;
    private IDeviceBackend? _wpd;
    private IDeviceBackend? _active;
    private string? _appleUnavailable;
    /// <summary>File elencati con il servizio Apple (percorso → file), per ritrovarli in WPD quando si cancella.</summary>
    private readonly Dictionary<string, RemoteFile> _known = new(StringComparer.Ordinal);
    private Dictionary<string, List<RemoteFile>>? _wpdFiles;

    public SmartBackend(IDeviceBackend apple, Func<IDeviceBackend> wpdFactory)
    {
        _apple = apple;
        _wpdFactory = wpdFactory;
    }

    private IDeviceBackend Wpd => _wpd ??= _wpdFactory();

    private bool AppleActive => _active is not null && ReferenceEquals(_active, _apple);

    public IReadOnlyList<DeviceInfo> ListDevices()
    {
        try
        {
            var list = _apple.ListDevices();
            _appleUnavailable = null;
            if (list.Count > 0) return list;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _appleUnavailable = e.Message;
        }
        return Wpd.ListDevices();
    }

    public OpenResult Open(string deviceId)
    {
        _known.Clear();
        _wpdFiles = null;
        if (!deviceId.StartsWith(AppleBackend.IdPrefix, StringComparison.Ordinal))
        {
            _active = Wpd;
            var r = Wpd.Open(deviceId);
            var why = _appleUnavailable is null ? "servizio Apple: nessun iPhone" : "servizio Apple non disponibile: " + _appleUnavailable;
            return r with { Notes = [.. (r.Notes ?? Array.Empty<string>()), "Canale: Windows (WPD), " + why] };
        }

        try
        {
            var r = _apple.Open(deviceId);
            _active = _apple;
            return r;
        }
        catch (DeviceException e) when (e.Kind == DeviceErrorKind.Gone)
        {
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException && IsWifiOnly(deviceId))
        {
            // In Wi-Fi Windows non vede il telefono: non c'è una via di riserva.
            throw new DeviceException("Collegamento in Wi-Fi non riuscito (" + e.Message + "): prova col cavo", DeviceErrors.NotReady, e);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Il servizio Apple c'è ma non va per questo telefono: si prova la via di Windows.
            var udid = deviceId[AppleBackend.IdPrefix.Length..];
            var dev = Wpd.ListDevices().FirstOrDefault()
                      ?? throw new DeviceException("Canale Apple non riuscito (" + e.Message + ") e Windows non vede l'iPhone", DeviceErrors.NotReady, e);
            _active = Wpd;
            var r = Wpd.Open(dev.Id);
            // Stesso ID e stessa identità dell'elenco: così l'app lo riconosce come lo stesso telefono.
            return r with
            {
                Device = r.Device with { Id = deviceId, Serial = udid },
                Notes = [.. (r.Notes ?? Array.Empty<string>()), "Canale Apple non riuscito: " + e.Message, "Canale: Windows (WPD)"],
            };
        }
    }

    private bool IsWifiOnly(string deviceId)
    {
        try { return _apple.ListDevices().FirstOrDefault(d => d.Id == deviceId)?.IsWifi == true; }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }

    private IDeviceBackend Active => _active ?? throw new DeviceException("iPhone non aperto", DeviceErrors.WpdDeviceNotOpen);

    public void Enumerate(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct)
    {
        if (!AppleActive)
        {
            Active.Enumerate(batch, ct);
            return;
        }
        _known.Clear();
        _apple.Enumerate(files =>
        {
            foreach (var f in files) _known[f.ObjectId] = f;
            batch(files);
        }, ct);
    }

    public byte[]? GetThumbnail(string objectId) => Active.GetThumbnail(objectId);

    public DownloadResult Download(string objectId, string destination, Action<long> progress, CancellationToken ct) =>
        Active.Download(objectId, destination, progress, ct);

    public IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds)
    {
        if (!AppleActive) return Active.Delete(objectIds);

        var map = WpdFiles();
        if (map is null)
            return objectIds.Select(id => new DeleteResult(id, false, DeviceErrors.NoWpdMatch)).ToList();

        var results = new List<DeleteResult>(objectIds.Count);
        foreach (var id in objectIds)
        {
            var wpdId = _known.TryGetValue(id, out var f) ? Match(map, f) : null;
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

    /// <summary>Elenco WPD per nome (una volta per apertura). Null se Windows non mostra le foto.</summary>
    private Dictionary<string, List<RemoteFile>>? WpdFiles()
    {
        if (_wpdFiles is not null) return _wpdFiles;
        try
        {
            var dev = Wpd.ListDevices().FirstOrDefault();
            if (dev is null) return null;
            var opened = Wpd.Open(dev.Id);
            if (!opened.Ready) return null;
            var map = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
            Wpd.Enumerate(batch =>
            {
                foreach (var f in batch)
                {
                    if (!map.TryGetValue(f.Name, out var list)) map[f.Name] = list = [];
                    list.Add(f);
                }
            }, CancellationToken.None);
            return _wpdFiles = map;
        }
        catch (DeviceException)
        {
            return null;
        }
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
