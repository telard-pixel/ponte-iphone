using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace PonteIPhone.Core.Transfer;

/// <summary>
/// Data e ora di scatto lette dal file stesso, come le vedrebbe l'utente sul telefono
/// (ora locale del luogo dello scatto). Serve a scegliere la cartella Anno\Mese giusta.
/// </summary>
public static partial class CaptureDate
{
    public static DateTime? FromFile(string path)
    {
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(path);
        }
        catch (Exception e) when (e is ImageProcessingException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException or IndexOutOfRangeException
                                      or NotSupportedException or OverflowException or FormatException)
        {
            return null;
        }
        return FromDirectories(dirs);
    }

    internal static DateTime? FromDirectories(IReadOnlyList<MetadataExtractor.Directory> dirs)
    {
        // 1. Foto: EXIF DateTimeOriginal (ora del luogo dello scatto, senza fuso).
        foreach (var sub in dirs.OfType<ExifSubIfdDirectory>())
        {
            if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dto) && Plausible(dto)) return dto;
            if (sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var dtd) && Plausible(dtd)) return dtd;
        }

        // 2. Video Apple: com.apple.quicktime.creationdate, con fuso ("2026-10-12T23:30:05+0200").
        foreach (var d in dirs)
        {
            if (!d.Name.Contains("QuickTime", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var t in d.Tags)
            {
                if (!t.Name.Contains("Creation Date", StringComparison.OrdinalIgnoreCase)) continue;
                var value = d.GetObject(t.Type);
                if (value is DateTimeOffset off && Plausible(off.DateTime)) return off.DateTime;
                var text = value as string ?? t.Description;
                if (text is not null && TryParseAppleDate(text, out var local)) return local;
            }
        }

        // 3. Video: data del filmato, in UTC per specifica: va portata all'ora locale del PC.
        foreach (var mvhd in dirs.OfType<QuickTimeMovieHeaderDirectory>())
        {
            if (mvhd.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out var utc) && Plausible(utc))
                return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        }

        // 4. Ultima spiaggia: DateTime dell'IFD0 (modifica), poi PNG/XMP non gestiti.
        foreach (var ifd0 in dirs.OfType<ExifIfd0Directory>())
        {
            if (ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var dt) && Plausible(dt)) return dt;
        }
        return null;
    }

    [GeneratedRegex(@"^(?<dt>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(\.\d+)?(?<tz>Z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex AppleDate();

    /// <summary>"2026-10-12T23:30:05+0200" → 12/10/2026 23:30:05 (l'ora segnata sul telefono, non convertita).</summary>
    public static bool TryParseAppleDate(string text, out DateTime local)
    {
        local = default;
        var m = AppleDate().Match(text.Trim());
        if (!m.Success) return false;
        if (!DateTime.TryParseExact(m.Groups["dt"].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var wall)) return false;
        if (!Plausible(wall)) return false;
        if (m.Groups["tz"].Value == "Z")
        {
            // Solo UTC, senza il fuso del luogo: si mostra nell'ora del PC.
            local = DateTime.SpecifyKind(wall, DateTimeKind.Utc).ToLocalTime();
            return true;
        }
        local = wall;
        return true;
    }

    /// <summary>Scarta le date "vuote" (1904 dei QuickTime, 1970, 2000-01-01 di alcuni telefoni) e quelle nel futuro.</summary>
    public static bool Plausible(DateTime d) => d.Year >= 1990 && d <= DateTime.Now.AddDays(2);
}
