using ImageMagick;

namespace PonteIPhone.Core.Transfer;

public interface IImageConverter
{
    /// <summary>Converte una foto HEIC in JPG mantenendo EXIF (data, GPS), profilo colore e orientamento corretto.</summary>
    void HeicToJpeg(string source, string destination, int quality);
}

/// <summary>
/// Conversione con ImageMagick incluso nell'app (libheif): funziona anche su PC senza le
/// estensioni HEIF del Microsoft Store.
/// </summary>
public sealed class MagickImageConverter : IImageConverter
{
    public void HeicToJpeg(string source, string destination, int quality)
    {
        using var image = new MagickImage(source);
        // libheif restituisce i pixel già ruotati secondo il file: se l'EXIF dicesse ancora "ruota di 90°",
        // i visualizzatori ruoterebbero una seconda volta. Si dichiara quindi l'orientamento normale.
        image.Orientation = OrientationType.TopLeft;
        var exif = image.GetExifProfile();
        if (exif is not null)
        {
            exif.SetValue(ExifTag.Orientation, (ushort)1);
            image.SetProfile(exif);
        }
        image.Quality = (uint)Math.Clamp(quality, 50, 100);
        image.Write(destination, MagickFormat.Jpeg);
    }
}
