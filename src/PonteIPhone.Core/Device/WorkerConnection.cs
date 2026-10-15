using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Device;

/// <summary>Avvia il processo separato che parla con l'iPhone.</summary>
public interface IWorkerLauncher
{
    IWorkerProcess Start(string pipeName);
}

public interface IWorkerProcess : IDisposable
{
    bool HasExited { get; }
    void Kill();
}

/// <summary>Lo stesso eseguibile dell'app, avviato con "--worker".</summary>
public sealed class ProcessWorkerLauncher(string exePath) : IWorkerLauncher
{
    public IWorkerProcess Start(string pipeName)
    {
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory,
        };
        info.ArgumentList.Add("--worker");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        var p = Process.Start(info) ?? throw new DeviceException("Impossibile avviare il processo per l'iPhone");
        return new OsProcess(p);
    }

    private sealed class OsProcess(Process p) : IWorkerProcess
    {
        public bool HasExited
        {
            get { try { return p.HasExited; } catch (InvalidOperationException) { return true; } }
        }

        public void Kill()
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose() => p.Dispose();
    }
}

/// <summary>Tempi oltre i quali una chiamata senza segni di vita fa riavviare il processo.</summary>
public sealed record WatchdogTimes(TimeSpan Short, TimeSpan Long)
{
    /// <summary>
    /// Valori scelti per l'iPhone: una miniatura arriva in decimi di secondo, un blocco di un video
    /// in meno di un secondo. 60 s senza un byte vuol dire che il driver è bloccato (come in Esplora file).
    /// </summary>
    public static readonly WatchdogTimes Default = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
}

/// <summary>
/// Lato "app" del collegamento con il processo separato. Ogni chiamata ha un watchdog: se il processo
/// non dà segni di vita entro il tempo previsto, viene chiuso, le chiamate in corso falliscono con
/// <see cref="WorkerRestartedException"/> (chi copia riprova il file) e alla chiamata successiva
/// riparte un processo nuovo, che riapre da solo il telefono che era aperto.
/// </summary>
public sealed class WorkerConnection : IDeviceConnection
{
    private readonly IWorkerLauncher _launcher;
    private readonly WatchdogTimes _times;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly Timer _watchdog;
    private long _nextId;
    private Session? _session;
    private string? _openDeviceId;
    private bool _disposed;

    /// <summary>Quante volte il watchdog ha dovuto riavviare il processo (per la diagnostica).</summary>
    public int Restarts { get; private set; }

