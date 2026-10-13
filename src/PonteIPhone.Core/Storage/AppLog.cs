using System.Globalization;

namespace PonteIPhone.Core.Storage;

/// <summary>Registro testuale, un file al giorno, tenuto 30 giorni.</summary>
public sealed class AppLog
{
    private readonly string _folder;
    private readonly object _lock = new();

    public AppLog(string folder)
    {
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var f in Directory.EnumerateFiles(folder, "ponte-*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-30)) File.Delete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public string Folder => _folder;

    public string TodayPath => Path.Combine(_folder, $"ponte-{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO ", message);

    public void Warn(string message) => Write("AVVISO", message);

    public void Error(string context, Exception e) => Write("ERRORE", $"{context}: {e.GetType().Name}: {e.Message} (0x{e.HResult:X8})\n{e.StackTrace}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level} {message}{Environment.NewLine}";
        lock (_lock)
        {
            try { File.AppendAllText(TodayPath, line); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
