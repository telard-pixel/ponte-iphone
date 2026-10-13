using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using ImageMagick;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;

namespace PonteIPhone.Services;

/// <summary>
/// Anteprime grandi: il file vero si scarica in una cache (massimo 1 GB, i più vecchi si cancellano)
/// e si decodifica con ImageMagick, così anche HEIC e DNG si vedono su qualsiasi PC.
/// </summary>
public sealed class PreviewService(IDeviceConnection conn, string deviceKey)
{
    private const long CacheLimit = 1L << 30;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly string _folder = Path.Combine(AppPaths.Previews,
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(deviceKey)))[..16]);

    /// <summary>Il file sul PC (scaricato ora o già in cache).</summary>
    public async Task<string> GetFileAsync(RemoteFile file, CancellationToken ct)
    {
        Directory.CreateDirectory(_folder);
        var name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(file.Key)))[..20] + "." + file.Extension.ToLowerInvariant();
        var path = Path.Combine(_folder, name);
        if (File.Exists(path) && new FileInfo(path).Length == file.Size) return path;

        await _one.WaitAsync(ct);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length == file.Size) return path;
            var part = path + ".part";
            await conn.DownloadAsync(file.ObjectId, part, null, ct);
            File.Move(part, path, overwrite: true);
            Trim();
            return path;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Immagine pronta da mostrare, lato lungo al massimo <paramref name="maxSide"/> pixel.</summary>
    public static ImageSource? Render(string path, int maxSide)
    {
        try
        {
            using var image = new MagickImage(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".heic" or ".heif")
                image.Orientation = OrientationType.TopLeft; // libheif l'ha già ruotata
            else
                image.AutoOrient();
            if (image.Width > maxSide || image.Height > maxSide)
                image.Resize(new MagickGeometry((uint)maxSide, (uint)maxSide) { Greater = true });
            image.Quality = 90;
            var bytes = image.ToByteArray(MagickFormat.Jpeg);
            return ThumbnailService.Decode(bytes, 0);
        }
        catch (Exception e) when (e is MagickException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private void Trim()
    {
        try
        {
            var files = new DirectoryInfo(_folder).GetFiles().OrderByDescending(f => f.LastAccessTimeUtc).ToList();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > CacheLimit) f.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
