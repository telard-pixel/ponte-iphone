using System.Diagnostics;
using System.Security.Cryptography;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;

namespace PonteIPhone.Core.Transfer;

public enum TransferMode
{
    Copy,
    /// <summary>Copia, verifica e poi cancella dal telefono.</summary>
    Move,
    /// <summary>Solo cancellazione dal telefono, senza copia (scelta esplicita dell'utente).</summary>
    Delete,
}

public sealed record TransferOptions
{
    public required string DestinationRoot { get; init; }
    public bool ByYearMonth { get; init; } = true;
    public bool SkipAlreadyCopied { get; init; } = true;
    public bool ConvertHeic { get; init; }
    public bool KeepHeicOriginal { get; init; } = true;
    public bool IncludeEdited { get; init; } = true;
    public bool IncludeSidecars { get; init; }
    public int JpegQuality { get; init; } = 92;
    public int MaxAttempts { get; init; } = 6;
    /// <summary>Attese tra un tentativo e l'altro (l'ultima si ripete).</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];
    /// <summary>Ogni quanto si cerca il telefono quando è scollegato o bloccato.</summary>
    public TimeSpan DevicePollInterval { get; init; } = TimeSpan.FromSeconds(3);

    public static TransferOptions From(AppSettings s, string? destination = null) => new()
    {
        DestinationRoot = destination ?? s.DestinationRoot,
        ByYearMonth = s.ByYearMonth,
        SkipAlreadyCopied = s.SkipAlreadyCopied,
        ConvertHeic = s.ConvertHeicToJpeg,
        KeepHeicOriginal = s.KeepHeicOriginal,
        IncludeEdited = s.IncludeEdited,
        IncludeSidecars = s.IncludeSidecars,
    };
}

public enum TransferPhase { Preparing, Copying, WaitingForDevice, Deleting, Done }

public sealed record TransferStatus(
    TransferPhase Phase,
    int ItemsDone,
    int ItemsTotal,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    TimeSpan? Remaining,
    string? CurrentFile,
    string? CurrentFolder,
    int Copied,
    int Skipped,
    int Failed,
    int Retries,
    string? Notice);

public sealed record ItemFailure(MediaItem Item, string Reason);

public sealed class TransferReport
{
    public TransferMode Mode { get; init; }
    public int Copied { get; set; }
    /// <summary>Già sul PC: non ricopiati.</summary>
    public int Skipped { get; set; }
    public List<ItemFailure> Failed { get; } = [];
    public int Deleted { get; set; }
    public int DeleteFailed { get; set; }
    /// <summary>Il telefono ha rifiutato la cancellazione: tutto copiato, niente tolto dal telefono.</summary>
    public bool DeleteRefused { get; set; }
    /// <summary>Motivo del primo elemento non tolto dal telefono, per spiegarlo all'utente.</summary>
    public string? DeleteProblem { get; set; }
    public int Retries { get; set; }
    public bool Canceled { get; set; }
    /// <summary>Interruzione di tutto il trasferimento (es. disco pieno).</summary>
    public string? AbortReason { get; set; }
    public long Bytes { get; set; }
    public TimeSpan Elapsed { get; set; }
    public HashSet<string> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Elementi interi e verificati sul PC (copiati ora o già presenti).</summary>
    public List<MediaItem> Safe { get; } = [];
    /// <summary>Elementi tolti dal telefono.</summary>
    public List<MediaItem> Removed { get; } = [];
    public List<string> ConversionWarnings { get; } = [];
}

/// <summary>Pausa del trasferimento: si ferma tra un file e l'altro.</summary>
public sealed class PauseController
{
    private volatile TaskCompletionSource? _gate;

    public bool IsPaused => _gate is not null;

    public void Pause() => _gate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Resume()
    {
        var g = _gate;
        _gate = null;
        g?.TrySetResult();
    }

    public Task WaitAsync(CancellationToken ct) => _gate?.Task.WaitAsync(ct) ?? Task.CompletedTask;
}

/// <summary>Il trasferimento si interrompe del tutto (es. disco pieno): inutile continuare con gli altri file.</summary>
public sealed class TransferAbortedException(string message) : Exception(message);

/// <summary>
/// Copia e sposta gli elementi dal telefono al PC con queste garanzie:
/// 1. ogni file arriva in un file temporaneo .part, con l'impronta SHA-256 calcolata mentre arriva;
/// 2. si controlla la dimensione e si rilegge il file dal disco confrontando l'impronta;
/// 3. solo allora prende il suo nome definitivo, senza mai sovrascrivere un file esistente;
/// 4. con Sposta si cancella dal telefono alla fine, e solo gli elementi completi sul PC.
/// Blocchi, scollegamenti e telefono bloccato si gestiscono riprovando e aspettando che torni.
/// </summary>
public sealed class TransferEngine
{
    public const string StagingFolderName = ".ponte-iphone-temp";

