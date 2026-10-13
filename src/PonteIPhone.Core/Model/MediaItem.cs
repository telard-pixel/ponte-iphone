using System.Text.RegularExpressions;

namespace PonteIPhone.Core.Model;

public enum MediaKind
{
    Photo,
    Live,
    Video,
    Screenshot,
}

/// <summary>Che cosa rappresenta un file dentro un elemento della libreria.</summary>
public enum FileRole
{
    /// <summary>La foto o il video originale.</summary>
    Primary,
    /// <summary>Il video di una Live Photo (IMG_1234.MOV accanto a IMG_1234.HEIC).</summary>
    LiveVideo,
    /// <summary>Versione modificata (IMG_E1234.*).</summary>
    Edited,
    /// <summary>File .AAE con le istruzioni di modifica di Apple.</summary>
    Sidecar,
}

public sealed record MediaFile(RemoteFile File, FileRole Role);

/// <summary>
/// Un elemento come lo vede l'app Foto: una Live Photo è un solo elemento anche se sul telefono
/// sono due file, una foto modificata porta con sé la versione modificata e il file .AAE.
/// </summary>
public sealed class MediaItem
{
    public required string Id { get; init; }
    public required string Folder { get; init; }
    /// <summary>Nome senza estensione del file principale (es. IMG_1234).</summary>
    public required string BaseName { get; init; }
    public required MediaKind Kind { get; init; }
    public required IReadOnlyList<MediaFile> Files { get; init; }
    /// <summary>Data dichiarata dal telefono (o ricavata dalla cartella): la data vera si legge dal file copiato.</summary>
    public DateTime? Date { get; init; }

    public RemoteFile Primary => Files.First(f => f.Role == FileRole.Primary).File;

    public long TotalSize => Files.Sum(f => f.File.Size);

    public bool HasEdited => Files.Any(f => f.Role == FileRole.Edited);

    public bool IsVideo => Kind == MediaKind.Video;

    /// <summary>File da copiare secondo le impostazioni (il principale e il video Live sempre).</summary>
    public IEnumerable<MediaFile> FilesToCopy(bool includeEdited, bool includeSidecars) => Files.Where(f => f.Role switch
    {
        FileRole.Primary or FileRole.LiveVideo => true,
        FileRole.Edited => includeEdited,
        FileRole.Sidecar => includeSidecars,
        _ => false,
    });

    public override string ToString() => $"{Folder}/{BaseName} ({Kind}, {Files.Count} file)";
}

