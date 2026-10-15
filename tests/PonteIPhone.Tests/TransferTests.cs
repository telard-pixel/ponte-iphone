using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;
using PonteIPhone.Core.Transfer;

namespace PonteIPhone.Tests;

/// <summary>Il motore di copia contro il telefono finto, con i guasti veri degli iPhone su Windows.</summary>
public sealed class TransferTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ponte-test-").FullName;
    private readonly FakeBackend _fake = new();
    private readonly InProcessConnection _conn;
    private readonly CopyRegistry _registry;
    private static readonly DateTime Oct = new(2026, 10, 12, 21, 0, 0);
    private static readonly DateTime Sep = new(2026, 9, 3, 9, 30, 0);

    public TransferTests()
    {
        _conn = new InProcessConnection(_fake);
        _registry = new CopyRegistry(Path.Combine(_dir, "copie.db"));
    }

    private string Dest => Path.Combine(_dir, "Foto");

    private TransferOptions Options(Action<TransferOptions>? _ = null) => new()
    {
        DestinationRoot = Dest,
        RetryDelays = [TimeSpan.FromMilliseconds(10)],
        DevicePollInterval = TimeSpan.FromMilliseconds(50),
        MaxAttempts = 4,
    };

    private async Task<List<MediaItem>> LoadAsync()
    {
        await _conn.OpenAsync(_fake.Device.Id, CancellationToken.None);
        var files = new List<RemoteFile>();
        await _conn.EnumerateAsync(b => files.AddRange(b), CancellationToken.None);
        return MediaGrouper.Group(files);
    }

    private TransferEngine Engine() => new(_conn, _fake.Device, _registry, converter: null);

    private string Month(DateTime d) => Path.Combine(Dest, d.Year.ToString(), DestinationPlanner.MonthFolder(d.Month));

    [Fact]
    public async Task CopyPutsFilesInYearMonthAndRecordsThem()
    {
        _fake.Add("202610_a", "IMG_0001.HEIC", 300_000, Oct);
        _fake.Add("202610_a", "IMG_0001.MOV", 900_000, Oct);
        _fake.Add("202609_a", "IMG_0002.MOV", 2_000_000, Sep);
        var items = await LoadAsync();

        var report = await Engine().RunAsync(items, TransferMode.Copy, Options(), null, null, CancellationToken.None);

        Assert.Equal(2, report.Copied);
        Assert.Empty(report.Failed);
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001.HEIC")));
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001.MOV")));
        Assert.True(File.Exists(Path.Combine(Month(Sep), "IMG_0002.MOV")));
        Assert.Equal(_fake.Files[2].Content(), File.ReadAllBytes(Path.Combine(Month(Sep), "IMG_0002.MOV")));
        Assert.Equal(3, _registry.Load(_fake.Device.Key).Count);
        Assert.Empty(_fake.Deleted);
        // Nessun file temporaneo rimasto
        Assert.False(Directory.Exists(Path.Combine(Dest, TransferEngine.StagingFolderName)));
    }

    [Fact]
    public async Task SecondCopySkipsWhatIsAlreadyThere()
    {
        _fake.Add("202610_a", "IMG_0001.HEIC", 50_000, Oct);
        var items = await LoadAsync();
        await Engine().RunAsync(items, TransferMode.Copy, Options(), null, null, CancellationToken.None);
        var calls = _fake.DownloadCalls;

        var again = await Engine().RunAsync(items, TransferMode.Copy, Options(), null, null, CancellationToken.None);

        Assert.Equal(0, again.Copied);
        Assert.Equal(1, again.Skipped);
        Assert.Equal(calls, _fake.DownloadCalls);
    }

    [Fact]
    public async Task DriverErrorsAndTruncatedFilesAreRetried()
    {
        _fake.Add("202610_a", "IMG_0001.MOV", 1_000_000, Oct);
        _fake.FailDownloads = 1;
        _fake.TruncateDownloads = 1;
        var items = await LoadAsync();

        var report = await Engine().RunAsync(items, TransferMode.Copy, Options(), null, null, CancellationToken.None);

        Assert.Equal(1, report.Copied);
        Assert.Equal(2, report.Retries);
        Assert.Equal(_fake.Files[0].Content(), File.ReadAllBytes(Path.Combine(Month(Oct), "IMG_0001.MOV")));
    }

    [Fact]
    public async Task GivesUpAfterMaxAttemptsAndDoesNotDelete()
    {
        _fake.Add("202610_a", "IMG_0001.MOV", 100_000, Oct);
        _fake.Add("202610_a", "IMG_0002.HEIC", 100_000, Oct);
        _fake.FailDownloads = 4; // tutti i tentativi del primo file
        var items = await LoadAsync();

        var report = await Engine().RunAsync(items.OrderBy(i => i.BaseName).ToList(), TransferMode.Move, Options(), null, null, CancellationToken.None);

        Assert.Single(report.Failed);
        Assert.Equal("IMG_0001", report.Failed[0].Item.BaseName);
        Assert.Equal(["IMG_0002.HEIC"], _fake.Deleted);
        Assert.Contains(_fake.Files, f => f.Info.Name == "IMG_0001.MOV");
    }

    [Fact]
    public async Task UnplugMidFileWaitsAndResumesWithNewObjectIds()
    {
        _fake.Add("202610_a", "IMG_0001.MOV", 2_000_000, Oct);
        _fake.Add("202610_a", "IMG_0002.MOV", 2_000_000, Oct);
        _fake.DisconnectDuringDownloads = 1;
        var items = await LoadAsync();
        var statuses = new List<TransferStatus>();

        var run = Engine().RunAsync(items, TransferMode.Copy, Options(), new SyncProgress<TransferStatus>(statuses.Add), null, CancellationToken.None);
        await WaitFor(() => _fake.Unplugged);
        await Task.Delay(200);
        Assert.False(run.IsCompleted);
        await _conn.Run(b => { _fake.Replug(); return true; }, CancellationToken.None);
        var report = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, report.Copied);
        Assert.Contains(statuses, s => s.Phase == TransferPhase.WaitingForDevice);
    }

    [Fact]
    public async Task AutoLockMidFileWaitsForUnlockAndResumes()
    {
        _fake.Add("202610_a", "IMG_0001.MOV", 2_000_000, Oct);
        _fake.Add("202610_a", "IMG_0002.MOV", 2_000_000, Oct);
        _fake.LockDuringDownloads = 1;
        var items = await LoadAsync();
        var statuses = new List<TransferStatus>();

        var run = Engine().RunAsync(items, TransferMode.Copy, Options(), new SyncProgress<TransferStatus>(statuses.Add), null, CancellationToken.None);
        await WaitFor(() => _fake.Locked);
        await Task.Delay(300);
        // Bloccato: si aspetta, senza consumare tentativi e senza dare il file per rifiutato.
        Assert.False(run.IsCompleted);
        await _conn.Run(b => { _fake.Locked = false; return true; }, CancellationToken.None);
        var report = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(2, report.Copied);
        Assert.Empty(report.Failed);
        Assert.Equal(0, report.Retries);
        Assert.Contains(statuses, s => s.Phase == TransferPhase.WaitingForDevice);
    }

    [Fact]
    public async Task LockDuringMoveDeleteIsNotTakenForARefusal()
    {
        for (int i = 1; i <= 3; i++) _fake.Add("202610_a", $"IMG_000{i}.HEIC", 10_000, Oct);
        _fake.LockOnNextDelete = true;
        var items = await LoadAsync();

        var run = Engine().RunAsync(items, TransferMode.Move, Options(), null, null, CancellationToken.None);
        await WaitFor(() => _fake.Locked);
        await Task.Delay(200);
        Assert.False(run.IsCompleted);
        await _conn.Run(b => { _fake.Locked = false; return true; }, CancellationToken.None);
        var report = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(report.DeleteRefused);
        Assert.Equal(3, report.Deleted);
        Assert.Empty(_fake.Files);
    }

    [Fact]
    public async Task DeleteOnlyRemovesWithoutCopying()
    {
        _fake.Add("202610_a", "IMG_0001.HEIC", 10_000, Oct);
        _fake.Add("202610_a", "IMG_0001.MOV", 10_000, Oct);
        _fake.Add("202610_a", "IMG_0002.HEIC", 10_000, Oct);
        var items = await LoadAsync();

        var report = await Engine().DeleteAsync(items, Options(), null, CancellationToken.None);

        Assert.Equal(TransferMode.Delete, report.Mode);
        Assert.Equal(2, report.Deleted);
        Assert.Equal(2, report.Removed.Count);
        Assert.Empty(_fake.Files);
        Assert.Equal(0, _fake.DownloadCalls);
        Assert.False(Directory.Exists(Dest));
    }

    [Fact]
    public async Task DeleteOnlyRefusedStopsAtOnce()
    {
        for (int i = 1; i <= 4; i++) _fake.Add("202610_a", $"IMG_000{i}.HEIC", 10_000, Oct);
        _fake.RefuseDelete = true;
        var items = await LoadAsync();

        var report = await Engine().DeleteAsync(items, Options(), null, CancellationToken.None);

        Assert.True(report.DeleteRefused);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(4, report.DeleteFailed);
        Assert.Equal(4, _fake.Files.Count);
    }

    [Fact]
    public async Task MoveDeletesOnlyAfterVerifiedCopy()
    {
        _fake.Add("202610_a", "IMG_0001.HEIC", 80_000, Oct);
        _fake.Add("202610_a", "IMG_0001.MOV", 80_000, Oct);
        _fake.Add("202610_a", "IMG_0001.AAE", 500, Oct);
        var items = await LoadAsync();

        var report = await Engine().RunAsync(items, TransferMode.Move, Options(), null, null, CancellationToken.None);

        Assert.Equal(1, report.Deleted);
        Assert.Empty(_fake.Files); // anche il .AAE: sul telefono l'elemento è uno solo
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001.HEIC")));
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001.MOV")));
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001.AAE"))); // con Sposta anche le modifiche annullabili
        Assert.All(_registry.Load(_fake.Device.Key).Values, r => Assert.NotNull(r.DeletedFromPhoneAt));
    }

    [Fact]
    public async Task MoveDoesNotDeleteWhenTheOldCopyChangedWithSameSize()
    {
        // Copiata ieri; poi il file sul PC è stato riscritto (stessa dimensione, contenuto diverso).
        _fake.Add("202610_a", "IMG_0001.HEIC", 80_000, Oct);
        var items = await LoadAsync();
        await Engine().RunAsync(items, TransferMode.Copy, Options(), null, null, CancellationToken.None);
        var onPc = Path.Combine(Month(Oct), "IMG_0001.HEIC");
        var bytes = File.ReadAllBytes(onPc);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(onPc, bytes);

        var report = await Engine().RunAsync(items, TransferMode.Move, Options(), null, null, CancellationToken.None);

        Assert.Equal(0, report.Deleted);
        Assert.Single(_fake.Files); // la foto resta sul telefono
        Assert.Empty(_fake.Deleted);
    }

    [Fact]
    public async Task MoveWithConversionAlwaysKeepsTheOriginal()
    {
        _fake.Add("202610_a", "IMG_0001.HEIC", 80_000, Oct);
        var items = await LoadAsync();
        var engine = new TransferEngine(_conn, _fake.Device, _registry, converter: new CopyingConverter());

        var report = await engine.RunAsync(items, TransferMode.Move, Options() with { ConvertHeic = true, KeepHeicOriginal = false },
            null, null, CancellationToken.None);

        Assert.Equal(1, report.Deleted);
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001.JPG")));
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001.HEIC"))); // l'HEIC non si perde
    }

    [Fact]
    public async Task RefusedDeleteStopsAfterTheFirstItem()
    {
        for (int i = 1; i <= 5; i++) _fake.Add("202610_a", $"IMG_000{i}.HEIC", 10_000, Oct);
        _fake.RefuseDelete = true;
        var items = await LoadAsync();

        var report = await Engine().RunAsync(items, TransferMode.Move, Options(), null, null, CancellationToken.None);

        Assert.Equal(5, report.Copied);
        Assert.True(report.DeleteRefused);
        Assert.Equal(0, report.Deleted);
        Assert.Equal(5, _fake.Files.Count);
    }

    [Fact]
    public async Task NameClashGetsTheSameSuffixForTheWholeLivePhoto()
    {
        _fake.Add("202610_a", "IMG_0001.HEIC", 10_000, Oct);
        _fake.Add("202610_a", "IMG_0001.MOV", 20_000, Oct);
        Directory.CreateDirectory(Month(Oct));
        File.WriteAllBytes(Path.Combine(Month(Oct), "IMG_0001.HEIC"), new byte[123]); // un'altra foto, stesso nome
        var items = await LoadAsync();

        var report = await Engine().RunAsync(items, TransferMode.Copy, Options(), null, null, CancellationToken.None);

        Assert.Equal(1, report.Copied);
        Assert.Equal(123, new FileInfo(Path.Combine(Month(Oct), "IMG_0001.HEIC")).Length);
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001 (2).HEIC")));
        Assert.True(File.Exists(Path.Combine(Month(Oct), "IMG_0001 (2).MOV")));
        Assert.False(File.Exists(Path.Combine(Month(Oct), "IMG_0001.MOV")));
    }

    [Fact]
    public async Task IdenticalFileAlreadyThereIsNotDuplicated()
    {
        var f = _fake.Add("202610_a", "IMG_0001.HEIC", 10_000, Oct);
        Directory.CreateDirectory(Month(Oct));
        File.WriteAllBytes(Path.Combine(Month(Oct), "IMG_0001.HEIC"), f.Content());
        var items = await LoadAsync();

        var report = await Engine().RunAsync(items, TransferMode.Copy, Options(), null, null, CancellationToken.None);

        Assert.Equal(1, report.Copied);
        Assert.Single(Directory.GetFiles(Month(Oct)));
        Assert.NotNull(_registry.Get(_fake.Device.Key, f.Info.Key));
    }

    [Fact]
    public async Task FileGoneFromPhoneIsReportedNotDeleted()
    {
        _fake.Add("202610_a", "IMG_0001.HEIC", 10_000, Oct);
        _fake.Add("202610_a", "IMG_0002.HEIC", 10_000, Oct);
        var items = await LoadAsync();
        _fake.Files.RemoveAt(0); // cancellata dal telefono dopo la lettura dell'elenco

        var report = await Engine().RunAsync(items, TransferMode.Move, Options(), null, null, CancellationToken.None);

        Assert.Single(report.Failed);
        Assert.Equal(1, report.Deleted);
    }

    [Fact]
    public async Task CancelLeavesNoPartialFilesAndDeletesNothing()
    {
        for (int i = 1; i <= 6; i++) _fake.Add("202610_a", $"IMG_000{i}.MOV", 3_000_000, Oct);
        _fake.BytesPerSecond = 6_000_000;
        var items = await LoadAsync();
        using var cts = new CancellationTokenSource();

        var run = Engine().RunAsync(items, TransferMode.Move, Options(), null, null, cts.Token);
        await Task.Delay(700);
        cts.Cancel();
        var report = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(report.Canceled);
        Assert.Empty(_fake.Deleted);
        Assert.Empty(Directory.EnumerateFiles(Dest, "*.part", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(Dest, TransferEngine.StagingFolderName)));
    }

    [Fact]
    public async Task ProgressReachesTheEnd()
    {
        _fake.Add("202610_a", "IMG_0001.MOV", 1_500_000, Oct);
        var items = await LoadAsync();
        var statuses = new List<TransferStatus>();

        await Engine().RunAsync(items, TransferMode.Copy, Options(), new SyncProgress<TransferStatus>(statuses.Add), null, CancellationToken.None);

        var last = statuses.Last();
        Assert.Equal(TransferPhase.Done, last.Phase);
        Assert.Equal(last.BytesTotal, last.BytesDone);
        Assert.Equal(1, last.Copied);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        _conn.DisposeAsync().AsTask().Wait();
        _registry.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>IProgress senza contesto di sincronizzazione: i test leggono gli stati subito.</summary>
internal sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    private readonly object _lock = new();
    public void Report(T value) { lock (_lock) action(value); }
}

/// <summary>Convertitore finto: il "JPG" è una copia dell'originale (basta per provare cosa resta sul PC).</summary>
internal sealed class CopyingConverter : IImageConverter
{
    public void HeicToJpeg(string source, string destination, int quality) => File.Copy(source, destination, overwrite: true);
}
