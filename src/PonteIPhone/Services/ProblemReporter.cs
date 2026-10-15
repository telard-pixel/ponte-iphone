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
    private readonly DateTime _startedUtc = DateTime.UtcNow;

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
        // Prima su disco, poi l'invio: se l'utente chiude l'app a metà, il rapporto parte al prossimo avvio.
        var file = SavePending(json);
        var task = Task.Run(async () =>
        {
            var (ok, discard, _) = await SendAsync(reason, json);
            if ((ok || discard) && file is not null) TryDelete(file);
        });
        lock (_running) _running.Add(task);
        return true;
    }

    private readonly List<Task> _running = [];

    /// <summary>Alla chiusura: aspetta gli invii in corso, per poco (quelli non finiti restano in coda su disco).</summary>
    public async Task FlushAsync(TimeSpan max)
    {
        Task[] tasks;
        lock (_running) tasks = _running.Where(t => !t.IsCompleted).ToArray();
        if (tasks.Length > 0) await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(max));
    }

    /// <summary>Invio chiesto dall'utente: aspetta l'esito e lo restituisce in parole semplici.</summary>
    public async Task<(bool Ok, string Message)> SendNowAsync(string reason, Func<string> buildText)
    {
        var json = BuildJson(reason, buildText);
        var (ok, discard, message) = await SendAsync(reason, json);
        if (!ok && !discard) SavePending(json);
        return (ok, message);
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
            // Solo quelli delle sessioni precedenti: quelli di adesso li sta già mandando Report.
            try { if (File.GetLastWriteTimeUtc(f) >= _startedUtc) continue; json = await File.ReadAllTextAsync(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            var (ok, discard, _) = await SendAsync("rimasto indietro", json);
            if (ok || discard) TryDelete(f); // rifiutato per sempre dal server: non deve bloccare gli altri
            else return; // ancora niente rete o server: si riprova la prossima volta
        }
    }

    private static string BuildJson(string reason, Func<string> buildText)
    {
        string text;
        try { text = buildText(); }
        catch (Exception e) { text = "Rapporto non costruito: " + e.Message; }
        if (text.Length > MaxChars) text = text[^MaxChars..];
        // Lettere accentate così come sono (non \u00e8): il rapporto resta entro il limite del ricevitore.
        return JsonSerializer.Serialize(new { app = "ponte-iphone", versione = UpdateService.Current.ToString(), motivo = reason, testo = text },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <returns>Ok; Discard = rifiutato per sempre (4xx diverso da 408/429), inutile riprovare.</returns>
    private async Task<(bool Ok, bool Discard, string Message)> SendAsync(string reason, string json)
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
                return (true, false, "Rapporto inviato.");
            }
            int code = (int)resp.StatusCode;
            LastResult = $"non accettato dal server: {code} ({reason})";
            log.Warn($"Rapporto non accettato: {code}");
            bool discard = code is >= 400 and < 500 and not 408 and not 429;
            return (false, discard, code == 429
                ? "Troppi rapporti in poco tempo: riprova tra un'ora."
                : "Il server non ha accettato il rapporto.");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Qualunque errore (rete, antivirus, proxy): si dice e il rapporto resta in coda.
            LastResult = $"non inviato: {e.Message} ({reason})";
            log.Warn("Rapporto non inviato: " + e.Message);
            return (false, false, "Rapporto non inviato: il PC non riesce a raggiungere il server (rete o antivirus).");
        }
    }

    private string? SavePending(string json)
    {
        try
        {
            Directory.CreateDirectory(PendingFolder);
            var path = Path.Combine(PendingFolder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            foreach (var old in Directory.GetFiles(PendingFolder, "*.json").Order(StringComparer.Ordinal).SkipLast(MaxPending)) TryDelete(old);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("Rapporto non conservato: " + e.Message);
            return null;
        }
    }

    /// <summary>Rapporti in coda, per la diagnostica.</summary>
    public static int PendingCount
    {
        get
        {
            try { return Directory.Exists(PendingFolder) ? Directory.GetFiles(PendingFolder, "*.json").Length : 0; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
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
