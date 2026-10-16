using System.Text.Json.Serialization;

namespace PonteIPhone.Core.Model;

/// <summary>Una voce della barra della memoria, come in iTunes (App, Foto, Musica…).</summary>
/// <param name="Key">Nome fisso per i colori: apps, appdata, photos, music, books, other, system, free.</param>
public sealed record StorageSlice(string Key, string Name, long Bytes);

/// <summary>Un'app installata dall'utente: programma (AppBytes) e documenti/dati (DataBytes).</summary>
public sealed record AppUsage(string Name, string BundleId, long AppBytes, long DataBytes)
{
    [JsonIgnore]
    public long Total => AppBytes + DataBytes;
}

/// <summary>
/// Come è suddivisa la memoria del telefono. Le voci sommate fanno <see cref="Capacity"/>:
/// quello che il telefono non dice per categoria finisce in "Altro".
/// </summary>
/// <param name="Notes">Cosa non si è potuto leggere (per il rapporto), vuoto se tutto bene.</param>
public sealed record StorageBreakdown(long Capacity, long Free, IReadOnlyList<StorageSlice> Slices, IReadOnlyList<AppUsage> Apps,
    IReadOnlyList<string> Notes)
{
    [JsonIgnore]
    public long Used => Capacity > Free ? Capacity - Free : 0;

    /// <summary>
    /// Mette insieme i numeri del telefono. <paramref name="capacity"/> è la memoria totale dichiarata,
    /// <paramref name="system"/> la parte riservata a iOS (0 se non si sa); le altre voci sono già misurate.
    /// "Altro" = usato meno tutto ciò che si è misurato (libreria Foto, cache, iCloud, Messaggi…).
    /// </summary>
    public const long MinSlice = 1_000_000;

    public static StorageBreakdown Build(long capacity, long free, long system, IReadOnlyList<AppUsage> apps,
        long photos, long music, long books, IReadOnlyList<string> notes)
    {
        free = Math.Clamp(free, 0, Math.Max(0, capacity));
        long appBytes = apps.Sum(a => a.AppBytes), dataBytes = apps.Sum(a => a.DataBytes);
        var measured = new List<StorageSlice>
        {
            new("apps", "App", appBytes),
            new("appdata", "Dati delle app", dataBytes),
            new("photos", "Foto e video", photos),
            new("music", "Musica", music),
            new("books", "Libri", books),
            new("system", "Sistema (iOS)", system),
        };
        long used = Math.Max(0, capacity - free);
        long known = measured.Sum(s => s.Bytes);
        // Se le misure superano l'usato (dimensioni delle app arrotondate dal telefono) si scala tutto in proporzione:
        // la barra deve restare onesta sul totale, che è il numero più sicuro.
        if (known > used && known > 0)
        {
            double k = (double)used / known;
            measured = measured.Select(s => s with { Bytes = (long)(s.Bytes * k) }).ToList();
            // Gli arrotondamenti vanno alla voce più grande, non in un "Altro" di pochi byte.
            int biggest = measured.IndexOf(measured.MaxBy(s => s.Bytes)!);
            measured[biggest] = measured[biggest] with { Bytes = measured[biggest].Bytes + used - measured.Sum(s => s.Bytes) };
            known = used;
        }
        // Voci sotto 1 MB (es. "Libri 117 KB · 0%" del 16/10) dicono solo rumore: vanno in "Altro".
        var slices = measured.Where(s => s.Bytes >= MinSlice).ToList();
        known = slices.Sum(s => s.Bytes);
        if (used - known > 0) slices.Add(new StorageSlice("other", "Altro", used - known));
        slices.Add(new StorageSlice("free", "Libero", free));
        return new StorageBreakdown(capacity, free, slices, apps.OrderByDescending(a => a.Total).ToList(), notes);
    }
}
