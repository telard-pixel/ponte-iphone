using System.Globalization;

namespace PonteIPhone.Core.Transfer;

/// <summary>Dove finisce ogni file sul PC.</summary>
public sealed class DestinationPlanner(string root, bool byYearMonth)
{
    public static readonly string[] Months =
        ["Gennaio", "Febbraio", "Marzo", "Aprile", "Maggio", "Giugno", "Luglio", "Agosto", "Settembre", "Ottobre", "Novembre", "Dicembre"];

    public string Root => root;

    /// <summary>"…\2026\10-Ottobre" (o la cartella principale se le sottocartelle sono spente).</summary>
    public string FolderFor(DateTime? date)
    {
        if (!byYearMonth) return root;
        if (date is not { } d) return Path.Combine(root, "Senza data");
        return Path.Combine(root, d.Year.ToString("0000", CultureInfo.InvariantCulture), MonthFolder(d.Month));
    }

    public static string MonthFolder(int month) => $"{month:00}-{Months[month - 1]}";

    /// <summary>Percorso relativo da mostrare ("2026\10-Ottobre").</summary>
    public string Relative(string folder) =>
        Path.GetRelativePath(root, folder) is var r && r != "." ? r : Path.GetFileName(root.TrimEnd('\\', '/'));

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Nome valido su Windows: caratteri vietati sostituiti, niente nomi riservati, niente punto o spazio finale.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = new HashSet<char>(['<', '>', ':', '"', '/', '\\', '|', '?', '*']);
        var chars = name.Select(c => c < 32 || invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.', ' ');
        if (s.Length == 0) s = "file";
        var stem = Path.GetFileNameWithoutExtension(s);
        if (Reserved.Contains(stem)) s = "_" + s;
        if (s.Length > 180) s = s[..(180 - Path.GetExtension(s).Length)] + Path.GetExtension(s);
        return s;
    }

    /// <summary>"IMG_1234.HEIC" con n=2 → "IMG_1234 (2).HEIC".</summary>
    public static string WithSuffix(string fileName, int n) =>
        n <= 1 ? fileName : $"{Path.GetFileNameWithoutExtension(fileName)} ({n}){Path.GetExtension(fileName)}";
}