    private readonly IDeviceConnection _conn;
    private readonly DeviceInfo _device;
    private readonly CopyRegistry _registry;
    private readonly IImageConverter? _converter;
    private readonly AppLog? _log;
    private readonly Dictionary<string, string> _ids = new(StringComparer.Ordinal);

    public TransferEngine(IDeviceConnection conn, DeviceInfo device, CopyRegistry registry, IImageConverter? converter, AppLog? log = null)
    {
        _conn = conn;
        _device = device;
        _registry = registry;
        _converter = converter;
        _log = log;
    }

    // Stato per l'avanzamento
    private TransferReport _report = new();
    private IProgress<TransferStatus>? _progress;
    private TransferPhase _phase;
    private int _itemsDone, _itemsTotal;
    private long _bytesDone, _bytesTotal, _currentFileBytes;
    private string? _currentFile, _currentFolder, _notice;
    private readonly Queue<(long Ticks, long Bytes)> _samples = new();
    private long _lastReport;

    public async Task<TransferReport> RunAsync(IReadOnlyList<MediaItem> items, TransferMode mode, TransferOptions o,
        IProgress<TransferStatus>? progress, PauseController? pause, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (mode == TransferMode.Move)
        {
            // Con Sposta l'elemento sparisce dal telefono: sul PC deve restare tutto ciò che c'era.
            // L'originale HEIC accanto al JPG (la conversione perde HDR e profondità) e i .AAE (le modifiche annullabili).
            o = o with { KeepHeicOriginal = o.KeepHeicOriginal || o.ConvertHeic, IncludeSidecars = true };
        }
        WithTiming(o);
        _report = new TransferReport { Mode = mode };
        _verified.Clear();
        _progress = progress;
        _itemsTotal = items.Count;
        _itemsDone = 0;
        _bytesDone = 0;
        _ids.Clear();
        foreach (var f in items.SelectMany(i => i.Files)) _ids[f.File.Key] = f.File.ObjectId;

        // Con Sposta l'elemento sparisce dal telefono: la versione modificata si copia sempre, se no andrebbe persa.
        bool includeEdited = mode == TransferMode.Move || o.IncludeEdited;
        var planner = new DestinationPlanner(o.DestinationRoot, o.ByYearMonth);
        var staging = Path.Combine(o.DestinationRoot, StagingFolderName);

        try
        {
            SetPhase(TransferPhase.Preparing, "Preparo la copia…");
            Directory.CreateDirectory(o.DestinationRoot);
            PrepareStaging(staging);

            // Cosa c'è davvero da copiare, per la barra e per lo spazio libero.
            var plan = items.Select(i => (Item: i, Files: i.FilesToCopy(includeEdited, o.IncludeSidecars).ToList())).ToList();
            var needed = plan.SelectMany(p => p.Files).Where(f => !(o.SkipAlreadyCopied && AlreadyOnPc(f.File) is not null))
                .Sum(f => f.File.Size);
            _bytesTotal = needed;
            CheckFreeSpace(o.DestinationRoot, needed, o.ConvertHeic && o.KeepHeicOriginal);

            SetPhase(TransferPhase.Copying, null);
            foreach (var (item, files) in plan)
            {
                ct.ThrowIfCancellationRequested();
                if (pause is not null) await pause.WaitAsync(ct);
                var ok = await CopyItemAsync(item, files, planner, staging, o, ct);
                if (ok) _report.Safe.Add(item);
                _itemsDone++;
                Report(force: true);
            }

            if (mode == TransferMode.Move && _report.Safe.Count > 0)
                await DeleteItemsAsync(_report.Safe, item => item.FilesToCopy(includeEdited, o.IncludeSidecars)
                    .All(f => VerifiedOnPc(f.File)), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _report.Canceled = true;
            _log?.Info("Trasferimento annullato dall'utente");
        }
        catch (TransferAbortedException e)
        {
            _report.AbortReason = e.Message;
            _log?.Warn("Trasferimento interrotto: " + e.Message);
        }
        finally
        {
            CleanStaging(staging);
            _report.Elapsed = clock.Elapsed;
            SetPhase(TransferPhase.Done, null);
        }
        _log?.Info($"Fine: {_report.Copied} copiati, {_report.Skipped} già presenti, {_report.Failed.Count} non riusciti, " +
                   $"{_report.Deleted} tolti dal telefono, {_report.Retries} nuovi tentativi, {_report.Bytes} byte in {_report.Elapsed}");
        return _report;
    }

    // ------------------------------------------------------------------ copia di un elemento

    private sealed record Staged(MediaFile File, string Path, long Size, string Sha256);

    private async Task<bool> CopyItemAsync(MediaItem item, List<MediaFile> files, DestinationPlanner planner, string staging,
        TransferOptions o, CancellationToken ct)
    {
        var present = new List<(MediaFile File, CopyRecord Record)>();
        var toCopy = new List<MediaFile>();
        foreach (var f in files)
        {
            var rec = o.SkipAlreadyCopied ? AlreadyOnPc(f.File) : null;
            if (rec is not null) present.Add((f, rec));
            else toCopy.Add(f);
        }

        if (toCopy.Count == 0)
        {
            _report.Skipped++;
            foreach (var (_, rec) in present) _report.Folders.Add(Path.GetDirectoryName(rec.Destination)!);
            return true;
        }

        var staged = new List<Staged>();
        try
        {
            foreach (var f in toCopy)
            {
                _currentFile = f.File.Name;
                var path = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".part");
                var res = await DownloadVerifiedAsync(f.File, path, ct);
                if (res is null)
                {
                    // Il file non c'è più sul telefono, o il telefono lo rifiuta: l'elemento non è completo.
                    return Fail(item, _lastFailure ?? "non copiato");
                }
                staged.Add(new Staged(f, path, res.Bytes, res.Sha256));
                _bytesDone += f.File.Size;
                _currentFileBytes = 0;
            }

            // Data vera dal file principale (o dalla copia già presente), poi quella del telefono.
            var primaryStaged = staged.FirstOrDefault(s => s.File.Role == FileRole.Primary) ?? staged[0];
            var date = CaptureDate.FromFile(primaryStaged.Path)
                       ?? present.Select(p => CaptureDate.FromFile(p.Record.Destination)).FirstOrDefault(d => d is not null)
                       ?? item.Date;
            var folder = planner.FolderFor(date);
            if (present.Count > 0)
            {
                // Metà dell'elemento è già sul PC: l'altra metà va nella stessa cartella.
                folder = Path.GetDirectoryName(present[0].Record.Destination) ?? folder;
            }
            _currentFolder = planner.Relative(folder);
            Report();

            Directory.CreateDirectory(folder);
            Finalize(staged, folder, o);
            _report.Copied++;
            _report.Folders.Add(folder);
            return true;
        }
        catch (IOException e) when (IsDiskFull(e))
        {
            throw new TransferAbortedException("Il disco di destinazione è pieno");
        }
        catch (UnauthorizedAccessException e)
        {
            throw new TransferAbortedException("Non ho il permesso di scrivere nella cartella di destinazione: " + e.Message);
        }
        catch (IOException e)
        {
            // Un file bloccato da un altro programma, un nome non accettato dal disco...: solo questo elemento.
            return Fail(item, "errore sul PC: " + e.Message);
        }
        finally
        {
            foreach (var s in staged) TryDelete(s.Path);
            foreach (var s in staged) TryDelete(s.Path + ".jpg");
        }
    }

