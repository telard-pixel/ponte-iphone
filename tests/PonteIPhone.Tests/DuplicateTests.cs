using PonteIPhone.Core.Model;

namespace PonteIPhone.Tests;

/// <summary>Fai spazio: doppioni solo se il contenuto è identico, e uno resta sempre sul telefono.</summary>
public class DuplicateTests
{
    private static RemoteFile F(string name, string folder, long size, DateTime date) =>
        new("/DCIM/" + folder + "/" + name, name, folder, size, date, null, true);

    private static readonly DateTime May = new(2026, 5, 12, 10, 0, 0);

    [Fact]
    public void SameSizeIsOnlyACandidateTheContentDecides()
    {
        var items = MediaGrouper.Group([
            F("IMG_2210.HEIC", "101APPLE", 5000, May),
            F("IMG_3301.HEIC", "103APPLE", 5000, May.AddDays(30)), // stessa dimensione, altra foto
            F("IMG_4400.HEIC", "105APPLE", 5000, May.AddDays(60)), // copia vera della prima
            F("IMG_5000.HEIC", "105APPLE", 7000, May)]);

        var candidates = DuplicateFinder.Candidates(items);
        Assert.Equal(3, Assert.Single(candidates).Count);

        var hashes = new Dictionary<string, string> { ["IMG_2210.HEIC"] = "AA", ["IMG_3301.HEIC"] = "BB", ["IMG_4400.HEIC"] = "aa" };
        var group = Assert.Single(DuplicateFinder.Confirm(candidates, f => hashes[f.Name]));
        Assert.Equal("IMG_2210", group.Keep.BaseName); // il più vecchio resta
        Assert.Equal("IMG_4400", Assert.Single(group.Copies).BaseName);
        Assert.Equal(5000, group.Saving);

        var newest = Assert.Single(DuplicateFinder.Confirm(candidates, f => hashes[f.Name], keepNewest: true));
        Assert.Equal("IMG_4400", newest.Keep.BaseName);
    }

    [Fact]
    public void ALivePhotoIsADuplicateOnlyIfItsVideoIsToo()
    {
        var items = MediaGrouper.Group([
            F("IMG_0001.HEIC", "100APPLE", 4000, May), F("IMG_0001.MOV", "100APPLE", 9000, May),
            F("IMG_0002.HEIC", "102APPLE", 4000, May), F("IMG_0002.MOV", "102APPLE", 9000, May)]);
        var candidates = DuplicateFinder.Candidates(items);

        // Foto uguale ma video diverso: togliere la seconda perderebbe il suo video.
        Assert.Empty(DuplicateFinder.Confirm(candidates, f => f.Name == "IMG_0002.MOV" ? "V2" : f.Extension == "MOV" ? "V1" : "P"));
        Assert.Single(DuplicateFinder.Confirm(candidates, f => f.Extension == "MOV" ? "V" : "P"));
    }

    [Fact]
    public void UnreadableOrEmptyFilesNeverCount()
    {
        var items = MediaGrouper.Group([
            F("IMG_0001.JPG", "100APPLE", 300, May), F("IMG_0002.JPG", "101APPLE", 300, May),
            F("IMG_0003.JPG", "101APPLE", 0, May), F("IMG_0004.JPG", "102APPLE", 0, May)]);
        var candidates = DuplicateFinder.Candidates(items);
        Assert.Equal(2, Assert.Single(candidates).Count); // i file vuoti (solo su iCloud) non entrano

        Assert.Empty(DuplicateFinder.Confirm(candidates, f => f.Name == "IMG_0002.JPG" ? null : "H"));
    }
}
