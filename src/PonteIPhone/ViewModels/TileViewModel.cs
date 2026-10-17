using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Transfer;
using PonteIPhone.Services;

namespace PonteIPhone.ViewModels;

/// <summary>Una miniatura della griglia (un elemento della libreria).</summary>
public sealed partial class TileViewModel : ObservableObject
{
    private readonly ThumbnailService _thumbs;

    public TileViewModel(MediaItem item, ThumbnailService thumbs)
    {
        Item = item;
        _thumbs = thumbs;
    }

    public MediaItem Item { get; private set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isOnPc;
    [ObservableProperty] private bool _isFocused;

    /// <summary>Fai spazio: questa miniatura resta (doppione tenuto) o è una copia identica da togliere.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDupKeep), nameof(IsDupCopy))]
    private DupRole _dupRole;

    public bool IsDupKeep => DupRole == DupRole.Keep;
    public bool IsDupCopy => DupRole == DupRole.Copy;

    /// <summary>Il telefono non ha una miniatura per questo file: si mostra l'icona del tipo.</summary>
    [ObservableProperty] private bool _hasNoThumbnail;

    /// <summary>La miniatura è in un contenitore visibile della griglia (le altre non si caricano).</summary>
    public bool IsVisible { get; set; }

    internal bool ThumbnailRequested { get; set; }

    public void Update(MediaItem item)
    {
        if (ReferenceEquals(item, Item)) return;
        Item = item;
        OnPropertyChanged(nameof(Item));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(Badge));
    }

    public ImageSource? Thumbnail
    {
        get
        {
            var cached = _thumbs.TryGet(this);
            if (cached is null) _thumbs.Request(this);
            return cached;
        }
    }

    internal void OnThumbnailReady(bool missing)
    {
        HasNoThumbnail = missing;
        OnPropertyChanged(nameof(Thumbnail));
    }

    public bool IsLive => Item.Kind == MediaKind.Live;
    public bool IsVideo => Item.Kind == MediaKind.Video;

    /// <summary>Icona per le miniature mancanti: foto, video, schermata.</summary>
    public string TypeGlyph => Item.Kind switch
    {
        MediaKind.Video => "",
        MediaKind.Screenshot => "",
        _ => "",
    };

    public string Badge => Item.Kind switch
    {
        MediaKind.Live => "LIVE",
        MediaKind.Video => Item.Primary.Extension,
        MediaKind.Screenshot => "PNG",
        _ => Item.Primary.Extension is "HEIC" or "JPG" or "JPEG" ? "" : Item.Primary.Extension,
    };

    public string Title => Item.BaseName;

    public string DateText => Item.Date is { } d
        ? d.ToString("d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo("it-IT"))
        : "Data sconosciuta";

    public string KindText => Item.Kind switch
    {
        MediaKind.Live => "Live Photo",
        MediaKind.Video => "Video",
        MediaKind.Screenshot => "Screenshot",
        _ => Item.HasEdited ? "Foto (modificata)" : "Foto",
    };

    public string SizeText => TransferEngine.Format(Item.TotalSize);

    public IEnumerable<FileLine> Files => Item.Files.Select(f => new FileLine(
        f.File.Name,
        TransferEngine.Format(f.File.Size),
        f.Role switch
        {
            FileRole.LiveVideo => "video Live",
            FileRole.Edited => "modificata",
            FileRole.Sidecar => "modifiche Apple",
            _ => "originale",
        }));
}

public sealed record FileLine(string Name, string Size, string Role);

public enum DupRole { None, Keep, Copy }

/// <summary>Intestazione di un mese nella griglia.</summary>
public sealed class HeaderRow(string title, string subtitle, IReadOnlyList<TileViewModel> tiles, string action = "Seleziona")
{
    public string Title { get; } = title;
    /// <summary>Testo del pulsante che seleziona il gruppo (il giorno, il mese, i doppioni).</summary>
    public string Action { get; } = action;
    public string Subtitle { get; } = subtitle;
    public IReadOnlyList<TileViewModel> Tiles { get; } = tiles;
}

/// <summary>Una riga di miniature: la griglia è una lista virtualizzata di righe, così regge decine di migliaia di elementi.</summary>
public sealed class TileRow(IReadOnlyList<TileViewModel> tiles)
{
    public IReadOnlyList<TileViewModel> Tiles { get; } = tiles;
}
