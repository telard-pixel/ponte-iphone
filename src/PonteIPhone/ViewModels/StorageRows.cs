using System.Windows.Media;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Transfer;

namespace PonteIPhone.ViewModels;

/// <summary>Una voce della barra della memoria (colore, larghezza in proporzione, etichetta).</summary>
public sealed class StorageSliceRow(StorageSlice slice, long capacity)
{
    public string Name => slice.Name;
    public string Size => TransferEngine.Format(slice.Bytes);
    /// <summary>Peso nella barra: i byte. Le voci piccolissime restano visibili (almeno lo 0,6%).</summary>
    public double Weight => Math.Max(slice.Bytes, capacity * 0.006);
    public string Percent => capacity > 0 ? $"{100.0 * slice.Bytes / capacity:0}%" : "";
    public bool IsFree => slice.Key == "free";
    public Brush Fill { get; } = BrushFor(slice.Key);
    public string Tooltip => $"{slice.Name}: {TransferEngine.Format(slice.Bytes)}";

    private static readonly Dictionary<string, Brush> Brushes = new()
    {
        ["apps"] = Make(0x2F, 0x80, 0xED),
        ["appdata"] = Make(0x27, 0xAE, 0x60),
        ["photos"] = Make(0xF2, 0xB8, 0x2C),
        ["music"] = Make(0xEB, 0x57, 0x57),
        ["books"] = Make(0x9B, 0x51, 0xE0),
        ["system"] = Make(0x6E, 0x6E, 0x73),
        ["other"] = Make(0xAE, 0xAE, 0xB2),
        ["free"] = Make(0x80, 0x80, 0x80, 0x30),
    };

    private static Brush BrushFor(string key) => Brushes.TryGetValue(key, out var b) ? b : Brushes["other"];

    private static Brush Make(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Un'app nell'elenco della pagina Spazio.</summary>
public sealed class AppRow(AppUsage app, double fraction)
{
    public string Name => app.Name;
    public string BundleId => app.BundleId;
    public string Total => TransferEngine.Format(app.Total);
    public string Detail => app.DataBytes > 0
        ? $"App {TransferEngine.Format(app.AppBytes)} · Documenti e dati {TransferEngine.Format(app.DataBytes)}"
        : $"App {TransferEngine.Format(app.AppBytes)}";
    /// <summary>Lunghezza della barretta rispetto all'app più grande (0..1).</summary>
    public double Fraction => fraction;
    public string Initial => string.IsNullOrWhiteSpace(app.Name) ? "?" : app.Name.Trim()[..1].ToUpperInvariant();
}
