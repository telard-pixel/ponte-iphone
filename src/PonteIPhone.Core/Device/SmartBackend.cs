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
    /// <summary>Lettura completa già tentata (riuscita o no) con questa sessione WPD: non si ripete per ogni file.</summary>
    private bool _wpdWholeTried;
    /// <summary>La sessione WPD va buttata alla prossima chiamata (iPhone scollegato, ID non più validi).</summary>
    private bool _wpdStale;
    /// <summary>Telefono aperto l'ultima volta: riaprire lo stesso non butta via quello che Windows ha già mostrato.</summary>
    private string? _openedFor;
    /// <summary>ID usbmux del collegamento con cui è stato letto l'elenco di Windows: se cambia, il cavo è stato ricollegato.</summary>
    private int? _wpdMux;
    /// <summary>L'ultima lettura delle cartelle è fallita con un errore (non "memoria vuota"): una riprova sola.</summary>
    private bool _wpdReadFailed;
    private readonly Action<TimeSpan> _sleep;
    /// <summary>Quante volte riprovare, a 5 s l'una, se Windows mostra la memoria vuota.</summary>
    private const int WpdWaitAttempts = 18;

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
        // Revisione del 17/10: il motore riapre il telefono dopo ogni intoppo, e buttare ogni volta l'elenco di Windows
        // voleva dire rileggere la memoria per minuti, finché lo schermo si bloccava e il collegamento cadeva.
        // Si butta solo per un altro telefono o dopo uno scollegamento; un ID non più valido si scopre cancellando.
        if (deviceId != _openedFor || _wpdStale) ForgetWpd();
        _openedFor = deviceId;
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
        try
        {
            return DeleteCore(objectIds, step);
        }
        catch (System.Runtime.InteropServices.COMException e)
        {
            // Un errore grezzo di Windows non deve uscire così: il motore lo tratterebbe come "fermati".
            _wpdStale = true;
            throw new DeviceException("Windows: " + e.Message, e.HResult, e);
        }
        catch (DeviceException e) when (e.Kind == DeviceErrorKind.Gone || e.Code == DeviceErrors.WpdStale)
        {
            // Scollegato (da qualunque canale) o elenco non più valido: alla prossima chiamata si rilegge da capo.
            _wpdStale = true;
            throw;
        }
    }

    private IReadOnlyList<DeleteResult> DeleteCore(IReadOnlyList<string> objectIds, Action<string?> step)
    {
        if (_wpdStale) ForgetWpd();
        // Ricollegato nel frattempo (anche senza che nessuno se ne sia accorto): gli ID di Windows non valgono più.
        var mux = _apple.CurrentMuxId();
        if (mux is null) throw new DeviceException("iPhone non collegato", DeviceErrors.DeviceNotConnected);
        if (_wpdFiles is not null && mux != -1 && mux != _wpdMux) ForgetWpd();
        // Bloccato: si dice subito, prima di aprire Windows (il motore aspetta lo sblocco e riprova).
        if (_apple.IsLocked() == true)
            return objectIds.Select(id => new DeleteResult(id, false, DeviceErrors.LockedForDelete)).ToList();

        // In un processo nuovo (quello delle cancellazioni) l'elenco non c'è: si chiedono i dati al telefono.
        // Un file che il canale Apple non trova più è già stato tolto (es. da un tentativo interrotto): conta come fatto.
        var files = new Dictionary<string, RemoteFile?>(StringComparer.Ordinal);
        foreach (var id in objectIds)
            if (!files.ContainsKey(id)) files[id] = !IsDcim(id) ? _known.GetValueOrDefault(id) : _apple.Describe(id);
        var wanted = files.Values.OfType<RemoteFile>().ToList();
        if (wanted.Count == 0)
            return objectIds.Select(id => new DeleteResult(id, false, IsDcim(id) ? DeviceErrors.FileNotFound : DeviceErrors.NoWpdMatch)).ToList();

        var map = WpdFiles(wanted, step);
        // Il 17/10 (1.0.54) Windows mostrava la memoria vuota per un po' dopo ogni avvio o ricollegamento, e alle 11:33,
        // partendo più tardi, vedeva 2711 file: 8 secondi di attesa non bastavano. Ora fino a un minuto e mezzo, ogni 5 s,
        // il tempo di sbloccare lo schermo.
        int readFailures = 0;
        for (int attempt = 1; map is null && _wpdProblem == DeviceErrors.WpdUnavailable; attempt++)
        {
            if (_apple.IsLocked() == true)
            {
                ForgetWpd();
                return objectIds.Select(id => new DeleteResult(id, false, DeviceErrors.LockedForDelete)).ToList();
            }
            // Telefono sparito dal cavo: è uno scollegamento, non una memoria vuota (il motore aspetta che torni).
            if (_apple.CurrentMuxId() is null) throw new DeviceException("iPhone non collegato", DeviceErrors.DeviceNotConnected);
            // L'attesa serve alla memoria vuota; se Windows dà errore leggendo, una riprova sola (prima: 18 × 75 s).
            if (_wpdReadFailed && ++readFailures > 1) { _wpdProblem = DeviceErrors.WpdNoAnswer; break; }
            if (attempt > WpdWaitAttempts) break;
            step(attempt == 1
                ? "Windows mostra la memoria dell'iPhone vuota: aspetto che la prepari (fino a un minuto e mezzo) [" + _wpdDetail + "]"
                : null);
            _sleep(TimeSpan.FromSeconds(5));
            ForgetWpd();
            map = WpdFiles(wanted, step);
            if (map is not null) step($"Windows: memoria pronta dopo {attempt * 5} s");
        }
        if (map is null) return objectIds.Select(id => new DeleteResult(id, false, _wpdProblem, _wpdDetail)).ToList();

        var results = new List<DeleteResult>(objectIds.Count);
        // Elenco di Windows non più affidabile a metà giro: questo file e i restanti tornano al motore "da riprovare"
        // (non sono errori), quelli già fatti restano fatti, e la prossima chiamata rilegge (revisione del 17/10).
        void RetryFrom(int index, string why)
        {
            _wpdStale = true;
            step("Windows: " + why + ": rileggo l'elenco e riprovo");
            for (int k = index; k < objectIds.Count; k++) results.Add(new DeleteResult(objectIds[k], false, DeviceErrors.RetryLater));
        }
        bool GoneFromPhone(string id) => IsDcim(id) && _apple.Describe(id) is null;

        for (int i = 0; i < objectIds.Count; i++)
        {
            var id = objectIds[i];
            if (files[id] is not { } file)
            {
                results.Add(new DeleteResult(id, false, IsDcim(id) ? DeviceErrors.FileNotFound : DeviceErrors.NoWpdMatch));
                continue;
            }
            var wpdId = Match(map, file);
            // Con solo alcune cartelle lette, un omonimo identico di un altro mese non si vedrebbe: se le date non tornano
            // si legge il resto (una volta) e decide il confronto completo. Se la lettura completa non riesce, non si cancella.
            if (wpdId is not null && !_wpdWhole && !DatesAgree(map, wpdId, file))
            {
                if (!_wpdWholeTried) ReadWholeSafely(map, step);
                wpdId = Match(map, file);
                if (wpdId is not null && !_wpdWhole && !DatesAgree(map, wpdId, file)) wpdId = null;
            }
            // Non nelle cartelle del suo mese: tutto il resto, una volta sola e solo per il file principale (il video di una
            // Live Photo, la versione modificata e il .AAE spesso Windows non li mostra, o spariscono con la foto).
            if (wpdId is null && !_wpdWholeTried && IsMainFile(file, wanted))
            {
                if (GoneFromPhone(id))
                {
                    results.Add(new DeleteResult(id, false, DeviceErrors.FileNotFound)); // già tolto: inutile leggere minuti
                    continue;
                }
                ReadWholeSafely(map, step);
                wpdId = Match(map, file);
            }
            if (wpdId is null)
            {
                if (GoneFromPhone(id))
                {
                    results.Add(new DeleteResult(id, false, DeviceErrors.FileNotFound)); // già tolto (iOS toglie il video Live con la foto)
                    continue;
                }
                step(MissingNote(map, file));
                results.Add(new DeleteResult(id, false, DeviceErrors.NoWpdMatch));
                continue;
            }
            // Subito prima di cancellare: l'oggetto di Windows è ancora quel file? Con un elenco vecchio un ID potrebbe
            // indicarne un altro: in quel caso non si cancella niente e si rilegge.
            var now = Wpd.DescribeObject(wpdId);
            if (now is null && GoneFromPhone(id))
            {
                // Sparito anche dal telefono: iOS lo ha tolto insieme alla foto (video Live). È fatto.
                RemoveFromMap(map, wpdId);
                _known.Remove(id);
                results.Add(new DeleteResult(id, false, DeviceErrors.FileNotFound));
                continue;
            }
            if (now is null || now.Size != file.Size || !string.Equals(now.Name, file.Name, StringComparison.OrdinalIgnoreCase))
            {
                RetryFrom(i, $"l'oggetto di {file.Name} non corrisponde più ({now?.Name ?? "sparito"})");
                break;
            }
            var r = Wpd.Delete([wpdId]).FirstOrDefault() ?? new DeleteResult(wpdId, false, DeviceErrors.GenFailure);
            step(null);
            if (!r.Ok && DeviceErrors.Classify(r.HResult) == DeviceErrorKind.Gone)
                throw new DeviceException("iPhone scollegato durante l'eliminazione", r.HResult); // al nuovo tentativo quelli già tolti risultano "non trovati"
            if (!r.Ok && DeviceErrors.IsNotFound(r.HResult) && !GoneFromPhone(id) && IsDcim(id))
            {
                RetryFrom(i, $"{file.Name} non trovato da Windows ma ancora sul telefono"); // ID vecchi
                break;
            }
            if (r.Ok || DeviceErrors.IsNotFound(r.HResult))
            {
                RemoveFromMap(map, wpdId);
                _known.Remove(id);
            }
            else if (!r.Refused)
            {
                // Errore di Windows su questo file (es. "la periferica non funziona"): questo conta come non riuscito,
                // gli altri si riprovano con un elenco nuovo invece di insistere sulla stessa sessione.
                results.Add(r with { ObjectId = id });
                RetryFrom(i + 1, $"{file.Name} non cancellato ({DeviceErrors.Describe(r.HResult)})");
                break;
            }
            results.Add(r with { ObjectId = id });
        }
        return results;
    }

    private static bool IsDcim(string id) => id.StartsWith("/DCIM/", StringComparison.Ordinal);

    /// <summary>Lettura completa che non fa saltare la cancellazione se fallisce (tranne uno scollegamento).</summary>
    private void ReadWholeSafely(Dictionary<string, List<RemoteFile>> map, Action<string?> step)
    {
        try { ReadWholeWpd(map, step); }
        catch (DeviceException e) when (e.Kind != DeviceErrorKind.Gone) { step("Windows: lettura completa non riuscita: " + e.Message); }
    }

    /// <summary>Date di Windows e del canale Apple entro 26 ore (fuso orario); senza una delle due non si scarta.</summary>
    private static bool DatesAgree(Dictionary<string, List<RemoteFile>> map, string wpdId, RemoteFile file)
    {
        var w = map.Values.SelectMany(l => l).FirstOrDefault(f => f.ObjectId == wpdId);
        var a = file.Created ?? file.Modified;
        var b = w?.Created ?? w?.Modified;
        return a is null || b is null || Math.Abs((a.Value - b.Value).TotalHours) <= 26;
    }

    /// <summary>Il file che decide (la foto o il video), non il video Live, la versione modificata o il .AAE che l'accompagnano.</summary>
    internal static bool IsMainFile(RemoteFile file, IReadOnlyList<RemoteFile> requested)
    {
        if (MediaGrouper.IsSidecar(file.Extension)) return false;
        var baseName = Path.GetFileNameWithoutExtension(file.Name);
        if (System.Text.RegularExpressions.Regex.IsMatch(baseName, @"^[A-Za-z]+_E\d")) return false;
        if (MediaGrouper.IsVideo(file.Extension))
            return !requested.Any(o => !ReferenceEquals(o, file) && MediaGrouper.IsImage(o.Extension)
                && string.Equals(o.Folder, file.Folder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Path.GetFileNameWithoutExtension(o.Name), baseName, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>Per il registro: cosa mostra Windows con lo stesso nome (es. .JPG al posto di .HEIC con "Automatico").</summary>
    private static string MissingNote(Dictionary<string, List<RemoteFile>> map, RemoteFile file)
    {
        var baseName = Path.GetFileNameWithoutExtension(file.Name);
        var same = map.Values.SelectMany(l => l)
            .Where(w => string.Equals(Path.GetFileNameWithoutExtension(w.Name), baseName, StringComparison.OrdinalIgnoreCase))
            .Take(4).Select(w => $"{w.Name} {Transfer.TransferEngine.Format(w.Size)} in {w.Folder}").ToList();
        return $"Windows: {file.Name} ({Transfer.TransferEngine.Format(file.Size)}, {file.Folder}) non trovato" +
               (same.Count == 0 ? "; nessun file con lo stesso nome nelle cartelle lette" : "; con lo stesso nome: " + string.Join(", ", same));
    }

    private void ForgetWpd()
    {
        _wpdFiles = null;
        _wpdFilesFor = null;
        _wpdProblem = 0;
        _wpdDetail = null;
        _wpdFoldersRead.Clear();
        _wpdWhole = false;
        _wpdWholeTried = false;
        _wpdStale = false;
        _wpdMux = null;
        _wpdReadFailed = false;
    }

    private static void RemoveFromMap(Dictionary<string, List<RemoteFile>> map, string wpdId)
    {
        foreach (var list in map.Values) list.RemoveAll(f => f.ObjectId == wpdId);
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
                // Cosa ha trovato Windows aprendo il telefono (DCIM o no, cartelle viste): nel registro anche quando va.
                step("Windows: " + _wpdDetail);
                _wpdFiles = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase);
                _wpdMux = _apple.CurrentMuxId();
                _wpdFoldersRead.Clear();
                _wpdWhole = false;
                _wpdWholeTried = false;
            }
            catch (DeviceException e) when (e.Kind != DeviceErrorKind.Gone)
            {
                _wpdProblem = DeviceErrors.WpdUnavailable;
                _wpdDetail = "errore di Windows: " + e.Message;
                _wpdReadFailed = true;
                return null;
            }
        }
        var map = _wpdFiles;
        try
        {
            ReadWpdFolders(map, wanted, step);
            // Cartelle del mese trovate ma vuote: prima di dire "nessun file" si guarda tutto.
            if (map.Count == 0 && !_wpdWholeTried) ReadWholeWpd(map, step);
        }
        catch (DeviceException e) when (e.Kind != DeviceErrorKind.Gone)
        {
            // Uno scollegamento invece esce: il motore aspetta che l'iPhone torni (prima diventava "memoria vuota" e
            // dopo 90 s fermava tutto).
            _wpdFiles = null;
            _wpdProblem = DeviceErrors.WpdUnavailable;
            _wpdDetail = "errore di Windows: " + e.Message;
            _wpdReadFailed = true;
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
        }, CancellationToken.None, step);
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
        // iOS può segnare dei file come non cancellabili (es. con Foto di iCloud): si annota, per capire un rifiuto.
        int locked = map.Values.Sum(l => l.Count(f => !f.CanDelete));
        if (locked > 0) step($"Windows: {locked} file segnati come non cancellabili nelle cartelle lette");
    }

    private void ReadWholeWpd(Dictionary<string, List<RemoteFile>> map, Action<string?> step)
    {
        _wpdWholeTried = true;
        var clock = Stopwatch.StartNew();
        int count = 0;
        // Le cartelle non ancora lette, con le note di avanzamento (l'elenco normale non ne ha).
        var read = Wpd.EnumerateFolders(name => !_wpdFoldersRead.Contains(name), batch =>
        {
            AddToMap(map, batch);
            count += batch.Count;
            step(null);
        }, CancellationToken.None, step);
        foreach (var r in read) _wpdFoldersRead.Add(r);
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
