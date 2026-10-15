using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PonteIPhone.Core.Storage;

namespace PonteIPhone.Services;

/// <summary>
/// Quando qualcosa va storto, manda da solo il rapporto (diagnostica + registro di oggi, niente foto)
/// al ricevitore di sola lettura sul server dell'utente: così non deve salvarlo e mandarlo lui.
/// Scelta dell'utente del 14/10. Nessuna chiave dentro l'app (il repository è pubblico): il ricevitore
/// accetta solo rapporti, con i suoi limiti. Qui: mai due volte lo stesso motivo in una sessione,
/// al massimo 10 invii automatici al giorno, sempre in sottofondo.
/// Un rapporto che non parte (rete, antivirus, server) resta in una cartella e si riprova all'avvio
/// successivo: il 15/10 nessun rapporto era arrivato e non c'era modo di sapere perché.
/// </summary>
public sealed class ProblemReporter(AppLog log)
{
    public const string Endpoint = "https://server-log.duckdns.org/api/rapporto";
    private const int MaxPerDay = 10;
    private const int MaxChars = 400_000;
    private const int MaxPending = 5;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static string PendingFolder => Path.Combine(AppPaths.Root, "rapporti-da-inviare");
    private readonly HashSet<string> _sent = new(StringComparer.Ordinal);

    /// <summary>Esito dell'ultimo invio, per la diagnostica.</summary>
    public string LastResult { get; private set; } = "nessun invio in questa sessione";

    /// <summary>True se il rapporto parte (o è già partito per questo motivo in questa sessione).</summary>
    public bool Report(string reason, Func<string> buildText)
    {
        lock (_sent)
        {
            if (_sent.Contains(reason)) return true;
            if (!TakeDailySlot()) return false;
            _sent.Add(reason);
        }
        var json = BuildJson(reason, buildText);
        _ = Task.Run(async () =>
        {
            var (ok, _) = await SendAsync(reason, json);
            if (!ok) SavePending(json);
        });
        return true;
    }

    /// <summary>Invio chiesto dall'utente: aspetta l'esito e lo restituisce in parole semplici.</summary>
    public async Task<(bool Ok, string Message)> SendNowAsync(string reason, Func<string> buildText)
    {
        var json = BuildJson(reason, buildText);
        var result = await SendAsync(reason, json);
        if (!result.Ok) SavePending(json);
        return result;
    }

    /// <summary>All'avvio: rimanda i rapporti rimasti indietro, dal più vecchio.</summary>
    public async Task RetryPendingAsync()
    {
        string[] files;
        try { files = Directory.Exists(PendingFolder) ? Directory.GetFiles(PendingFolder, "*.json") : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        foreach (var f in files.Order(StringComparer.Ordinal))
        {
            string json;
            try { json = await File.ReadAllTextAsync(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var (ok, _) = await SendAsync("rimasto indietro", json);
            if (!ok) return; // ancora niente rete o server: si riprova la prossima volta
            TryDelete(f);
        }
    }

    private static string BuildJson(string reason, Func<string> buildText)
    {
        string text;
        try { text = buildText(); }
        catch (Exception e) { text = "Rapporto non costruito: " + e.Message; }
        if (text.Length > MaxChars) text = text[^MaxChars..];
        return JsonSerializer.Serialize(new { app = "ponte-iphone", versione = UpdateService.Current.ToString(), motivo = reason, testo = text });
    }

    private async Task<(bool Ok, string Message)> SendAsync(string reason, string json)
    {
        try
        {
            // StringContent dichiara la lunghezza: il JSON "a pezzi" di PostAsJsonAsync il ricevitore lo rifiutava (411).
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(Endpoint, content);
            if (resp.IsSuccessStatusCode)
            {
                LastResult = $"inviato alle {DateTime.Now:HH:mm} ({reason})";
                log.Info($"Rapporto inviato ({reason})");
                return (true, "Rapporto inviato.");
            }
            LastResult = $"non accettato dal server: {(int)resp.StatusCode} ({reason})";
            log.Warn($"Rapporto non accettato: {(int)resp.StatusCode}");
            return (false, $"Il server non ha accettato il rapporto (codice {(int)resp.StatusCode}).");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            LastResult = $"non inviato: {e.Message} ({reason})";
            log.Warn("Rapporto non inviato: " + e.Message);
            return (false, "Rapporto non inviato: " + e.Message);
        }
    }

    private void SavePending(string json)
    {
        try
        {
            Directory.CreateDirectory(PendingFolder);
            File.WriteAllText(Path.Combine(PendingFolder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.json"), json);
            foreach (var old in Directory.GetFiles(PendingFolder, "*.json").Order(StringComparer.Ordinal).SkipLast(MaxPending)) TryDelete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("Rapporto non conservato: " + e.Message);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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