/// <summary>Raggruppa i file del Rullino negli elementi che vede l'utente.</summary>
public static partial class MediaGrouper
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { "HEIC", "HEIF", "JPG", "JPEG", "PNG", "DNG", "GIF", "TIF", "TIFF", "WEBP" };

    private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { "MOV", "MP4", "M4V", "3GP", "AVI" };

    public static bool IsImage(string ext) => ImageExt.Contains(ext);
    public static bool IsVideo(string ext) => VideoExt.Contains(ext);
    public static bool IsSidecar(string ext) => ext.Equals("AAE", StringComparison.OrdinalIgnoreCase);

    // IMG_E1234 = modificata, IMG_O1234 = file .AAE dell'originale. Il prefisso può cambiare (es. "IMG_").
    [GeneratedRegex(@"^(?<pre>[A-Za-z]+_)(?<flag>[EO])(?<num>\d{3,})$")]
    private static partial Regex FlaggedName();

    // Cartelle degli iOS recenti: 202601_a, 202601_b...
    [GeneratedRegex(@"^(?<y>(19|20)\d{2})(?<m>0[1-9]|1[0-2])(_|$)")]
    private static partial Regex MonthFolder();

    /// <summary>Primo giorno del mese indicato dal nome della cartella (202601_a → 1/1/2026), se c'è.</summary>
    public static DateTime? DateFromFolder(string folder)
    {
        var last = folder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var m = MonthFolder().Match(last);
        return m.Success ? new DateTime(int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["m"].Value), 1) : null;
    }

    /// <summary>Elementi dal più recente al meno recente.</summary>
    public static List<MediaItem> Group(IEnumerable<RemoteFile> files)
    {
        var groups = new Dictionary<string, List<(RemoteFile File, bool Flagged, char Flag)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var baseName = Path.GetFileNameWithoutExtension(f.Name);
            var key = baseName;
            char flag = '\0';
            var m = FlaggedName().Match(baseName);
            if (m.Success)
            {
                key = m.Groups["pre"].Value + m.Groups["num"].Value;
                flag = char.ToUpperInvariant(m.Groups["flag"].Value[0]);
            }
            var groupKey = f.Folder + "/" + key;
            if (!groups.TryGetValue(groupKey, out var list)) groups[groupKey] = list = [];
            list.Add((f, m.Success, flag));
        }

        var items = new List<MediaItem>(groups.Count);
        foreach (var (groupKey, list) in groups)
        {
            var item = Build(groupKey, list);
            if (item is not null) items.Add(item);
        }
        return items
            .OrderByDescending(i => i.Date ?? DateTime.MinValue)
            .ThenByDescending(i => i.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static MediaItem? Build(string groupKey, List<(RemoteFile File, bool Flagged, char Flag)> list)
    {
        var plain = list.Where(x => !x.Flagged).Select(x => x.File).ToList();
        var image = plain.Where(f => IsImage(f.Extension)).OrderByDescending(f => f.Size).FirstOrDefault();
        var video = plain.Where(f => IsVideo(f.Extension)).OrderByDescending(f => f.Size).FirstOrDefault();

        var files = new List<MediaFile>();
        MediaKind kind;
        if (image is not null)
        {
            files.Add(new MediaFile(image, FileRole.Primary));
            if (video is not null)
            {
                files.Add(new MediaFile(video, FileRole.LiveVideo));
                kind = MediaKind.Live;
            }
            else
            {
                kind = image.Extension == "PNG" ? MediaKind.Screenshot : MediaKind.Photo;
            }
        }
        else if (video is not null)
        {
            files.Add(new MediaFile(video, FileRole.Primary));
            kind = MediaKind.Video;
        }
        else
        {
            // Solo versioni modificate (raro): la prima diventa il principale. Solo .AAE: niente da mostrare.
            var edited = list.Where(x => x.Flag == 'E' && (IsImage(x.File.Extension) || IsVideo(x.File.Extension)))
                .Select(x => x.File).OrderBy(f => IsVideo(f.Extension)).FirstOrDefault();
            if (edited is null) return null;
            files.Add(new MediaFile(edited, FileRole.Primary));
            kind = IsVideo(edited.Extension) ? MediaKind.Video : MediaKind.Photo;
        }

        var used = new HashSet<RemoteFile>(files.Select(f => f.File));
        foreach (var (f, _, flag) in list)
        {
            if (used.Contains(f)) continue;
            if (IsSidecar(f.Extension)) files.Add(new MediaFile(f, FileRole.Sidecar));
            else if (flag == 'E') files.Add(new MediaFile(f, FileRole.Edited));
            else if (IsImage(f.Extension) || IsVideo(f.Extension)) files.Add(new MediaFile(f, FileRole.Edited)); // stesso nome, altro formato
            // Altri file sconosciuti (es. .THM) restano fuori.
        }

        var primary = files[0].File;
        var date = primary.Created ?? primary.Modified
                   ?? files.Select(f => f.File.Created ?? f.File.Modified).FirstOrDefault(d => d is not null)
                   ?? DateFromFolder(primary.Folder);
        return new MediaItem
        {
            Id = groupKey,
            Folder = primary.Folder,
            BaseName = Path.GetFileNameWithoutExtension(primary.Name),
            Kind = kind,
            Files = files,
            Date = date,
        };
    }
}
