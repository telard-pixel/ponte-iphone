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
    public void OpensListsAndReadsThroughTheAppleService()
    {
        using var backend = new AppleBackend(_phone.Client());
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
    public void CableIsPreferredAndWifiWorksAlone()
    {
        _phone.Wifi = true;
        using var backend = new AppleBackend(_phone.Client());
        var both = Assert.Single(backend.ListDevices());
        Assert.False(both.IsWifi);
        Assert.False(backend.Open(both.Id).Device.IsWifi);
        Assert.Equal(0, _phone.WifiConnections);

        _phone.Usb = false; // cavo staccato
        var wifi = Assert.Single(backend.ListDevices());
        Assert.Equal(both.Id, wifi.Id); // stesso telefono per l'app
        Assert.True(wifi.IsWifi);
        var opened = backend.Open(wifi.Id);
        Assert.Equal(OpenState.Ready, opened.State);
        Assert.True(opened.Device.IsWifi);
        Assert.True(_phone.WifiConnections > 0);
        var res = backend.Download("/DCIM/100APPLE/IMG_0002.JPG", Path.Combine(_dir, "w.part"), _ => { }, CancellationToken.None);
        Assert.Equal(_phone.Files["/DCIM/100APPLE/IMG_0002.JPG"].Length, res.Bytes);
    }

    [Fact]
    public void WifiFailureDoesNotFallBackToWindows()
    {
        _phone.Usb = false;
        _phone.Wifi = true;
        _phone.ServePairRecord = false;
        using var backend = new SmartBackend(new AppleBackend(_phone.Client()), () => new FakeBackend());
        var dev = Assert.Single(backend.ListDevices());

        // Windows vedrebbe un altro telefono (quello finto col cavo): non va scambiato per questo.
        var e = Assert.Throws<DeviceException>(() => backend.Open(dev.Id));
        Assert.Contains("Wi-Fi", e.Message);
    }

    [Fact]
    public void SilentPhoneFallsBackToWindowsInsteadOfHanging()
    {
        var saved = UsbmuxClient.ReplyTimeout;
        UsbmuxClient.ReplyTimeout = TimeSpan.FromSeconds(1);
        try
        {
            _phone.HangLockdown = true;
            var fake = new FakeBackend();
            fake.Add("202610_a", "IMG_0001.HEIC", 1000);
            using var backend = new SmartBackend(new AppleBackend(_phone.Client()), () => fake);
            var dev = Assert.Single(backend.ListDevices());

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var opened = backend.Open(dev.Id);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"aperto in {clock.Elapsed}");
            Assert.True(opened.Ready);
            Assert.Contains(opened.Notes!, n => n.Contains("Canale Apple non riuscito") && n.Contains("sessione cifrata"));

            // Al controllo successivo si va diretti su Windows, senza riaspettare il telefono.
            backend.Open(dev.Id);
            Assert.Equal(1, _phone.LockdownConnections);
        }
        finally
        {
            UsbmuxClient.ReplyTimeout = saved;
        }
    }

    [Fact]
    public void LockedPhoneIsReportedAsLocked()
    {
        _phone.Locked = true;
        using var backend = new AppleBackend(_phone.Client());
        var opened = backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid);
        Assert.Equal(OpenState.LockedOrNotTrusted, opened.State);

        _phone.Locked = false;
        Assert.Equal(OpenState.Ready, backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid).State);
    }

    [Fact]
    public void MissingFileBecomesNotFound()
    {
        using var backend = new AppleBackend(_phone.Client());
        backend.Open(AppleBackend.IdPrefix + FakeAppleDevice.Udid);
        var e = Assert.Throws<DeviceException>(() =>
            backend.Download("/DCIM/100APPLE/IMG_9999.HEIC", Path.Combine(_dir, "y.part"), _ => { }, CancellationToken.None));
        Assert.Equal(DeviceErrorKind.NotFound, e.Kind);
    }

    [Fact]
    public async Task EngineCopiesThroughTheAppleChannel()
    {
        using var backend = new SmartBackend(new AppleBackend(_phone.Client()), () => new FakeBackend());
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
    public void WithoutPairRecordFallsBackToWindowsKeepingTheSameIdentity()
    {
        _phone.ServePairRecord = false;
        var fake = new FakeBackend();
        fake.Add("202610_a", "IMG_0001.HEIC", 1000);
        using var backend = new SmartBackend(new AppleBackend(_phone.Client()), () => fake);
        var dev = Assert.Single(backend.ListDevices());

        var opened = backend.Open(dev.Id);

        Assert.True(opened.Ready);
        Assert.Equal(dev.Id, opened.Device.Id);
        Assert.Equal(dev.Key, opened.Device.Key);
        Assert.Contains(opened.Notes!, n => n.Contains("Canale: Windows"));
    }

    [Fact]
    public void DeleteGoesThroughWindowsMatchingNameAndSize()
    {
        var fake = new FakeBackend();
        fake.Add("202610_a", "IMG_0002.JPG", 90_000, content: _phone.Files["/DCIM/100APPLE/IMG_0002.JPG"]);
        fake.Add("202610_a", "IMG_0001.HEIC", 700_000, content: _phone.Files["/DCIM/100APPLE/IMG_0001.HEIC"]);
        using var backend = new SmartBackend(new AppleBackend(_phone.Client()), () => fake);
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

        Assert.Equal("w2", SmartBackend.Match(map, b with { ObjectId = "/DCIM/x" }));
        Assert.Null(SmartBackend.Match(map, b with { ObjectId = "/DCIM/x", Created = null }));
        Assert.Null(SmartBackend.Match(map, b with { Name = "IMG_0002.HEIC" }));
    }

    public void Dispose()
    {
        _phone.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
