using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using PonteIPhone.Core.Storage;

namespace PonteIPhone.Services;

/// <summary>
/// Quando qualcosa va storto, manda da solo il rapporto (diagnostica + registro di oggi, niente foto)
/// al ricevitore di sola lettura sul server dell'utente: così non deve salvarlo e mandarlo lui.
/// Scelta dell'utente del 14/10. Nessuna chiave dentro l'app (il repository è pubblico): il ricevitore
/// accetta solo rapporti, con i suoi limiti. Qui: mai due volte lo stesso motivo in una sessione,
/// al massimo 10 invii al giorno, sempre in sottofondo.
/// </summary>
public sealed class ProblemReporter(AppLog log)
{
    public const string Endpoint = "https://server-log.duckdns.org/api/rapporto";
    private const int MaxPerDay = 10;
    private const int MaxChars = 400_000;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);

    /// <summary>True se il rapporto parte (o è già partito per questo motivo in questa sessione).</summary>
    public bool Report(string reason, Func<string> buildText)
    {
        lock (_sent)
        {
            if (_sent.Contains(reason)) return true;
            if (!TakeDailySlot()) return false;
            _sent.Add(reason);
        }
        string text;
        try { text = buildText(); }
        catch (Exception e) { text = "Rapporto non costruito: " + e.Message; }
        if (text.Length > MaxChars) text = text[^MaxChars..];
        var payload = new { app = "ponte-iphone", versione = UpdateService.Current.ToString(), motivo = reason, testo = text };
        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await Http.PostAsJsonAsync(Endpoint, payload);
                log.Info(resp.IsSuccessStatusCode ? $"Rapporto inviato ({reason})" : $"Rapporto non accettato: {(int)resp.StatusCode}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                log.Warn("Rapporto non inviato: " + e.Message);
            }
        });
        return true;
    }

    private static bool TakeDailySlot()
    {
        var path = Path.Combine(AppPaths.Root, "rapporti-inviati.txt");
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        try
        {
            int count = 0;
            if (File.Exists(path) && File.ReadAllText(path).Split(' ') is [var day, var n] && day == today) int.TryParse(n, out count);
            if (count >= MaxPerDay) return false;
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(path, $"{today} {count + 1}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