    public WorkerConnection(IWorkerLauncher launcher, WatchdogTimes? times = null, Action<string>? log = null)
    {
        _launcher = launcher;
        _times = times ?? WatchdogTimes.Default;
        _log = log;
        _watchdog = new Timer(_ => CheckHangs(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private sealed class Session(IWorkerProcess process, NamedPipeServerStream pipe, StreamWriter writer)
    {
        public IWorkerProcess Process { get; } = process;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public StreamWriter Writer { get; } = writer;
        public bool Dead { get; set; }
        /// <summary>Telefono aperto in questo processo (dopo un riavvio va riaperto).</summary>
        public string? OpenedDevice { get; set; }
    }

    private sealed class Pending(TimeSpan timeout, Session session)
    {
        public TaskCompletionSource<JsonElement?> Tcs { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Session Session { get; } = session;
        public TimeSpan Timeout { get; } = timeout;
        public long LastActivity = Stopwatch.GetTimestamp();
        /// <summary>
        /// Il processo la sta eseguendo. Una richiesta ancora in coda dietro un'altra lunga (es. una miniatura
        /// dietro l'elenco) non è un blocco: senza questo il watchdog chiudeva processi che lavoravano.
        /// </summary>
        public volatile bool Started;
        public Action<JsonElement>? OnBatch { get; init; }
        public Action<JsonElement>? OnProgress { get; init; }
        public void Touch() => Interlocked.Exchange(ref LastActivity, Stopwatch.GetTimestamp());
        public TimeSpan Idle => Stopwatch.GetElapsedTime(Interlocked.Read(ref LastActivity));
    }

    // ---------------------------------------------------------------- avvio e lettura

    private async Task<Session> EnsureSessionAsync(CancellationToken ct)
    {
        var s = _session;
        if (s is { Dead: false }) return s;
        await _startLock.WaitAsync(ct);
        try
        {
            s = _session;
            if (s is { Dead: false }) return s;
            ObjectDisposedException.ThrowIf(_disposed, this);

            var name = "PonteIPhone-" + Guid.NewGuid().ToString("N");
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            IWorkerProcess process;
            try
            {
                process = _launcher.Start(name);
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await pipe.WaitForConnectionAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill();
                process.Dispose();
                await pipe.DisposeAsync();
                throw new DeviceException("Il processo per l'iPhone non è partito");
            }
            catch
            {
                process.Kill();
                process.Dispose();
                await pipe.DisposeAsync();
                throw;
            }

            var writer = new StreamWriter(pipe, WorkerProtocol.Utf8) { AutoFlush = true, NewLine = "\n" };
            s = new Session(process, pipe, writer);
            _session = s;
            var session = s;
            _ = Task.Run(() => ReadLoop(session));
            _log?.Invoke("Processo per l'iPhone avviato");
            return s;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task ReadLoop(Session s)
    {
        using var reader = new StreamReader(s.Pipe, WorkerProtocol.Utf8);
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                WireMessage? msg;
                try { msg = JsonSerializer.Deserialize<WireMessage>(line, WorkerProtocol.Json); }
                catch (JsonException) { continue; }
                if (msg is null || !_pending.TryGetValue(msg.Id, out var p)) continue;
                p.Touch();
                switch (msg.Kind)
                {
                    case WorkerProtocol.KindStarted:
                        p.Started = true;
                        break;
                    case WorkerProtocol.KindBatch:
                        if (msg.Data is { } b) p.OnBatch?.Invoke(b);
                        break;
                    case WorkerProtocol.KindProgress:
                        if (msg.Data is { } d) p.OnProgress?.Invoke(d);
                        break;
                    case WorkerProtocol.KindOk:
                        if (_pending.TryRemove(msg.Id, out _)) p.Tcs.TrySetResult(msg.Data);
                        break;
                    case WorkerProtocol.KindCanceled:
                        if (_pending.TryRemove(msg.Id, out _)) p.Tcs.TrySetCanceled();
                        break;
                    case WorkerProtocol.KindError:
                        if (_pending.TryRemove(msg.Id, out _))
                            p.Tcs.TrySetException(new DeviceException(msg.Error ?? DeviceErrors.Describe(msg.Code), msg.Code));
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        Kill(s, "il processo per l'iPhone si è chiuso");
    }

    // ---------------------------------------------------------------- watchdog

    private void CheckHangs()
    {
        foreach (var p in _pending.Values)
        {
            if (p.Started && p.Idle > p.Timeout && !p.Session.Dead)
            {
                _log?.Invoke($"Nessuna risposta dall'iPhone da {p.Idle.TotalSeconds:0} s: riavvio il collegamento");
                Restarts++;
                Kill(p.Session, "l'iPhone non rispondeva");
            }
        }
    }

    private void Kill(Session s, string reason)
    {
        lock (s)
        {
            if (s.Dead) return;
            s.Dead = true;
        }
        try { s.Process.Kill(); } catch { }
        try { s.Pipe.Dispose(); } catch { }
        try { s.Process.Dispose(); } catch { }
        foreach (var (id, p) in _pending)
        {
            if (p.Session == s && _pending.TryRemove(id, out _))
                p.Tcs.TrySetException(new WorkerRestartedException("Collegamento con l'iPhone riavviato: " + reason));
        }
    }

    /// <summary>
    /// Chiude il processo attuale: la chiamata successiva ne avvia uno nuovo, con un collegamento WPD
    /// fresco. Serve quando il telefono sembra bloccato da troppo tempo anche se è già sbloccato
    /// (Windows a volte continua a mostrare la memoria vuota alla stessa sessione). Da chiamare senza chiamate in corso.
    /// </summary>
    public void Restart(string reason)
    {
        if (_session is { Dead: false } s)
        {
            _log?.Invoke("Nuovo processo per l'iPhone: " + reason);
            Kill(s, reason);
        }
    }

    // ---------------------------------------------------------------- chiamate

    private async Task<JsonElement?> CallAsync(WireRequest req, TimeSpan timeout, CancellationToken ct,
        Action<JsonElement>? onBatch = null, Action<JsonElement>? onProgress = null)
    {
        var s = await EnsureSessionAsync(ct);

        // Processo nuovo dopo un riavvio: riapre il telefono che l'app stava usando.
        if (req.Op is not (WorkerProtocol.OpOpen or WorkerProtocol.OpList) && _openDeviceId is { } dev && s.OpenedDevice != dev)
        {
            var reopen = new WireRequest { Op = WorkerProtocol.OpOpen, DeviceId = dev };
            var opened = await SendAsync(s, reopen, _times.Long, ct, null, null);
            var result = opened?.Deserialize<OpenResult>(WorkerProtocol.Json);
            if (result is null || !result.Ready)
                throw new DeviceException("L'iPhone non è più accessibile: sbloccalo e ricollegalo", DeviceErrors.NotReady);
            s.OpenedDevice = dev;
        }

        var data = await SendAsync(s, req, timeout, ct, onBatch, onProgress);
        if (req.Op == WorkerProtocol.OpOpen) s.OpenedDevice = req.DeviceId;
        return data;
    }

    private async Task<JsonElement?> SendAsync(Session s, WireRequest req, TimeSpan timeout, CancellationToken ct,
        Action<JsonElement>? onBatch, Action<JsonElement>? onProgress)
    {
        req.Id = Interlocked.Increment(ref _nextId);
        var p = new Pending(timeout, s) { OnBatch = onBatch, OnProgress = onProgress };
        _pending[req.Id] = p;
        var line = JsonSerializer.Serialize(req, WorkerProtocol.Json);
        try
        {
            lock (_writeLock) s.Writer.WriteLine(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            _pending.TryRemove(req.Id, out _);
            Kill(s, "pipe chiusa");
            throw new WorkerRestartedException("Collegamento con l'iPhone interrotto");
        }

        // Annullamento: il processo interrompe l'operazione e risponde "canceled". Se dentro il driver
        // non può (chiamata bloccata), dopo 3 secondi si chiude il processo: "Annulla" deve annullare davvero.
        using var reg = ct.Register(() =>
        {
            var cancel = JsonSerializer.Serialize(new WireRequest { Op = WorkerProtocol.OpCancel, Target = req.Id }, WorkerProtocol.Json);
            try { lock (_writeLock) s.Writer.WriteLine(cancel); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
            _ = Task.Delay(CancelGrace).ContinueWith(_ =>
            {
                if (!_pending.TryRemove(req.Id, out var stuck)) return;
                stuck.Tcs.TrySetCanceled();
                _log?.Invoke("Annullamento: il processo non rispondeva, lo chiudo");
                Kill(s, "annullato");
            }, TaskScheduler.Default);
        });
        return await p.Tcs.Task;
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        var data = await CallAsync(new WireRequest { Op = WorkerProtocol.OpList }, _times.Short, ct);
        return data?.Deserialize<List<DeviceInfo>>(WorkerProtocol.Json) ?? [];
    }

    public async Task<OpenResult> OpenAsync(string deviceId, CancellationToken ct)
    {
        var data = await CallAsync(new WireRequest { Op = WorkerProtocol.OpOpen, DeviceId = deviceId }, _times.Long, ct);
        var result = data?.Deserialize<OpenResult>(WorkerProtocol.Json) ?? throw new DeviceException("Risposta vuota all'apertura");
        _openDeviceId = result.Ready ? deviceId : null;
        return result;
    }

    public async Task<EnumerateSummary> EnumerateAsync(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct)
    {
        var data = await CallAsync(new WireRequest { Op = WorkerProtocol.OpEnumerate }, _times.Long, ct,
            onBatch: e => batch(e.Deserialize<List<RemoteFile>>(WorkerProtocol.Json) ?? []));
        return data is { ValueKind: JsonValueKind.Object } d
            ? d.Deserialize<EnumerateSummary>(WorkerProtocol.Json) ?? new EnumerateSummary(0, 0)
            : new EnumerateSummary(0, 0);
    }

    public async Task<byte[]?> GetThumbnailAsync(string objectId, CancellationToken ct)
    {
        var data = await CallAsync(new WireRequest { Op = WorkerProtocol.OpThumbnail, ObjectId = objectId }, _times.Short, ct);
        return data is { ValueKind: JsonValueKind.String } d ? d.GetBytesFromBase64() : null;
    }

    public async Task<DownloadResult> DownloadAsync(string objectId, string destination, Action<long>? progress, CancellationToken ct)
    {
        var data = await CallAsync(new WireRequest { Op = WorkerProtocol.OpDownload, ObjectId = objectId, Destination = destination },
            _times.Long, ct, onProgress: e => { if (e.ValueKind == JsonValueKind.Number) progress?.Invoke(e.GetInt64()); });
        return data?.Deserialize<DownloadResult>(WorkerProtocol.Json) ?? throw new DeviceException("Risposta vuota allo scaricamento");
    }

    /// <summary>Quanto aspettare la risposta a un annullamento prima di chiudere il processo.</summary>
    public static TimeSpan CancelGrace { get; set; } = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<string> objectIds, CancellationToken ct)
    {
        // La prima volta Windows rilegge il telefono per ritrovare i file: tempo fisso più qualche secondo a file.
        var timeout = TimeSpan.FromSeconds(90) + TimeSpan.FromSeconds(3 * objectIds.Count);
        var data = await CallAsync(new WireRequest { Op = WorkerProtocol.OpDelete, Ids = objectIds.ToArray() }, timeout, ct);
        return data?.Deserialize<List<DeleteResult>>(WorkerProtocol.Json) ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _watchdog.DisposeAsync();
        if (_session is { } s) Kill(s, "app chiusa");
        _startLock.Dispose();
    }
}
