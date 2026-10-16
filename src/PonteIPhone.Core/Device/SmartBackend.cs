using System.Diagnostics;
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
    /// <summary>Cosa ha visto WPD quando non è utilizzabile (cartelle, memoria): finisce nel registro.</summary>
    private string? _wpdDetail;
    /// <summary>Cartelle WPD già lette in questo processo, e se si è già letto tutto il Rullino.</summary>
    private readonly HashSet<string> _wpdFoldersRead = new(StringComparer.OrdinalIgnoreCase);
    private bool _wpdWhole;
    private readonly Action<TimeSpan> _sleep;

    /// <param name="sleep">Attesa tra due tentativi WPD (nei test non aspetta).</param>
    public SmartBackend(AppleBackend apple, Func<IDeviceBackend> wpdFactory, Action<TimeSpan>? sleep = null)
    {
        _apple = apple;
        _wpdFactory = wpdFactory;
        _sleep = sleep ?? Thread.Sleep;
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
        // Telefono riaperto (o ricollegato): l'elenco WPD di prima può avere ID non più validi.
        ForgetWpd();
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
    public string? LastEnumerationSummary => _apple.LastEnumerationSummary;

    public byte[]? GetThumbnail(string objectId) => _apple.GetThumbnail(objectId);

    public StorageBreakdown? GetStorage(CancellationToken ct) => _apple.GetStorage(ct);

    public DownloadResult Download(string objectId, string destination, Action<long> progress, CancellationToken ct) =>
        _apple.Download(objectId, destination, progress, ct);

    public IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds) => Delete(objectIds, _ => { });

    public IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds, Action<string?> step)
    {
        // In un processo nuovo (quello delle cancellazioni) l'elenco non c'è: si chiedono i dati al telefono.
        var files = new Dictionary<string, RemoteFile?>(StringComparer.Ordinal);
        foreach (var id in objectIds)
            if (!files.ContainsKey(id)) files[id] = _known.TryGetValue(id, out var k) ? k : _apple.Describe(id);
        var wanted = files.Values.OfType<RemoteFile>().ToList();

        var map = WpdFiles(wanted, step);
        // Il 15/10 l'eliminazione si fermava subito con "Windows non mostra le foto": il canale Apple legge anche
        // col telefono bloccato, WPD no. Se è bloccato lo si dice (il motore aspetta lo sblocco e riprova);
        // altrimenti si riprova un paio di volte, perché appena ricollegato Windows mostra la memoria con qualche secondo di ritardo.
        for (int attempt = 1; map is null && _wpdProblem == DeviceErrors.WpdUnavailable; attempt++)
        {
            if (_apple.IsLocked() == true)
            {
                ForgetWpd();
                return objectIds.Select(id => new DeleteResult(id, false, DeviceErrors.LockedForDelete)).ToList();
            }
            if (attempt == 3) break;
            _sleep(TimeSpan.FromSeconds(4));
            ForgetWpd();
            map = WpdFiles(wanted, step);
        }
        if (map is null) return objectIds.Select(id => new DeleteResult(id, false, _wpdProblem, _wpdDetail)).ToList();

        var results = new List<DeleteResult>(objectIds.Count);
        foreach (var id in objectIds)
        {
            var file = files[id];
            var wpdId = file is null ? null : Match(map, file);
            if (wpdId is not null && !_wpdWhole && !SameDay(map, wpdId, file!))
            {
                // Con solo alcune cartelle lette un omonimo di un altro mese non si vede: la data deve tornare.
                wpdId = null;
            }
            if (wpdId is null && file is not null && !_wpdWhole)
            {
                // Non era nelle cartelle del suo mese (data cambiata, nomi di cartella diversi): si legge tutto, una volta sola.
                try { ReadWholeWpd(map, step); }
                catch (DeviceException e) { step("Windows: lettura completa non riuscita: " + e.Message); }
                wpdId = Match(map, file);
            }
            if (wpdId is null)
            {
                results.Add(new DeleteResult(id, false, DeviceErrors.NoWpdMatch));
                continue;
            }
            var r = Wpd.Delete([wpdId]).FirstOrDefault() ?? new DeleteResult(wpdId, false, DeviceErrors.GenFailure);
            step(null);
            if (!r.Ok && DeviceErrors.IsNotFound(r.HResult) && _apple.Describe(id) is not null)
            {
                // WPD dice "non trovato" ma il file è ancora sul telefono: ID vecchio, non una cancellazione riuscita.
                r = r with { HResult = DeviceErrors.NoWpdMatch };
                ForgetWpd();
            }
            else if (r.Ok || DeviceErrors.IsNotFound(r.HResult))
            {
                RemoveFromMap(map, wpdId);
            }
            results.Add(r with { ObjectId = id });
        }
        return results;
    }

    private void ForgetWpd()
    {
        _wpdFiles = null;
        _wpdFilesFor = null;
        _wpdProblem = 0;
        _wpdDetail = null;
        _wpdFoldersRead.Clear();
        _wpdWhole = false;
    }

    private static void RemoveFromMap(Dictionary<string, List<RemoteFile>> map, string wpdId)
    {
        foreach (var list in map.Values) list.RemoveAll(f => f.ObjectId == wpdId);
    }

    /// <summary>Data di Windows e del canale Apple entro un giorno (fuso orario); senza una delle due non si scarta.</summary>
    private static bool SameDay(Dictionary<string, List<RemoteFile>> map, string wpdId, RemoteFile file)
    {
        var w = map.Values.SelectMany(l => l).FirstOrDefault(f => f.ObjectId == wpdId);
        var a = file.Created ?? file.Modified;
        var b = w?.Created ?? w?.Modified;
        return a is null || b is null || Math.Abs((a.Value - b.Value).TotalHours) <= 26;
    }

    private static void AddToMap(Dictionary<string, List<RemoteFile>> map, IReadOnlyList<RemoteFile> batch)
    {
        foreach (var f in batch)
        {
            if (!map.TryGetValue(f.Name, out var list)) map[f.Name] = list = [];
            if (!list.Any(x => x.ObjectId == f.ObjectId)) list.Add(f);
        }
    }

    /// <summary>
    /// Elenco WPD del telefono aperto con almeno le cartelle dei file da cancellare, o null (con il motivo in
    /// <see cref="_wpdProblem"/>). Il 16/10, appena Windows ha cominciato a mostrare le foto, leggere tutto il Rullino
    /// (3085 file, uno per uno) lasciava l'app ferma su "Tolgo dal telefono" finché il collegamento veniva chiuso:
    /// ora si leggono solo le cartelle che servono.
    /// </summary>
    private Dictionary<string, List<RemoteFile>>? WpdFiles(IReadOnlyList<RemoteFile> wanted, Action<string?> step)
    {
        var udid = _apple.OpenedUdid;
        if (udid is null) { _wpdProblem = DeviceErrors.WpdUnavailable; _wpdDetail = "telefono non aperto"; return null; }
        if (_wpdFiles is null || _wpdFilesFor != udid)
        {
            if (_wpdProblem != 0 && _wpdFilesFor == udid) return null;
            _wpdFilesFor = udid;
            try
            {
                var all = Wpd.ListDevices();
                var dev = PickWpdDevice(all, udid, _apple.UsbDeviceCount());
                if (dev is null)
                {
                    // Nessun iPhone in Windows è un "non mostra", non un "più telefoni".
                    _wpdProblem = all.Count == 0 ? DeviceErrors.WpdUnavailable : DeviceErrors.WpdAmbiguous;
                    _wpdDetail = $"Windows vede {all.Count} dispositivi portatili";
                    return null;
                }
                var opened = Wpd.Open(dev.Id);
                step(null);
                if (!opened.Ready)
                {
                    _wpdProblem = DeviceErrors.WpdUnavailable;
                    _wpdDetail = $"Windows: {opened.State}; " + string.Join(" | ", opened.Notes ?? []);
                    return null;
                }
                _wpdDetail = string.Join(" | ", opened.Notes ?? []);
                _wpdFiles = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
                _wpdFoldersRead.Clear();
                _wpdWhole = false;
            }
            catch (DeviceException e)
            {
                _wpdProblem = DeviceErrors.WpdUnavailable;
                _wpdDetail = "errore di Windows: " + e.Message;
                return null;
            }
        }
        var map = _wpdFiles;
        try
        {
            ReadWpdFolders(map, wanted, step);
            // Cartelle del mese trovate ma vuote: prima di dire "nessun file" si guarda tutto.
            if (map.Count == 0 && !_wpdWhole) ReadWholeWpd(map, step);
        }
        catch (DeviceException e)
        {
            _wpdFiles = null;
            _wpdProblem = DeviceErrors.WpdUnavailable;
            _wpdDetail = "errore di Windows: " + e.Message;
            return null;
        }
        if (map.Count == 0)
        {
            _wpdFiles = null;
            _wpdProblem = DeviceErrors.WpdUnavailable;
            _wpdDetail = "Windows apre l'iPhone ma non elenca nessun file; " + _wpdDetail;
            return null;
        }
        _wpdProblem = 0;
        return map;
    }

    /// <summary>
    /// Le cartelle WPD dove possono stare i file: stesso nome del canale Apple (100APPLE) oppure il mese della foto,
    /// perché Windows le mostra per mese (202610_a). Un giorno di margine per il fuso orario.
    /// </summary>
    private void ReadWpdFolders(Dictionary<string, List<RemoteFile>> map, IReadOnlyList<RemoteFile> wanted, Action<string?> step)
    {
        if (_wpdWhole) return;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var months = new HashSet<(int, int)>();
        foreach (var f in wanted)
        {
            if (f.Folder.Split('/')[0] is { Length: > 0 } top) names.Add(top);
            foreach (var d in new[] { f.Created, f.Modified })
            {
                if (d is not { } day) continue;
                foreach (var near in new[] { day.AddDays(-1), day, day.AddDays(1) }) months.Add((near.Year, near.Month));
            }
        }
        bool Wanted(string folder)
        {
            if (_wpdFoldersRead.Contains(folder)) return false;
            if (names.Contains(folder)) return true;
            return MediaGrouper.DateFromFolder(folder) is { } m && months.Contains((m.Year, m.Month));
        }

        var clock = Stopwatch.StartNew();
        int count = 0;
        var read = Wpd.EnumerateFolders(Wanted, batch =>
        {
            AddToMap(map, batch);
            count += batch.Count;
            step(null);
        }, CancellationToken.None);
        if (read.Contains("*"))
        {
            _wpdWhole = true;
            step($"Windows: letta tutta la memoria ({count} file) in {clock.Elapsed.TotalSeconds:0.0} s");
            return;
        }
        foreach (var r in read) _wpdFoldersRead.Add(r);
        if (read.Count == 0 && _wpdFoldersRead.Count == 0)
        {
            step("Windows: nessuna cartella del mese delle foto, leggo tutto il Rullino");
            ReadWholeWpd(map, step);
            return;
        }
        if (read.Count > 0)
            step($"Windows: lette le cartelle {string.Join(", ", read)} ({count} file) in {clock.Elapsed.TotalSeconds:0.0} s");
    }

    private void ReadWholeWpd(Dictionary<string, List<RemoteFile>> map, Action<string?> step)
    {
        var clock = Stopwatch.StartNew();
        int count = 0;
        Wpd.Enumerate(batch =>
        {
            AddToMap(map, batch);
            count += batch.Count;
            step(null);
        }, CancellationToken.None);
        _wpdWhole = true;
        step($"Windows: letto tutto il Rullino ({count} file) in {clock.Elapsed.TotalSeconds:0.0} s");
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
    /// Lo stesso file in WPD: sempre stesso nome E stessa dimensione (un elenco WPD incompleto non deve far
    /// cancellare l'omonimo di un'altra cartella). Se ce n'è più d'uno decidono la cartella, poi la data.
    /// Nel dubbio nessuno: meglio non cancellare che cancellare la foto sbagliata.
    /// </summary>
    internal static string? Match(Dictionary<string, List<RemoteFile>> map, RemoteFile f)
    {
        if (!map.TryGetValue(f.Name, out var byName) || byName.Count == 0) return null;
        var sameSize = byName.Where(w => w.Size == f.Size && f.Size > 0).ToList();
        if (sameSize.Count == 1) return sameSize[0].ObjectId;
        if (sameSize.Count == 0) return null;
        // Stesso nome e dimensione in più cartelle (contatore ripartito da IMG_0001): la cartella, se WPD la dà uguale…
        var sameFolder = sameSize.Where(w => w.Folder.Length > 0 && string.Equals(w.Folder, f.Folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sameFolder.Count == 1) return sameFolder[0].ObjectId;
        // …altrimenti la data.
        var sameDate = sameSize.Where(w => w.Created is { } c && f.Created is { } d && Math.Abs((c - d).TotalMinutes) < 2).ToList();
        return sameDate.Count == 1 ? sameDate[0].ObjectId : null;
    }

    public void Dispose()
    {
        _apple.Dispose();
        _wpd?.Dispose();
    }
}
