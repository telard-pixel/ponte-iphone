using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PonteIPhone.Core.Apple;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;
using PonteIPhone.Core.Transfer;

namespace PonteIPhone.Tests;

/// <summary>Il canale Apple (usbmux → lockdown con TLS → AFC) contro un iPhone finto che parla gli stessi protocolli.</summary>
public sealed class AppleTests : IDisposable
{
    private readonly FakeAppleDevice _phone = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("ponte-apple-").FullName;
    private string _pairs => Path.Combine(_dir, "abbinamenti");

    public AppleTests()
    {
        _phone.Files["/DCIM/100APPLE/IMG_0001.HEIC"] = FakeBackend.Deterministic("a", 700_000);
        _phone.Files["/DCIM/100APPLE/IMG_0001.MOV"] = FakeBackend.Deterministic("b", 1_200_000);
        _phone.Files["/DCIM/100APPLE/IMG_0002.JPG"] = FakeBackend.Deterministic("c", 90_000);
        _phone.Files["/DCIM/100APPLE/IMG_0003.JPG"] = []; // vuoto (segnaposto di iCloud): va escluso
        _phone.Files["/PhotoData/Mutations/DCIM/100APPLE/IMG_0002/Adjustments/FullSizeRender.jpg"] = FakeBackend.Deterministic("d", 80_000);
        _phone.Files["/PhotoData/Thumbnails/V2/DCIM/100APPLE/IMG_0001.HEIC/5005.JPG"] = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    }

    [Fact]
    public void XmlPlistRoundTrip()
    {
        var src = new Dictionary<string, object>
        {
            ["s"] = "a & <b>", ["n"] = 42L, ["t"] = true, ["d"] = new byte[] { 1, 2, 3 },
            ["l"] = new List<object> { "x", 1L }, ["sub"] = new Dictionary<string, object> { ["k"] = "v" },
        };
        var back = PropertyList.ParseDict(PropertyList.ToXml(src));
        Assert.Equal("a & <b>", back.GetString("s"));
        Assert.Equal(42L, back.GetLong("n"));
        Assert.True(back.GetBool("t"));
        Assert.Equal([1, 2, 3], back.GetData("d"));
        Assert.Equal("v", back.GetDict("sub")!.GetString("k"));
        Assert.Equal(2, ((List<object>)back["l"]).Count);
    }

