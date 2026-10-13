using System.IO.Pipes;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Tests;

/// <summary>
/// Il collegamento con il processo separato, con un "processo" finto nello stesso test:
/// protocollo, annullamento e soprattutto il watchdog che riavvia quando il driver si blocca.
/// </summary>
public sealed class WorkerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ponte-worker-").FullName;

    private sealed class ThreadLauncher(Func<FakeBackend> factory) : IWorkerLauncher
    {
        public int Started { get; private set; }
        public List<FakeBackend> Backends { get; } = [];

        public IWorkerProcess Start(string pipeName)
        {
            Started++;
            var backend = factory();
            Backends.Add(backend);
            var stop = new CancellationTokenSource();
            backend.HangRelease = stop.Token;
            var proc = new ThreadProcess(stop);
            var t = new Thread(() =>
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    pipe.Connect(10_000);
                    WorkerHost.Serve(pipe, backend, stop.Token);
                }
                catch (Exception) { }
                finally { proc.Exited = true; }
            }) { IsBackground = true };
            t.Start();
            return proc;
        }
    }

    private sealed class ThreadProcess(CancellationTokenSource stop) : IWorkerProcess
    {
        public bool Exited { get; set; }
        public bool HasExited => Exited;
        public void Kill() { try { stop.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() { }
    }

    private static FakeBackend NewFake()
    {
        var f = new FakeBackend();
        f.Add("202610_a", "IMG_0001.HEIC", 300_000, new DateTime(2026, 10, 1), thumbnail: [1, 2, 3, 4]);
        f.Add("202610_a", "IMG_0001.MOV", 400_000, new DateTime(2026, 10, 1));
        for (int i = 2; i < 700; i++) f.Add("202610_a", $"IMG_{i:0000}.JPG", 1000, new DateTime(2026, 10, 1));
        return f;
    }

    [Fact]
    public async Task ProtocolCarriesEverything()
    {
        var launcher = new ThreadLauncher(NewFake);
        await using var conn = new WorkerConnection(launcher);

        var devices = await conn.ListDevicesAsync(CancellationToken.None);
        var dev = Assert.Single(devices);
        var opened = await conn.OpenAsync(dev.Id, CancellationToken.None);
        Assert.True(opened.Ready);
        Assert.Equal(128_000_000_000, opened.Storage!.Capacity);

        var files = new List<RemoteFile>();
        await conn.EnumerateAsync(b => files.AddRange(b), CancellationToken.None);
        Assert.Equal(700, files.Count);
        Assert.Equal(new DateTime(2026, 10, 1), files[0].Created);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await conn.GetThumbnailAsync(files[0].ObjectId, CancellationToken.None));
        Assert.Null(await conn.GetThumbnailAsync(files[1].ObjectId, CancellationToken.None));

        var path = Path.Combine(_dir, "x.part");
        long lastProgress = 0;
        var res = await conn.DownloadAsync(files[1].ObjectId, path, p => lastProgress = p, CancellationToken.None);
        Assert.Equal(400_000, res.Bytes);
        Assert.Equal(launcher.Backends[0].Files[1].Content(), File.ReadAllBytes(path));

        var del = await conn.DeleteAsync([files[2].ObjectId, "inesistente"], CancellationToken.None);
        Assert.True(del[0].Ok);
        Assert.False(del[1].Ok);
        Assert.True(DeviceErrors.IsNotFound(del[1].HResult));
        Assert.Equal(1, launcher.Started);
    }

    [Fact]
    public async Task ErrorsArriveWithTheirCode()
    {
        var launcher = new ThreadLauncher(NewFake);
        await using var conn = new WorkerConnection(launcher);
        var dev = (await conn.ListDevicesAsync(CancellationToken.None))[0];
        await conn.OpenAsync(dev.Id, CancellationToken.None);
        launcher.Backends[0].FailDownloads = 1;

        var e = await Assert.ThrowsAsync<DeviceException>(() =>
            conn.DownloadAsync(launcher.Backends[0].Files[0].Info.ObjectId, Path.Combine(_dir, "a.part"), null, CancellationToken.None));
        Assert.Equal(DeviceErrors.GenFailure, e.Code);
        Assert.Equal(DeviceErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task HungDriverIsKilledAndTheNextCallWorks()
    {
        var launcher = new ThreadLauncher(() =>
        {
            var f = NewFake();
            return f;
        });
        await using var conn = new WorkerConnection(launcher, new WatchdogTimes(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)));
        var dev = (await conn.ListDevicesAsync(CancellationToken.None))[0];
        await conn.OpenAsync(dev.Id, CancellationToken.None);
        launcher.Backends[0].HangDownloads = 1;
        var id = launcher.Backends[0].Files[1].Info.ObjectId;

        var started = DateTime.UtcNow;
        await Assert.ThrowsAsync<WorkerRestartedException>(() =>
            conn.DownloadAsync(id, Path.Combine(_dir, "b.part"), null, CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        Assert.Equal(1, conn.Restarts);

        // Processo nuovo, telefono riaperto da solo, scaricamento riuscito.
        var res = await conn.DownloadAsync(id, Path.Combine(_dir, "c.part"), null, CancellationToken.None);
        Assert.Equal(400_000, res.Bytes);
        Assert.Equal(2, launcher.Started);
    }

    [Fact]
    public async Task CancelStopsADownload()
    {
        var launcher = new ThreadLauncher(() =>
        {
            var f = NewFake();
            f.BytesPerSecond = 50_000; // 8 secondi per il video
            return f;
        });
        await using var conn = new WorkerConnection(launcher);
        var dev = (await conn.ListDevicesAsync(CancellationToken.None))[0];
        await conn.OpenAsync(dev.Id, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            conn.DownloadAsync(launcher.Backends[0].Files[1].Info.ObjectId, Path.Combine(_dir, "d.part"), null, cts.Token));

        // Il processo resta lo stesso e risponde ancora.
        Assert.Single(await conn.ListDevicesAsync(CancellationToken.None));
        Assert.Equal(1, launcher.Started);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
