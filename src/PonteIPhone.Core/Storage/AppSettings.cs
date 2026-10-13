using System.Text.Json;

namespace PonteIPhone.Core.Storage;

/// <summary>Impostazioni dell'utente (JSON nella cartella dati, con copia di riserva).</summary>
public sealed class AppSettings
{
    public string DestinationRoot { get; set; } = DefaultDestination();
    public bool ByYearMonth { get; set; } = true;
    public bool SkipAlreadyCopied { get; set; } = true;
    public bool ConvertHeicToJpeg { get; set; }
    public bool KeepHeicOriginal { get; set; } = true;
    public bool IncludeEdited { get; set; } = true;
    public bool IncludeSidecars { get; set; }
    public bool ConfirmMove { get; set; } = true;
    public bool KeepAwake { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;
    public int ThumbnailSize { get; set; } = 120;
    public bool NewestFirst { get; set; } = true;
    public bool ShowInspector { get; set; } = true;
    /// <summary>Telefoni (chiave) che hanno rifiutato la cancellazione: Sposta lo dice prima di partire.</summary>
    public List<string> DeleteRefusedDevices { get; set; } = [];
    /// <summary>Telefoni su cui la cancellazione ha funzionato almeno una volta.</summary>
    public List<string> DeleteWorkedDevices { get; set; } = [];
    public List<string> RecentDestinations { get; set; } = [];

    public static string DefaultDestination() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Foto iPhone");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(candidate), Json);
                    if (s is not null) return s.Normalized();
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return new AppSettings();
    }

    private AppSettings Normalized()
    {
        if (string.IsNullOrWhiteSpace(DestinationRoot)) DestinationRoot = DefaultDestination();
        ThumbnailSize = Math.Clamp(ThumbnailSize, 80, 220);
        return this;
    }

    /// <summary>Scrive prima in un file temporaneo: un'interruzione a metà non perde le impostazioni.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        File.Move(tmp, path, overwrite: true);
    }

    public void RememberDestination(string folder)
    {
        RecentDestinations.RemoveAll(d => string.Equals(d, folder, StringComparison.OrdinalIgnoreCase));
        RecentDestinations.Insert(0, folder);
        if (RecentDestinations.Count > 5) RecentDestinations.RemoveRange(5, RecentDestinations.Count - 5);
    }
}

/// <summary>Cartelle dell'app in %LOCALAPPDATA%\PonteIPhone.</summary>
public static class AppPaths
{
    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PonteIPhone");

    public static string Settings => Path.Combine(Root, "impostazioni.json");
    public static string Registry => Path.Combine(Root, "copie.db");
    public static string Logs => Path.Combine(Root, "registro");
    public static string Thumbnails => Path.Combine(Root, "miniature");
    public static string Previews => Path.Combine(Root, "anteprime");
    public static string Updates => Path.Combine(Root, "aggiornamento");
}
