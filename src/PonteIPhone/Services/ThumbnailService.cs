using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;
using PonteIPhone.ViewModels;

namespace PonteIPhone.Services;

/// <summary>
/// Miniature: chieste al telefono una alla volta (prima le ultime richieste, cioè quelle sullo schermo),
/// salvate su disco per le aperture successive e tenute in memoria solo le più recenti.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    private const int MemoryLimit = 900;
    private const int DecodeWidth = 260;

    private readonly IDeviceConnection _conn;
    private readonly string _cacheFolder;
    private readonly Dispatcher _dispatcher;
    private readonly AppLog _log;
    private readonly LinkedList<TileViewModel> _stack = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> _memory = new();
    private readonly LinkedList<(string Key, ImageSource Image)> _lru = new();
    private readonly HashSet<string> _missing = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _pumping;

    public int Fetched { get; private set; }
    public int MissingCount => _missing.Count;
    public int Errors { get; private set; }

    public ThumbnailService(IDeviceConnection conn, string deviceKey, Dispatcher dispatcher, AppLog log)
    {
        _conn = conn;
        _dispatcher = dispatcher;
        _log = log;
        _cacheFolder = Path.Combine(AppPaths.Thumbnails, Hash(deviceKey)[..16]);
        try { Directory.CreateDirectory(_cacheFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string KeyOf(TileViewModel t) => t.Item.Primary.Key;

    public ImageSource? TryGet(TileViewModel tile)
    {
        var key = KeyOf(tile);
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Image;
            }
            if (_missing.Contains(key) && !tile.HasNoThumbnail)
                _dispatcher.BeginInvoke(() => tile.OnThumbnailReady(missing: true));
            return null;
        }
    }

    public void Request(TileViewModel tile)
    {
        lock (_lock)
        {
            // Una richiesta arriva solo da una tessera che si sta disegnando: è visibile.
            tile.IsVisible = true;
            if (tile.ThumbnailRequested || _missing.Contains(KeyOf(tile))) return;
            tile.ThumbnailRequested = true;
            _stack.AddFirst(tile);
            if (_pumping) return;
            _pumping = true;
        }
        _ = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TileViewModel? tile;
            lock (_lock)
            {
                // Le miniature uscite dallo schermo si saltano: si richiederanno quando tornano visibili.
                while (_stack.First is { } first && !first.Value.IsVisible)
                {
                    first.Value.ThumbnailRequested = false;
                    _stack.RemoveFirst();
                }
                tile = _stack.First?.Value;
                if (tile is null)
                {
                    _pumping = false;
                    return;
                }
                _stack.RemoveFirst();
                // Fuori dalla coda: se in futuro esce dalla memoria, la tessera potrà chiederla di nuovo.
                tile.ThumbnailRequested = false;
            }

            bool known;
            lock (_lock) known = _memory.ContainsKey(KeyOf(tile));
            if (known)
            {
                _ = _dispatcher.BeginInvoke(() => tile.OnThumbnailReady(missing: false));
                continue;
            }

            try
            {
                var image = await LoadAsync(tile);
                if (image is not null)
                {
                    Remember(KeyOf(tile), image);
                    _ = _dispatcher.BeginInvoke(() => tile.OnThumbnailReady(missing: false));
                }
                else
                {
                    lock (_lock) _missing.Add(KeyOf(tile));
                    _ = _dispatcher.BeginInvoke(() => tile.OnThumbnailReady(missing: true));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Errors++;
                tile.ThumbnailRequested = false;
                if (Errors <= 5) _log.Warn($"Miniatura di {tile.Item.Primary.Name}: {e.Message}");
                // Telefono bloccato o scollegato: inutile insistere subito con le altre.
                if (e is DeviceException { Kind: DeviceErrorKind.Gone or DeviceErrorKind.Transient })
                    await Task.Delay(1500, _cts.Token).ContinueWith(_ => { });
            }
        }
    }

    private async Task<ImageSource?> LoadAsync(TileViewModel tile)
    {
        var primary = tile.Item.Primary;
        var path = Path.Combine(_cacheFolder, Hash(primary.Key)[..24] + ".jpg");
        byte[]? bytes = null;
        if (File.Exists(path))
        {
            try { bytes = await File.ReadAllBytesAsync(path, _cts.Token); }
            catch (IOException) { }
        }
        if (bytes is null)
        {
            bytes = await _conn.GetThumbnailAsync(primary.ObjectId, _cts.Token);
            Fetched++;
            if (bytes is null || bytes.Length == 0) return null;
            try { await File.WriteAllBytesAsync(path, bytes, _cts.Token); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return Decode(bytes, DecodeWidth);
    }

    /// <summary>JPEG/PNG → immagine congelata (usabile da qualsiasi thread), già rimpicciolita.</summary>
    public static ImageSource? Decode(byte[] bytes, int width)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.DecodePixelWidth = width;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Remember(string key, ImageSource image)
    {
        lock (_lock)
        {
            if (_memory.TryGetValue(key, out var existing)) _lru.Remove(existing);
            var node = _lru.AddFirst((key, image));
            _memory[key] = node;
            while (_lru.Count > MemoryLimit)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _memory.Remove(last.Value.Key);
            }
        }
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    public void Dispose() => _cts.Cancel();
}
