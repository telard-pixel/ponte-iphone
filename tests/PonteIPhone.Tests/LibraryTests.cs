using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;
using PonteIPhone.Core.Transfer;

namespace PonteIPhone.Tests;

public class GrouperTests
{
    private static RemoteFile F(string name, string folder = "202610_a", long size = 100, DateTime? date = null) =>
        new("id-" + folder + name, name, folder, size, date ?? new DateTime(2026, 10, 12, 10, 0, 0), null, true);

    [Fact]
    public void LivePhotoIsOneItemWithTwoFiles()
    {
        var items = MediaGrouper.Group([F("IMG_1234.HEIC"), F("IMG_1234.MOV", size: 3000)]);
        var item = Assert.Single(items);
        Assert.Equal(MediaKind.Live, item.Kind);
        Assert.Equal("IMG_1234.HEIC", item.Primary.Name);
        Assert.Contains(item.Files, f => f.Role == FileRole.LiveVideo && f.File.Name == "IMG_1234.MOV");
    }

    [Fact]
    public void EditedVersionAndAaeStayWithTheOriginal()
    {
        var items = MediaGrouper.Group([F("IMG_0007.HEIC"), F("IMG_E0007.HEIC"), F("IMG_0007.AAE"), F("IMG_O0007.AAE")]);
        var item = Assert.Single(items);
        Assert.Equal(MediaKind.Photo, item.Kind);
        Assert.True(item.HasEdited);
        Assert.Equal(2, item.Files.Count(f => f.Role == FileRole.Sidecar));
        // Di base: originale e modificata, niente .AAE.
        Assert.Equal(["IMG_0007.HEIC", "IMG_E0007.HEIC"], item.FilesToCopy(true, false).Select(f => f.File.Name).Order());
        Assert.Equal(["IMG_0007.HEIC"], item.FilesToCopy(false, false).Select(f => f.File.Name));
    }

    [Fact]
    public void SameNumberInDifferentFoldersAreDifferentItems()
    {
        var items = MediaGrouper.Group([F("IMG_0001.HEIC", "202601_a"), F("IMG_0001.HEIC", "202609_a")]);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void KindsAreRecognised()
    {
        var items = MediaGrouper.Group([F("IMG_0001.MOV"), F("IMG_0002.PNG"), F("IMG_0003.JPG"), F("IMG_0004.DNG")]);
        Assert.Equal(MediaKind.Video, items.Single(i => i.BaseName == "IMG_0001").Kind);
        Assert.Equal(MediaKind.Screenshot, items.Single(i => i.BaseName == "IMG_0002").Kind);
        Assert.Equal(MediaKind.Photo, items.Single(i => i.BaseName == "IMG_0003").Kind);
        Assert.Equal(MediaKind.Photo, items.Single(i => i.BaseName == "IMG_0004").Kind);
    }

    [Fact]
    public void OnlySidecarsShowNothing()
    {
        Assert.Empty(MediaGrouper.Group([F("IMG_0009.AAE")]));
    }

    [Fact]
    public void NewestFirstAndDateFromFolderWhenMissing()
    {
        var a = F("IMG_0001.HEIC", "202601_a") with { Created = null };
        var b = F("IMG_0002.HEIC", "202603_b") with { Created = null };
        var items = MediaGrouper.Group([a, b]);
        Assert.Equal("IMG_0002", items[0].BaseName);
        Assert.Equal(new DateTime(2026, 3, 1), items[0].Date);
        Assert.Null(MediaGrouper.DateFromFolder("100APPLE"));
    }
}

public class PlannerTests
{
    [Fact]
    public void YearMonthFolders()
    {
        var p = new DestinationPlanner(@"C:\Foto", byYearMonth: true);
        Assert.Equal(Path.Combine(@"C:\Foto", "2026", "10-Ottobre"), p.FolderFor(new DateTime(2026, 10, 31, 23, 59, 0)));
        Assert.Equal(Path.Combine(@"C:\Foto", "Senza data"), p.FolderFor(null));
        Assert.Equal(@"C:\Foto", new DestinationPlanner(@"C:\Foto", false).FolderFor(DateTime.Now));
    }

    [Theory]
    [InlineData("IMG_1.HEIC", "IMG_1.HEIC")]
    [InlineData("a:b?.jpg", "a_b_.jpg")]
    [InlineData("CON.JPG", "_CON.JPG")]
    [InlineData("foto. ", "foto")]
    public void SafeNames(string input, string expected) => Assert.Equal(expected, DestinationPlanner.SafeFileName(input));

    [Fact]
    public void Suffix()
    {
        Assert.Equal("IMG_1.HEIC", DestinationPlanner.WithSuffix("IMG_1.HEIC", 1));
        Assert.Equal("IMG_1 (2).HEIC", DestinationPlanner.WithSuffix("IMG_1.HEIC", 2));
    }
}

public class CaptureDateTests
{
    [Fact]
    public void AppleDateKeepsTheWallClockOfThePlace()
    {
        Assert.True(CaptureDate.TryParseAppleDate("2026-09-30T23:30:05+0200", out var d));
        Assert.Equal(new DateTime(2026, 9, 30, 23, 30, 5), d);
        Assert.True(CaptureDate.TryParseAppleDate("2026-09-30T23:30:05-07:00", out var d2));
        Assert.Equal(new DateTime(2026, 9, 30, 23, 30, 5), d2);
    }

    [Fact]
    public void BogusDatesAreRejected()
    {
        Assert.False(CaptureDate.TryParseAppleDate("1904-01-01T00:00:00Z", out _));
        Assert.False(CaptureDate.TryParseAppleDate("domani", out _));
        Assert.False(CaptureDate.Plausible(new DateTime(1970, 1, 1)));
    }

    [Fact]
    public void RandomBytesHaveNoDate()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[5000]);
        try { Assert.Null(CaptureDate.FromFile(path)); }
        finally { File.Delete(path); }
    }
}

public class RegistryTests
{
    [Fact]
    public void RoundTripAndDeletion()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            using var reg = new CopyRegistry(Path.Combine(dir, "copie.db"));
            var file = Path.Combine(dir, "IMG_1.HEIC");
            File.WriteAllBytes(file, new byte[10]);
            reg.Record("tel", "K1", file, 10, "ABC");
            var r = reg.Get("tel", "K1");
            Assert.NotNull(r);
            Assert.True(CopyRegistry.StillOnDisk(r));
            reg.MarkDeleted("tel", "K1");
            Assert.NotNull(reg.Load("tel")["K1"].DeletedFromPhoneAt);
            Assert.Empty(reg.Load("altro"));
            Assert.Equal(1, reg.Summary("tel").Count);
            File.Delete(file);
            Assert.False(CopyRegistry.StillOnDisk(r));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}

public class SettingsTests
{
    [Fact]
    public void SaveLoadAndBackup()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "impostazioni.json");
        var s = new AppSettings { DestinationRoot = @"D:\Foto", ConvertHeicToJpeg = true };
        s.Save(path);
        s.Save(path);
        Assert.True(File.Exists(path + ".bak"));
        File.WriteAllText(path, "{rotto");
        var loaded = AppSettings.Load(path);
        Assert.Equal(@"D:\Foto", loaded.DestinationRoot);
        Assert.True(loaded.ConvertHeicToJpeg);
        Directory.Delete(dir, true);
    }
}
