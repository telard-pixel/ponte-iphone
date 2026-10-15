using System.Collections.Concurrent;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Device;

/// <summary>
/// Esegue le chiamate al telefono una alla volta su un thread dedicato (MTA, come vuole WPD).
/// Lo usano il processo separato che parla con l'iPhone e la modalità dimostrativa.
/// Qui non c'è il watchdog: un blocco del driver bloccherebbe questo thread, per questo
/// l'app usa <see cref="WorkerConnection"/>, che mette tutto in un altro processo.
/// </summary>
public sealed class InProcessConnection : IDeviceConnection
{
    private readonly IDeviceBackend _backend;
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public InProcessConnection(IDeviceBackend backend, string name = "iPhone")
    {
        _backend = backend;
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        if (OperatingSystem.IsWindows()) _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            work();
        }
        _backend.Dispose();
    }

    public Task<T> Run<T>(Func<IDeviceBackend, T> call, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // L'annullamento passa dal backend (che controlla il token): la chiamata finisce solo quando
        // il backend ha davvero smesso, così nessun file resta aperto dopo.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try
            {
                if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
                tcs.TrySetResult(call(_backend));
            }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception e) { tcs.TrySetException(e); }
        });
        return tcs.Task;
    }

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct) => Run(b => b.ListDevices(), ct);

    public Task<OpenResult> OpenAsync(string deviceId, CancellationToken ct) => Run(b => b.Open(deviceId), ct);

    public Task<EnumerateSummary> EnumerateAsync(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct) =>
        Run(b =>
        {
            long total = 0;
            b.Enumerate(files => { total += files.Count; batch(files); }, ct);
            return new EnumerateSummary(total, b.CloudOnlyFiles, b.LastEnumerationSummary);
        }, ct);

    public Task<byte[]?> GetThumbnailAsync(string objectId, CancellationToken ct) => Run(b => b.GetThumbnail(objectId), ct);

    public Task<DownloadResult> DownloadAsync(string objectId, string destination, Action<long>? progress, CancellationToken ct) =>
        Run(b => b.Download(objectId, destination, progress ?? (_ => { }), ct), ct);

    public Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<string> objectIds, CancellationToken ct) =>
        Run(b => b.Delete(objectIds), ct);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _queue.CompleteAdding();
        return ValueTask.CompletedTask;
    }
}
