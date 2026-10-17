namespace PonteIPhone.Core.Model;

/// <summary>Elementi identici: uno resta sull'iPhone, gli altri si possono togliere senza perdere niente.</summary>
public sealed record DuplicateGroup(MediaItem Keep, IReadOnlyList<MediaItem> Copies)
{
    public long Saving => Copies.Sum(c => c.TotalSize);
}

/// <summary>
/// Doppioni veri, non "simili" (richiesta del 17/10: "vedere dove poter eliminare file o doppioni").
/// Due elementi sono doppioni solo se hanno gli stessi file (foto, video Live, versione modificata, .AAE) con la stessa
/// dimensione E lo stesso SHA-256: la dimensione serve solo a scegliere chi leggere, mai a decidere.
/// </summary>
public static class DuplicateFinder
{
    /// <summary>
    /// Gruppi di elementi con la stessa forma (tipo e dimensioni di tutti i file): gli unici che vale la pena leggere.
    /// Un elemento con un file vuoto (solo su iCloud) non entra: non se ne può controllare il contenuto.
    /// </summary>
    public static List<List<MediaItem>> Candidates(IEnumerable<MediaItem> items) =>
        items.Where(i => i.Files.Count > 0 && i.Files.All(f => f.File.Size > 0))
            .GroupBy(Shape, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.ToList())
            .ToList();

    /// <summary>File da leggere per confermare i candidati.</summary>
    public static IEnumerable<RemoteFile> FilesToHash(IEnumerable<List<MediaItem>> candidates) =>
        candidates.SelectMany(g => g).SelectMany(i => i.Files).Select(f => f.File);

    /// <summary>
    /// I gruppi confermati dal contenuto. <paramref name="hash"/> dà lo SHA-256 di un file, o null se non si è potuto
    /// leggere: quell'elemento resta fuori. Resta sul telefono il più vecchio (o il più recente), a parità il primo per cartella e nome.
    /// </summary>
    public static List<DuplicateGroup> Confirm(IEnumerable<List<MediaItem>> candidates, Func<RemoteFile, string?> hash, bool keepNewest = false)
    {
        var result = new List<DuplicateGroup>();
        foreach (var group in candidates)
        {
            var byContent = new Dictionary<string, List<MediaItem>>(StringComparer.Ordinal);
            foreach (var item in group)
            {
                var parts = new List<string>();
                foreach (var f in Ordered(item))
                {
                    var h = hash(f.File);
                    if (string.IsNullOrEmpty(h)) { parts = null; break; }
                    parts.Add($"{f.Role}:{f.File.Size}:{h.ToUpperInvariant()}");
                }
                if (parts is null) continue;
                var key = string.Join("|", parts);
                if (!byContent.TryGetValue(key, out var list)) byContent[key] = list = [];
                list.Add(item);
            }
            foreach (var same in byContent.Values.Where(l => l.Count > 1))
            {
                var ordered = (keepNewest
                        ? same.OrderByDescending(i => i.Date ?? DateTime.MinValue)
                        : same.OrderBy(i => i.Date ?? DateTime.MaxValue))
                    .ThenBy(i => i.Folder, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(i => i.BaseName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                result.Add(new DuplicateGroup(ordered[0], ordered.Skip(1).ToList()));
            }
        }
        return result.OrderByDescending(g => g.Saving).ToList();
    }

    private static IEnumerable<MediaFile> Ordered(MediaItem item) =>
        item.Files.OrderBy(f => f.Role).ThenBy(f => f.File.Size).ThenBy(f => f.File.Extension, StringComparer.OrdinalIgnoreCase);

    private static string Shape(MediaItem item) =>
        item.Kind + "#" + string.Join("|", Ordered(item).Select(f => $"{f.Role}:{f.File.Extension.ToUpperInvariant()}:{f.File.Size}"));
}