    private bool Fail(MediaItem item, string reason)
    {
        _report.Failed.Add(new ItemFailure(item, reason));
        _log?.Warn($"Non copiato {item}: {reason}");
        return false;
    }

    /// <summary>Copia registrata e ancora intera sul PC.</summary>
    private CopyRecord? AlreadyOnPc(RemoteFile f)
    {
        var rec = _registry.Get(_device.Key, f.Key);
        return rec is not null && CopyRegistry.StillOnDisk(rec) ? rec : null;
    }

    /// <summary>File copiati e confrontati con l'impronta in questo trasferimento.</summary>
    private readonly HashSet<string> _verified = new(StringComparer.Ordinal);

    /// <summary>
    /// Ultimo controllo prima di togliere un file dal telefono con Sposta. Copiato adesso: già confrontato.
    /// Copiato in passato: si rilegge la copia sul PC e se ne confronta l'impronta (la stessa dimensione non basta:
    /// un file riscritto o rovinato può averla uguale).
    /// </summary>
    private bool VerifiedOnPc(RemoteFile f)
    {
        var rec = AlreadyOnPc(f);
        if (rec is null) return false;
        if (_verified.Contains(f.Key)) return true;
        try
        {
            if (string.Equals(HashFile(rec.Destination), rec.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _verified.Add(f.Key);
                return true;
            }
            _log?.Warn($"Copia sul PC diversa da quella registrata: {rec.Destination}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log?.Warn($"Copia sul PC non leggibile: {rec.Destination}: {e.Message}");
        }
        return false;
    }

    private string? _lastFailure;

    /// <summary>
    /// Scarica con i tentativi necessari. Null se il file non si può copiare (sparito o rifiutato);
    /// eccezione se va interrotto tutto (annullamento, disco pieno).
    /// </summary>
    private async Task<DownloadResult?> DownloadVerifiedAsync(RemoteFile file, string path, CancellationToken ct)
    {
        _lastFailure = null;
        int attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;
            try
            {
                if (!_ids.TryGetValue(file.Key, out var objectId))
                {
                    _lastFailure = "il file non c'è più sul telefono";
                    return null;
                }
                _currentFileBytes = 0;
                var res = await _conn.DownloadAsync(objectId, path, b => { _currentFileBytes = b; Report(); }, ct);
                if (file.Size > 0 && res.Bytes != file.Size)
                    throw new DeviceException($"arrivati {res.Bytes} byte su {file.Size}", DeviceErrors.SizeMismatch);

                // Rilettura dal disco: scopre errori di scrittura (chiavette, dischi di rete, disco che cede).
                var reread = await HashFileAsync(path, ct);
                if (!string.Equals(reread, res.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new DeviceException("impronta diversa alla rilettura", DeviceErrors.VerifyFailed);

                _report.Bytes += res.Bytes;
                if (_notice is not null) { _notice = null; Report(force: true); }
                return res;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(path);
                throw;
            }
            catch (IOException e) when (IsDiskFull(e))
            {
                TryDelete(path);
                throw new TransferAbortedException("Il disco di destinazione è pieno");
            }
            catch (Exception e) when (e is DeviceException or IOException)
            {
                TryDelete(path);
                _currentFileBytes = 0;
                var code = e is DeviceException de ? de.Code : e.HResult;
                var kind = e is DeviceException d2 ? d2.Kind : DeviceErrorKind.Transient;
                _log?.Warn($"{file.Name}, tentativo {attempt}: {e.Message} (0x{code:X8}, {kind})");

                switch (kind)
                {
                    case DeviceErrorKind.NotFound:
                        _lastFailure = "il file non c'è più sul telefono";
                        return null;
                    case DeviceErrorKind.Local:
                        throw new TransferAbortedException(DeviceErrors.Describe(code));
                    case DeviceErrorKind.Gone:
                        // Non conta come tentativo: si aspetta che l'iPhone torni.
                        attempt--;
                        await WaitForDeviceAsync(ct);
                        continue;
                }

                // L'iPhone che si blocca da solo (Blocco automatico) non dà un errore suo: le letture
                // falliscono con "accesso negato" o "periferica non funzionante". Prima di contare il
                // tentativo, o di dare il file per rifiutato, si guarda se è solo bloccato.
                if (await WaitIfLockedAsync(ct))
                {
                    attempt--;
                    continue;
                }
                if (kind == DeviceErrorKind.Refused)
                {
                    _lastFailure = DeviceErrors.Describe(code);
                    return null;
                }

                if (attempt >= _maxAttempts)
                {
                    _lastFailure = $"{DeviceErrors.Describe(code)} ({attempt} tentativi)";
                    return null;
                }
                _report.Retries++;
                _notice = $"L'iPhone non risponde ({DeviceErrors.Describe(code)}): riprovo, tentativo {attempt + 1} di {_maxAttempts}";
                Report(force: true);
                await Task.Delay(_delays[Math.Min(attempt - 1, _delays.Count - 1)], ct);

                // Se nel frattempo è stato scollegato, si aspetta che torni.
                if (!await DevicePresentAsync(ct)) await WaitForDeviceAsync(ct);
            }
        }
    }

    private int _maxAttempts = 6;
    private IReadOnlyList<TimeSpan> _delays = [TimeSpan.FromSeconds(1)];
    private TimeSpan _poll = TimeSpan.FromSeconds(3);

    private void WithTiming(TransferOptions o)
    {
        _maxAttempts = Math.Max(1, o.MaxAttempts);
        _delays = o.RetryDelays.Count > 0 ? o.RetryDelays : [TimeSpan.FromSeconds(1)];
        _poll = o.DevicePollInterval;
    }

    private async Task<bool> DevicePresentAsync(CancellationToken ct)
    {
        try
        {
            var list = await _conn.ListDevicesAsync(ct);
            return list.Any(d => d.Key == _device.Key);
        }
        catch (DeviceException)
        {
            return false;
        }
    }

    /// <summary>
    /// Controlla se l'iPhone è scollegato o bloccato; in quel caso aspetta che torni pronto e restituisce true
    /// (chi chiama riprova senza contare il tentativo). False: il telefono è lì e sbloccato, l'errore era un altro.
    /// </summary>
    private async Task<bool> WaitIfLockedAsync(CancellationToken ct)
    {
        try
        {
            var dev = (await _conn.ListDevicesAsync(ct)).FirstOrDefault(d => d.Key == _device.Key);
            if (dev is not null)
            {
                var opened = await _conn.OpenAsync(dev.Id, ct);
                if (opened.Ready) return false;
                _log?.Info("L'iPhone risulta bloccato");
            }
        }
        catch (DeviceException e)
        {
            _log?.Warn("Controllo dell'iPhone: " + e.Message);
        }
        await WaitForDeviceAsync(ct);
        return true;
    }

    /// <summary>
    /// Aspetta che l'iPhone torni (ricollegato e sbloccato), lo riapre e rilegge l'elenco dei file:
    /// dopo un nuovo collegamento gli ID di Windows cambiano, i file si ritrovano per cartella, nome e dimensione.
    /// </summary>
    private async Task WaitForDeviceAsync(CancellationToken ct)
    {
        var previous = _phase;
        SetPhase(TransferPhase.WaitingForDevice, "Sblocca l'iPhone (o ricollegalo): si riprende da solo");
        _log?.Info("In attesa dell'iPhone");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var dev = (await _conn.ListDevicesAsync(ct)).FirstOrDefault(d => d.Key == _device.Key);
                if (dev is not null)
                {
                    var opened = await _conn.OpenAsync(dev.Id, ct);
                    if (opened.Ready)
                    {
                        _notice = "iPhone ritrovato: rileggo l'elenco dei file…";
                        Report(force: true);
                        var fresh = new Dictionary<string, string>(StringComparer.Ordinal);
                        await _conn.EnumerateAsync(batch => { foreach (var f in batch) fresh[f.Key] = f.ObjectId; }, ct);
                        _ids.Clear();
                        foreach (var (k, v) in fresh) _ids[k] = v;
                        _log?.Info($"iPhone ritrovato, {fresh.Count} file");
                        _notice = null;
                        SetPhase(previous == TransferPhase.WaitingForDevice ? TransferPhase.Copying : previous, null);
                        return;
                    }
                    _notice = "Sblocca l'iPhone (e tocca Autorizza se lo chiede): si riprende da solo";
                    Report(force: true);
                }
            }
            catch (DeviceException e)
            {
                _log?.Warn("Ricerca dell'iPhone: " + e.Message);
            }
            await Task.Delay(_poll, ct);
        }
    }

    // ------------------------------------------------------------------ posa dei file

    private sealed record Output(string Name, string Source, bool OriginalOfRemote, Staged Staged);

    /// <summary>
    /// Mette i file al loro posto. Un elemento usa lo stesso suffisso per tutti i suoi file
    /// (IMG_1234 (2).HEIC e IMG_1234 (2).MOV restano accoppiati). Un file identico già presente
    /// non si duplica.
    /// </summary>
    private void Finalize(List<Staged> staged, string folder, TransferOptions o)
    {
        var outputs = new List<Output>();
        foreach (var s in staged)
        {
            var name = DestinationPlanner.SafeFileName(s.File.File.Name);
            bool isHeic = s.File.File.Extension is "HEIC" or "HEIF";
            bool convert = o.ConvertHeic && isHeic && _converter is not null && s.File.Role is FileRole.Primary or FileRole.Edited;
            if (convert)
            {
                var jpgPath = s.Path + ".jpg";
                try
                {
                    _converter!.HeicToJpeg(s.Path, jpgPath, o.JpegQuality);
                    outputs.Add(new Output(Path.ChangeExtension(name, ".JPG"), jpgPath, OriginalOfRemote: !o.KeepHeicOriginal, s));
                    if (o.KeepHeicOriginal) outputs.Add(new Output(name, s.Path, OriginalOfRemote: true, s));
                    continue;
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    TryDelete(jpgPath);
                    _report.ConversionWarnings.Add($"{s.File.File.Name}: {e.Message}");
                    _log?.Warn($"Conversione JPG non riuscita per {s.File.File.Name}, copio l'originale: {e.Message}");
                }
            }
            outputs.Add(new Output(name, s.Path, OriginalOfRemote: true, s));
        }

        // Suffisso comune: il primo n per cui ogni file è libero o già identico.
        var hashes = new Dictionary<string, string>();
        string Hash(string p) => hashes.TryGetValue(p, out var h) ? h : hashes[p] = HashFile(p);
        for (int n = 1; ; n++)
        {
            bool fits = true;
            foreach (var out_ in outputs)
            {
                var target = Path.Combine(folder, DestinationPlanner.WithSuffix(out_.Name, n));
                if (!File.Exists(target)) continue;
                var srcLen = new FileInfo(out_.Source).Length;
                if (new FileInfo(target).Length == srcLen && Hash(target) == Hash(out_.Source)) continue;
                fits = false;
                break;
            }
            if (!fits) continue;

            foreach (var out_ in outputs)
            {
                var target = Path.Combine(folder, DestinationPlanner.WithSuffix(out_.Name, n));
                long size = new FileInfo(out_.Source).Length;
                string sha = out_.Source == out_.Staged.Path ? out_.Staged.Sha256 : Hash(out_.Source);
                if (File.Exists(target))
                {
                    TryDelete(out_.Source); // identico già presente
                }
                else
                {
                    File.Move(out_.Source, target, overwrite: false);
                    // Data del file sul PC = data dello scatto (comoda in Esplora file).
                    TrySetTimes(target, out_.Staged.File.File);
                }
                if (out_.OriginalOfRemote)
                {
                    _registry.Record(_device.Key, out_.Staged.File.File.Key, target, size, sha);
                    _verified.Add(out_.Staged.File.File.Key);
                }
            }
            return;
        }
    }

    private static void TrySetTimes(string path, RemoteFile f)
    {
        try
        {
            if (f.Modified is { } m) File.SetLastWriteTime(path, m);
            if (f.Created is { } c) File.SetCreationTime(path, c);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    // ------------------------------------------------------------------ cancellazione dal telefono

    /// <summary>
    /// Solo cancellazione dal telefono (comando "Elimina"): niente copia, niente controllo che siano sul PC.
    /// L'utente ha già confermato sapendo quanti elementi non hanno una copia.
    /// </summary>
    public async Task<TransferReport> DeleteAsync(IReadOnlyList<MediaItem> items, TransferOptions o,
        IProgress<TransferStatus>? progress, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        WithTiming(o);
        _report = new TransferReport { Mode = TransferMode.Delete };
        _progress = progress;
        _ids.Clear();
        foreach (var f in items.SelectMany(i => i.Files)) _ids[f.File.Key] = f.File.ObjectId;
        try
        {
            await DeleteItemsAsync(items, mustBeOnPc: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _report.Canceled = true;
            _log?.Info("Eliminazione annullata dall'utente");
        }
        finally
        {
            _report.Elapsed = clock.Elapsed;
            SetPhase(TransferPhase.Done, null);
        }
        _log?.Info($"Eliminazione: {_report.Deleted} tolti dal telefono, {_report.DeleteFailed} non tolti" +
                   (_report.DeleteRefused ? " (rifiutata dall'iPhone)" : ""));
        return _report;
    }

    /// <param name="mustBeOnPc">Con Sposta: ultimo controllo che la copia sul PC ci sia ancora, intera.</param>
    private async Task DeleteItemsAsync(IReadOnlyList<MediaItem> items, Func<MediaItem, bool>? mustBeOnPc, CancellationToken ct)
    {
        var deleting = mustBeOnPc is null ? "Tolgo dal telefono gli elementi scelti…" : "Tolgo dal telefono gli elementi copiati e verificati…";
        _itemsTotal = items.Count;
        _itemsDone = 0;
        SetPhase(TransferPhase.Deleting, deleting);
        var queue = new Queue<MediaItem>(items);
        var retries = new Dictionary<MediaItem, int>();
        bool probed = false;
        bool waitingUnlock = false;

        // Al terzo errore lo stesso elemento resta sul telefono. L'attesa dello sblocco non conta come errore.
        void Requeue(IEnumerable<MediaItem> again, bool countAsError)
        {
            foreach (var item in again)
            {
                var n = countAsError ? (retries[item] = retries.GetValueOrDefault(item) + 1) : retries.GetValueOrDefault(item);
                if (n < 3) queue.Enqueue(item);
                else _report.DeleteFailed++;
            }
        }

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            // Il primo da solo: se iOS rifiuta, ci si ferma subito senza insistere.
            int take = probed ? Math.Min(20, queue.Count) : 1;
            var chunk = Enumerable.Range(0, take).Select(_ => queue.Dequeue()).ToList();

            var deletable = mustBeOnPc is null ? chunk : chunk.Where(mustBeOnPc).ToList();
            foreach (var skipped in chunk.Except(deletable))
            {
                _report.DeleteFailed++;
                _log?.Warn($"Non cancellato {skipped}: la copia sul PC non risulta completa");
            }
            if (deletable.Count == 0) continue;

            // Tutti i file dell'elemento, anche quelli non copiati (.AAE): sul telefono l'elemento è uno solo.
            // Prima il principale: spesso iOS toglie da solo gli altri, che risultano poi "non trovati" (va bene).
            var ids = new List<(MediaItem Item, MediaFile File, string Id)>();
            foreach (var item in deletable)
                foreach (var f in item.Files.OrderBy(x => x.Role))
                    if (_ids.TryGetValue(f.File.Key, out var id)) ids.Add((item, f, id));

            IReadOnlyList<DeleteResult> results;
            try
            {
                results = await _conn.DeleteAsync(ids.Select(x => x.Id).ToList(), ct);
            }
            catch (DeviceException e) when (e.Kind == DeviceErrorKind.Gone)
            {
                // Scollegato: si aspetta che torni e si riprova.
                _log?.Warn("Cancellazione: " + e.Message);
                await WaitForDeviceAsync(ct);
                SetPhase(TransferPhase.Deleting, deleting);
                Requeue(deletable, countAsError: true);
                continue;
            }
            catch (DeviceException e)
            {
                // Il telefono non ha risposto alla cancellazione (WPD bloccato): riprovare vorrebbe dire minuti
                // di attesa per niente. Ci si ferma e si dice come fare.
                _log?.Warn("Cancellazione interrotta: " + e.Message);
                _report.DeleteProblem ??= "il telefono non ha risposto alla cancellazione: eliminale dall'app Foto (hanno il bollino ✓ PC)";
                _report.DeleteFailed += deletable.Count + queue.Count;
                return;
            }

            var byId = results.GroupBy(r => r.ObjectId).ToDictionary(g => g.Key, g => g.Last());

            // iPhone bloccato: Windows non ne mostra le foto finché non si sblocca (il canale Apple sì, per questo
            // l'app le vede). Si aspetta lo sblocco senza contare errori, come per la copia.
            if (results.Count > 0 && results.All(r => r.HResult == DeviceErrors.LockedForDelete))
            {
                if (!waitingUnlock) _log?.Info("Eliminazione: l'iPhone è bloccato, aspetto che venga sbloccato");
                waitingUnlock = true;
                SetPhase(TransferPhase.WaitingForDevice, "Sblocca l'iPhone per eliminare: si riprende da solo");
                Requeue(deletable, countAsError: false);
                await Task.Delay(_poll, ct);
                continue;
            }
            if (waitingUnlock)
            {
                waitingUnlock = false;
                _log?.Info("iPhone sbloccato: riprendo l'eliminazione");
                SetPhase(TransferPhase.Deleting, deleting);
            }

            var failed = new List<(MediaItem Item, bool Refused)>();
            foreach (var item in deletable)
            {
                var mine = ids.Where(x => x.Item == item).ToList();
                bool primaryOk = mine.Where(x => x.File.Role == FileRole.Primary)
                    .All(x => byId.TryGetValue(x.Id, out var r) && (r.Ok || DeviceErrors.IsNotFound(r.HResult)));
                if (primaryOk)
                {
                    _report.Deleted++;
                    _report.Removed.Add(item);
                    foreach (var x in mine) _registry.MarkDeleted(_device.Key, x.File.File.Key);
                }
                else
                {
                    failed.Add((item, mine.Any(x => byId.TryGetValue(x.Id, out var r) && r.Refused)));
                    var bad = mine.Where(x => x.File.Role == FileRole.Primary)
                        .Select(x => byId.TryGetValue(x.Id, out var r) ? r : null).FirstOrDefault(r => r is { Ok: false });
                    _report.DeleteProblem ??= bad is null ? "il telefono non ha risposto" : DeviceErrors.Describe(bad.HResult);
                }
            }

            // Problemi che valgono per tutti (Windows non vede le foto, più iPhone collegati): inutile insistere.
            var stop = deletable.SelectMany(i => ids.Where(x => x.Item == i).Select(x => x.Id))
                .Select(id => byId.TryGetValue(id, out var r) ? r : null)
                .FirstOrDefault(r => r is { Ok: false } && DeviceErrors.StopsDeletion(r.HResult));
            if (stop is not null && _report.Deleted == 0 && failed.Count == deletable.Count)
            {
                _report.DeleteProblem ??= DeviceErrors.Describe(stop.HResult);
                _report.DeleteFailed += failed.Count + queue.Count;
                _log?.Warn("Cancellazione non possibile: " + _report.DeleteProblem + (stop.Detail is { } why ? $" [{why}]" : ""));
                return;
            }

            if (failed.Count > 0)
            {
                // Un iPhone che si è bloccato a metà rifiuta come uno che non lo permette mai:
                // se era bloccato si aspetta lo sblocco e si riprova, senza concludere niente.
                if (await WaitIfLockedAsync(ct))
                {
                    SetPhase(TransferPhase.Deleting, deleting);
                    Requeue(failed.Select(f => f.Item), countAsError: false);
                    // Se non è passato niente, il prossimo giro rifà la prova con un elemento solo.
                    if (failed.Count == deletable.Count && !probed)
                    {
                        _itemsDone = Math.Min(_itemsTotal, _report.Deleted + _report.DeleteFailed);
                        Report(force: true);
                        continue;
                    }
                }
                else if (!probed && failed.Any(f => f.Refused))
                {
                    _report.DeleteRefused = true;
                    _report.DeleteFailed += failed.Count + queue.Count;
                    _log?.Warn("L'iPhone rifiuta la cancellazione da Windows: mi fermo" + (mustBeOnPc is null ? "" : ", le copie sul PC restano"));
                    return;
                }
                else
                {
                    _report.DeleteFailed += failed.Count;
                }
            }
            probed = true;
            _itemsDone = Math.Min(_itemsTotal, _report.Deleted + _report.DeleteFailed);
            Report(force: true);
        }
    }

    // ------------------------------------------------------------------ utilità

    private void PrepareStaging(string staging)
    {
        Directory.CreateDirectory(staging);
        try { File.SetAttributes(staging, File.GetAttributes(staging) | FileAttributes.Hidden); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // Avanzi di una copia interrotta (PC spento, app chiusa): file a metà, mai definitivi.
        foreach (var f in Directory.EnumerateFiles(staging)) TryDelete(f);
    }

    private static void CleanStaging(string staging)
    {
        try
        {
            if (!Directory.Exists(staging)) return;
            foreach (var f in Directory.EnumerateFiles(staging)) TryDelete(f);
            if (!Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void CheckFreeSpace(string root, long needed, bool doubleForJpeg)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
            if (!drive.IsReady) return;
            // Margine: 10% e almeno 200 MB, più i JPG se si tengono anche gli originali.
            long need = needed + Math.Max(needed / 10, 200L * 1024 * 1024) + (doubleForJpeg ? needed / 3 : 0);
            if (drive.AvailableFreeSpace < need)
                throw new TransferAbortedException(
                    $"Spazio insufficiente su {drive.Name}: servono {Format(need)}, liberi {Format(drive.AvailableFreeSpace)}");
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static bool IsDiskFull(IOException e) =>
        e.HResult is DeviceErrors.DiskFull or DeviceErrors.HandleDiskFull;

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
    }

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void SetPhase(TransferPhase phase, string? notice)
    {
        _phase = phase;
        _notice = notice;
        Report(force: true);
    }

    private void Report(bool force = false)
    {
        if (_progress is null) return;
        var now = Stopwatch.GetTimestamp();
        if (!force && Stopwatch.GetElapsedTime(_lastReport, now) < TimeSpan.FromMilliseconds(100)) return;
        _lastReport = now;

        long done = _bytesDone + _currentFileBytes;
        long t0, b0;
        lock (_samples)
        {
            _samples.Enqueue((now, done));
            while (_samples.Count > 2 && Stopwatch.GetElapsedTime(_samples.Peek().Ticks, now) > TimeSpan.FromSeconds(6))
                _samples.Dequeue();
            (t0, b0) = _samples.Peek();
        }
        var secs = Stopwatch.GetElapsedTime(t0, now).TotalSeconds;
        double speed = secs > 0.5 ? Math.Max(0, (done - b0) / secs) : 0;
        TimeSpan? remaining = speed > 1 && _bytesTotal > done ? TimeSpan.FromSeconds((_bytesTotal - done) / speed) : null;

        _progress.Report(new TransferStatus(_phase, _itemsDone, _itemsTotal, Math.Min(done, Math.Max(_bytesTotal, done)), _bytesTotal,
            speed, remaining, _currentFile, _currentFolder, _report.Copied, _report.Skipped, _report.Failed.Count, _report.Retries, _notice));
    }
}