    [Fact]
    public void BinaryPlistIsRead()
    {
        // {"a": "b", "n": 5, "t": true} scritto a mano nel formato bplist00.
        var b = new List<byte>(Encoding.ASCII.GetBytes("bplist00"));
        b.AddRange([0xD3, 1, 2, 3, 4, 5, 6]);         // 8: dizionario di 3
        b.AddRange([0x51, (byte)'a']);                 // 15
        b.AddRange([0x51, (byte)'n']);                 // 17
        b.AddRange([0x51, (byte)'t']);                 // 19
        b.AddRange([0x51, (byte)'b']);                 // 21
        b.AddRange([0x10, 5]);                         // 23
        b.Add(0x09);                                   // 25
        b.AddRange([8, 15, 17, 19, 21, 23, 25]);       // 26: tabella degli offset
        var trailer = new byte[32];
        trailer[6] = 1;
        trailer[7] = 1;
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), 26);
        b.AddRange(trailer);

        var d = PropertyList.ParseDict(b.ToArray());
        Assert.Equal("b", d.GetString("a"));
        Assert.Equal(5L, d.GetLong("n"));
        Assert.True(d.GetBool("t"));
    }

    [Fact]
    public void CountsPhotosLeftOnICloud()
    {
        // "Ottimizza spazio iPhone": in DCIM restano file vuoti al posto delle foto su iCloud.
        _phone.Files["/DCIM/101APPLE/IMG_0100.HEIC"] = [];
        _phone.Files["/DCIM/101APPLE/IMG_0101.HEIC"] = [];
        _phone.Files["/DCIM/101APPLE/.MISC"] = [];
        // Originale solo su iCloud: in DCIM non c'è niente, ma la libreria ha la miniatura.
        _phone.Files["/PhotoData/Thumbnails/V2/DCIM/101APPLE/IMG_0102.HEIC/5005.JPG"] = [0xFF, 0xD8];
        _phone.Files["/PhotoData/Thumbnails/V2/DCIM/101APPLE/IMG_0100.HEIC/5005.JPG"] = [0xFF, 0xD8]; // già contata come file vuoto
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        var dev = Assert.Single(backend.ListDevices());
        Assert.Equal(OpenState.Ready, backend.Open(dev.Id).State);

        var files = new List<RemoteFile>();
        backend.Enumerate(files.AddRange, CancellationToken.None);
        Assert.Equal(4, files.Count);
        // I due vuoti nuovi, IMG_0003 vuoto del telefono di prova, IMG_0102 solo miniatura; .MISC no.
        Assert.Equal(4, backend.CloudOnlyFiles);
        Assert.Contains("2 cartelle (0 vuote), 3 file vuoti, 1 foto con miniatura ma senza file", backend.LastEnumerationSummary);
    }

    [Fact]
    public void OpensListsAndReadsThroughTheAppleService()
    {
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        var dev = Assert.Single(backend.ListDevices());
        Assert.Equal(AppleBackend.IdPrefix + FakeAppleDevice.Udid, dev.Id);
        Assert.Equal(FakeAppleDevice.Udid, dev.Key);

        var opened = backend.Open(dev.Id);
        Assert.Equal(OpenState.Ready, opened.State);
        Assert.Equal("iPhone di Prova", opened.Device.Name);
        Assert.Equal("18.1", opened.Device.Firmware);
        Assert.Equal(FakeAppleDevice.Udid, opened.Device.Key);
        Assert.Equal(128_000_000_000, opened.Storage!.Capacity);
        Assert.Equal(1, _phone.SessionsWithClientCert); // sessione cifrata con il certificato dell'abbinamento

        var files = new List<RemoteFile>();
        backend.Enumerate(files.AddRange, CancellationToken.None);
        Assert.Equal(4, files.Count);
        var edited = Assert.Single(files, f => f.Name == "IMG_E0002.JPG");
        Assert.Equal("100APPLE", edited.Folder);
        Assert.NotNull(files[0].Created);

        var items = MediaGrouper.Group(files);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Kind == MediaKind.Live);
        Assert.Contains(items, i => i.HasEdited);

        var thumb = backend.GetThumbnail("/DCIM/100APPLE/IMG_0001.HEIC");
        Assert.Equal(_phone.Files["/PhotoData/Thumbnails/V2/DCIM/100APPLE/IMG_0001.HEIC/5005.JPG"], thumb);

        var dest = Path.Combine(_dir, "x.part");
        long last = 0;
        var res = backend.Download("/DCIM/100APPLE/IMG_0001.MOV", dest, b => last = b, CancellationToken.None);
        var expected = _phone.Files["/DCIM/100APPLE/IMG_0001.MOV"];
        Assert.Equal(expected.Length, res.Bytes);
        Assert.Equal(expected.Length, last);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), res.Sha256);
        Assert.Equal(expected, File.ReadAllBytes(dest));
    }

    [Fact]
    public void WithoutPairRecordAsksTrustThenPairsAndSavesIt()
    {
        // PC mai abbinato: l'app chiede lei "Autorizza", aspetta la risposta e salva l'abbinamento.
        _phone.ServePairRecord = false;
        _phone.PairPendingReplies = 2;
        var pairs = _pairs;
        using var backend = new SmartBackend(new AppleBackend(_phone.Client(), pairs), () => new FakeBackend());
        var dev = Assert.Single(backend.ListDevices());

        Assert.Equal(OpenState.NotTrusted, backend.Open(dev.Id).State);
        Assert.Equal(OpenState.NotTrusted, backend.Open(dev.Id).State);
        var opened = backend.Open(dev.Id);

        Assert.Equal(OpenState.Ready, opened.State);
        Assert.Equal(dev.Key, opened.Device.Key);
        Assert.Equal(3, _phone.PairRequests);
        Assert.NotNull(_phone.SavedPairRecord);
        Assert.True(File.Exists(Path.Combine(pairs, FakeAppleDevice.Udid + ".plist")));
        var saved = PairRecord.From(_phone.SavedPairRecord)!;
        Assert.Equal("BUID-PC", saved.SystemBuid);
        Assert.Equal([9, 9, 9], saved.EscrowBag);

        // Riaperto dopo: l'abbinamento salvato basta, niente più richieste.
        Assert.Equal(OpenState.Ready, backend.Open(dev.Id).State);
        Assert.Equal(3, _phone.PairRequests);
    }

    [Fact]
    public void PhoneThatForgotThePcIsPairedAgain()
    {
        _phone.TrustedHosts.Clear(); // ripristinato o "Ripristina posizione e privacy"
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        var dev = Assert.Single(backend.ListDevices());
        Assert.Equal(OpenState.Ready, backend.Open(dev.Id).State);
        Assert.Equal(1, _phone.PairRequests);
    }

    [Fact]
    public void TrustDeniedAndLockedAreToldApart()
    {
        _phone.ServePairRecord = false;
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        var dev = Assert.Single(backend.ListDevices());
        _phone.Locked = true;
        Assert.Equal(OpenState.LockedOrNotTrusted, backend.Open(dev.Id).State);
        _phone.Locked = false;
        _phone.DenyPairing = true;
        Assert.Equal(OpenState.TrustDenied, backend.Open(dev.Id).State);
    }

    [Fact]
    public void WifiOnlyPhoneIsIgnored()
    {
        _phone.Usb = false;
        _phone.Wifi = true;
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        Assert.Empty(backend.ListDevices());
    }

    [Fact]
    public void SilentPhoneDoesNotHang()
    {
        var saved = UsbmuxClient.ReplyTimeout;
        UsbmuxClient.ReplyTimeout = TimeSpan.FromSeconds(1);
        try
        {
            _phone.HangLockdown = true;
            using var backend = new SmartBackend(new AppleBackend(_phone.Client(), _pairs), () => new FakeBackend());
            var dev = Assert.Single(backend.ListDevices());
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var e = Assert.Throws<DeviceException>(() => backend.Open(dev.Id));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"risposta dopo {clock.Elapsed}");
            Assert.Contains("sessione cifrata", e.Message);
            Assert.Equal(DeviceErrorKind.Transient, e.Kind);
        }
        finally
        {
            UsbmuxClient.ReplyTimeout = saved;
        }
    }

    [Fact]
    public void ConnectionDropIsTransientAndTheNextCallReopens()
    {
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        _phone.DropAfcAfter = 3; // il primo collegamento AFC cade presto
        backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid);
        var e = Assert.ThrowsAny<DeviceException>(() =>
            backend.Download("/DCIM/100APPLE/IMG_0001.MOV", Path.Combine(_dir, "d1.part"), _ => { }, CancellationToken.None));
        Assert.Equal(DeviceErrorKind.Transient, e.Kind); // telefono ancora lì: non "scollegato"

        var res = backend.Download("/DCIM/100APPLE/IMG_0001.MOV", Path.Combine(_dir, "d2.part"), _ => { }, CancellationToken.None);
        Assert.Equal(_phone.Files["/DCIM/100APPLE/IMG_0001.MOV"].Length, res.Bytes);
    }

    [Fact]
    public async Task DeleteWorksFromAFreshProcessWithoutTheList()
    {
        // Come nell'app: le cancellazioni hanno un processo tutto loro, che apre il telefono ma non lo elenca.
        var fake = new FakeBackend();
        fake.Add("202610_a", "IMG_0002.JPG", 90_000, content: _phone.Files["/DCIM/100APPLE/IMG_0002.JPG"]);
        var browse = new List<RemoteFile>();
        using (var first = new AppleBackend(_phone.Client(), _pairs))
        {
            first.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid);
            first.Enumerate(browse.AddRange, CancellationToken.None);
        }
        var item = Assert.Single(MediaGrouper.Group(browse), i => i.BaseName == "IMG_0002");

        using var fresh = new SmartBackend(new AppleBackend(_phone.Client(), _pairs), () => fake);
        await using var conn = new InProcessConnection(fresh);
        var opened = await conn.OpenAsync(AppleBackend.IdPrefix + FakeAppleDevice.Udid, CancellationToken.None);
        using var registry = new CopyRegistry(Path.Combine(_dir, "copie.db"));
        var report = await new TransferEngine(conn, opened.Device, registry, converter: null)
            .DeleteAsync([item], new TransferOptions { DestinationRoot = _dir }, null, CancellationToken.None);

        Assert.Equal(1, report.Deleted);
        Assert.Equal(["IMG_0002.JPG"], fake.Deleted);
    }

    [Fact]
    public async Task DeleteStopsAtOnceWhenWindowsDoesNotShowThePhotos()
    {
        var fake = new FakeBackend { Locked = true }; // Windows vede il telefono ma con la memoria vuota
        using var backend = new SmartBackend(new AppleBackend(_phone.Client(), _pairs), () => fake, sleep: _ => { });
        await using var conn = new InProcessConnection(backend);
        var opened = await conn.OpenAsync(AppleBackend.IdPrefix + FakeAppleDevice.Udid, CancellationToken.None);
        var files = new List<RemoteFile>();
        await conn.EnumerateAsync(files.AddRange, CancellationToken.None);
        using var registry = new CopyRegistry(Path.Combine(_dir, "copie.db"));
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var report = await new TransferEngine(conn, opened.Device, registry, converter: null)
            .DeleteAsync(MediaGrouper.Group(files), new TransferOptions { DestinationRoot = _dir }, null, CancellationToken.None);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(0, report.Deleted);
        Assert.Equal(2, report.DeleteFailed);
        Assert.Contains("Windows non mostra", report.DeleteProblem);
        Assert.Empty(fake.Deleted);
    }

    [Fact]
    public void StorageIsSplitLikeITunes()
    {
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        Assert.True(backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid).Ready);

        var b = backend.GetStorage(CancellationToken.None);

        Assert.Equal(128_000_000_000, b.Capacity);
        // Libero come AFC (28 GB, come la barretta laterale), non i 40 di com.apple.disk_usage (16/10: 74 contro 14 su iOS 27).
        Assert.Equal(28_000_000_000, b.Free);
        Assert.Equal(b.Capacity, b.Slices.Sum(x => x.Bytes)); // la barra torna sempre al totale
        long Slice(string key) => b.Slices.FirstOrDefault(x => x.Key == key)?.Bytes ?? 0;
        Assert.Equal(12_000_000_000, Slice("system"));                    // disco meno partizione dati
        Assert.Equal(720_000_000, Slice("apps"));
        Assert.Equal(10_000_000_000, Slice("appdata"));
        Assert.Equal(700_000 + 1_200_000 + 90_000 + 80_000 + 7, Slice("photos")); // DCIM + libreria di Foto
        Assert.Equal(0, Slice("music"));
        Assert.True(Slice("other") > 0);
        Assert.Equal(["WhatsApp", "Maps", "Calcolatrice X"], b.Apps.Select(a => a.Name));
        Assert.Contains("considera liberi", Assert.Single(b.Notes));
    }

    [Fact]
    public void StorageBarNeverExceedsTheTotal()
    {
        // Dimensioni delle app arrotondate per eccesso dal telefono: più grandi dell'usato.
        var b = StorageBreakdown.Build(100, 60, 10, [new AppUsage("A", "a", 30, 40)], photos: 5, music: 0, books: 0, notes: []);
        Assert.Equal(100, b.Slices.Sum(x => x.Bytes));
        Assert.Equal(60, b.Slices.Single(x => x.Key == "free").Bytes);
        Assert.DoesNotContain(b.Slices, x => x.Key == "other");
    }

    [Fact]
    public void TinySlicesGoToOther()
    {
        var b = StorageBreakdown.Build(128_000_000_000, 14_000_000_000, 6_000_000_000, [], photos: 5_800_000_000, music: 0, books: 117_000, notes: []);
        Assert.DoesNotContain(b.Slices, x => x.Key == "books");
        Assert.Equal(128_000_000_000, b.Slices.Sum(x => x.Bytes));
    }

    [Fact]
    public void UnpluggedPhoneIsNoticedAtOnce()
    {
        // iOS 26+: lo schermo si blocca e l'iPhone chiude i dati. Prima l'app aspettava 25 s a vuoto.
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        Assert.True(backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid).Ready);
        _phone.Unplug();
        Thread.Sleep(500);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var e = Assert.Throws<DeviceException>(() => backend.Enumerate(_ => { }, CancellationToken.None));
        Assert.Equal(DeviceErrorKind.Gone, e.Kind);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void IsLockedTellsLockedFromUnlocked()
    {
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        Assert.Null(backend.IsLocked()); // telefono non ancora aperto: non si sa
        backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid);
        Assert.False(backend.IsLocked());
        _phone.Locked = true;
        Assert.True(backend.IsLocked());
    }

    [Fact]
    public async Task DeleteWaitsForTheUnlockInsteadOfGivingUp()
    {
        // 15/10: l'app mostrava le foto (il canale Apple legge anche da bloccato), ma Elimina si fermava subito
        // perché Windows, col telefono bloccato, non le mostra. Ora aspetta lo sblocco e poi elimina.
        var fake = new FakeBackend();
        fake.Add("202610_a", "IMG_0002.JPG", 90_000, content: _phone.Files["/DCIM/100APPLE/IMG_0002.JPG"]);
        using var backend = new SmartBackend(new AppleBackend(_phone.Client(), _pairs), () => fake, sleep: _ => { });
        await using var conn = new InProcessConnection(backend);
        var opened = await conn.OpenAsync(AppleBackend.IdPrefix + FakeAppleDevice.Udid, CancellationToken.None);
        var files = new List<RemoteFile>();
        await conn.EnumerateAsync(files.AddRange, CancellationToken.None);
        var item = Assert.Single(MediaGrouper.Group(files), i => i.BaseName == "IMG_0002");
        _phone.Locked = true;
        fake.Locked = true;
        var waited = false;
        var progress = new SyncProgress(s =>
        {
            if (s.Phase != TransferPhase.WaitingForDevice) return;
            waited = true;
            _phone.Locked = false; // l'utente sblocca
            fake.Locked = false;
        });
        using var registry = new CopyRegistry(Path.Combine(_dir, "copie.db"));

        var report = await new TransferEngine(conn, opened.Device, registry, converter: null)
            .DeleteAsync([item], new TransferOptions { DestinationRoot = _dir, DevicePollInterval = TimeSpan.FromMilliseconds(20) },
                progress, CancellationToken.None);

        Assert.True(waited);
        Assert.Equal(1, report.Deleted);
        Assert.Null(report.DeleteProblem);
        Assert.Equal(["IMG_0002.JPG"], fake.Deleted);
    }

    private sealed class SyncProgress(Action<TransferStatus> on) : IProgress<TransferStatus>
    {
        public void Report(TransferStatus value) => on(value);
    }

    [Fact]
    public void TheRightPhoneIsChosenForDeleting()
    {
        var a = new DeviceInfo(@"\\?\usb#vid_05ac&pid_12a8&mi_00#00008110001a2b3c4d5e6f70#{6ac27878}", "", "Apple", "", "", "");
        var b = new DeviceInfo(@"\\?\usb#vid_05ac&pid_12a8&mi_00#00008030000111222333444f#{6ac27878}", "", "Apple", "", "", "");
        Assert.Same(a, SmartBackend.PickWpdDevice([a, b], FakeAppleDevice.Udid, 2));
        Assert.Null(SmartBackend.PickWpdDevice([b], FakeAppleDevice.Udid, 2)); // l'altro telefono: mai
        var unknown = new DeviceInfo("wpd-1", "", "Apple", "", "", "");
        Assert.Same(unknown, SmartBackend.PickWpdDevice([unknown], FakeAppleDevice.Udid, 1));
        Assert.Null(SmartBackend.PickWpdDevice([unknown, b], FakeAppleDevice.Udid, 2));
    }

    [Fact]
    public void LockedPhoneIsReportedAsLocked()
    {
        _phone.Locked = true;
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        var opened = backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid);
        Assert.Equal(OpenState.LockedOrNotTrusted, opened.State);

        _phone.Locked = false;
        Assert.Equal(OpenState.Ready, backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid).State);
    }

    [Fact]
    public void MissingFileBecomesNotFound()
    {
        using var backend = new AppleBackend(_phone.Client(), _pairs);
        backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid);
        var e = Assert.Throws<DeviceException>(() =>
            backend.Download("/DCIM/100APPLE/IMG_9999.HEIC", Path.Combine(_dir, "y.part"), _ => { }, CancellationToken.None));
        Assert.Equal(DeviceErrorKind.NotFound, e.Kind);
    }

    [Fact]
    public async Task EngineCopiesThroughTheAppleChannel()
    {
        using var backend = new SmartBackend(new AppleBackend(_phone.Client(), _pairs), () => new FakeBackend());
        await using var conn = new InProcessConnection(backend);
        var dev = Assert.Single(await conn.ListDevicesAsync(CancellationToken.None));
        var opened = await conn.OpenAsync(dev.Id, CancellationToken.None);
        Assert.True(opened.Ready);
        var files = new List<RemoteFile>();
        await conn.EnumerateAsync(files.AddRange, CancellationToken.None);
        using var registry = new CopyRegistry(Path.Combine(_dir, "copie.db"));
        var engine = new TransferEngine(conn, opened.Device, registry, converter: null);

        var report = await engine.RunAsync(MediaGrouper.Group(files), TransferMode.Copy,
            new TransferOptions { DestinationRoot = Path.Combine(_dir, "Foto"), ByYearMonth = false }, null, null, CancellationToken.None);

        Assert.Equal(2, report.Copied);
        Assert.Empty(report.Failed);
        Assert.Equal(_phone.Files["/DCIM/100APPLE/IMG_0002.JPG"], File.ReadAllBytes(Path.Combine(_dir, "Foto", "IMG_0002.JPG")));
        Assert.True(File.Exists(Path.Combine(_dir, "Foto", "IMG_E0002.JPG")));
    }

    [Fact]
    public void DeleteGoesThroughWindowsMatchingNameAndSize()
    {
        var fake = new FakeBackend();
        fake.Add("202610_a", "IMG_0002.JPG", 90_000, content: _phone.Files["/DCIM/100APPLE/IMG_0002.JPG"]);
        fake.Add("202610_a", "IMG_0001.HEIC", 700_000, content: _phone.Files["/DCIM/100APPLE/IMG_0001.HEIC"]);
        using var backend = new SmartBackend(new AppleBackend(_phone.Client(), _pairs), () => fake);
        backend.Open(Assert.Single(backend.ListDevices()).Id);
        backend.Enumerate(_ => { }, CancellationToken.None);

        var results = backend.Delete(["/DCIM/100APPLE/IMG_0002.JPG", "/DCIM/100APPLE/IMG_0001.MOV"]);

        Assert.True(results[0].Ok);
        Assert.Equal("/DCIM/100APPLE/IMG_0002.JPG", results[0].ObjectId);
        Assert.False(results[1].Ok); // il .MOV in Windows non c'è: non si cancella niente a caso
        Assert.Equal(DeviceErrors.NoWpdMatch, results[1].HResult);
        Assert.Equal(["IMG_0002.JPG"], fake.Deleted);
    }

    [Fact]
    public void AmbiguousMatchDeletesNothing()
    {
        var a = new RemoteFile("w1", "IMG_0001.HEIC", "202501_a", 500, new DateTime(2025, 1, 3), null, true);
        var b = new RemoteFile("w2", "IMG_0001.HEIC", "202601_a", 500, new DateTime(2026, 1, 3), null, true);
        var map = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase) { ["IMG_0001.HEIC"] = [a, b] };

        var afc = b with { ObjectId = "/DCIM/x", Folder = "100APPLE" };
        Assert.Equal("w2", SmartBackend.Match(map, afc));
        Assert.Null(SmartBackend.Match(map, afc with { Created = null }));
        Assert.Null(SmartBackend.Match(map, afc with { Name = "IMG_0002.HEIC" }));
        // Stessa cartella anche in WPD: decide quella, anche senza data.
        Assert.Equal("w1", SmartBackend.Match(map, a with { ObjectId = "/DCIM/y", Created = null }));
    }

    [Fact]
    public void SameNameButDifferentSizeIsNeverDeleted()
    {
        // L'elenco WPD ha perso una cartella: resta un solo IMG_0001.HEIC, ma è un'altra foto.
        var other = new RemoteFile("w1", "IMG_0001.HEIC", "202501_a", 500_000, new DateTime(2025, 1, 3), null, true);
        var map = new Dictionary<string, List<RemoteFile>>(StringComparer.OrdinalIgnoreCase) { ["IMG_0001.HEIC"] = [other] };
        var mine = new RemoteFile("/DCIM/120APPLE/IMG_0001.HEIC", "IMG_0001.HEIC", "120APPLE", 800_000, new DateTime(2026, 1, 3), null, true);
        Assert.Null(SmartBackend.Match(map, mine));
    }

    public void Dispose()
    {
        _phone.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
