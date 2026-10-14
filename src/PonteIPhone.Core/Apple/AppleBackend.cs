using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ImageMagick;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Apple;

/// <summary>Il servizio Apple non è utilizzabile per questo telefono: si usa la via di Windows (WPD).</summary>
public sealed class AppleUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// iPhone attraverso il servizio Apple (come iTunes e Dispositivi Apple): usbmux → lockdown con
/// l'abbinamento → AFC. A differenza di WPD non dipende da come Windows ha visto il telefono al
/// collegamento, e gli ID dei file sono i loro percorsi, uguali anche dopo un ricollegamento.
/// Non cancella: cancellare file sotto DCIM scavalcherebbe l'app Foto (vedi <see cref="SmartBackend"/>).
/// </summary>
public sealed partial class AppleBackend : IDeviceBackend
{
    public const string IdPrefix = "apple:";
    private const string Dcim = "/DCIM";

    private readonly UsbmuxClient _mux;
    private AfcClient? _afc;
    private readonly List<string> _notes = [];

    public AppleBackend(UsbmuxClient? mux = null)
    {
        _mux = mux ?? new UsbmuxClient();
    }

    public IReadOnlyList<DeviceInfo> ListDevices() =>
        _mux.ListDevices()
            .Where(d => !d.ConnectionType.Equals("Network", StringComparison.OrdinalIgnoreCase))
            .GroupBy(d => d.Udid).Select(g => g.First())
            .Select(d => new DeviceInfo(IdPrefix + d.Udid, "", "Apple Inc.", "iPhone", d.Udid, ""))
            .ToList();

    public OpenResult Open(string deviceId)
    {
        CloseAfc();
        _notes.Clear();
        var udid = deviceId.StartsWith(IdPrefix, StringComparison.Ordinal) ? deviceId[IdPrefix.Length..] : deviceId;
        UsbmuxDevice dev;
        try
        {
            dev = _mux.ListDevices().FirstOrDefault(d => d.Udid == udid && !d.ConnectionType.Equals("Network", StringComparison.OrdinalIgnoreCase))
                  ?? throw new DeviceException("iPhone non collegato", DeviceErrors.DeviceNotConnected);
        }
        catch (UsbmuxException e)
        {
            throw new AppleUnavailableException(e.Message, e);
        }

        var pair = ReadPair(udid);
        var info = new DeviceInfo(deviceId, "", "Apple Inc.", "iPhone", udid, "");
        if (pair is null)
        {
            // Mai toccato "Autorizza" su questo PC (o abbinamento non leggibile): ci pensa WPD.
            throw new AppleUnavailableException("nessun abbinamento con questo PC leggibile dal servizio Apple");
        }

        LockdownClient lockdown;
        try
        {
            lockdown = new LockdownClient(_mux.Connect(dev.DeviceId, LockdownClient.Port));
        }
        catch (UsbmuxException e)
        {
            throw new DeviceException("iPhone non raggiungibile: " + e.Message, DeviceErrors.DeviceNotConnected, e);
        }

        try
        {
            try
            {
                lockdown.StartSession(pair);
            }
            catch (LockdownException e) when (e.Error is "InvalidHostID" or "PairingProhibited" or "SslFailed" or "InvalidPairRecord")
            {
                throw new AppleUnavailableException("abbinamento non accettato dal telefono: " + e.Message);
            }

            var values = SafeValues(lockdown);
            info = new DeviceInfo(deviceId,
                values.GetString("DeviceName") ?? "",
                "Apple Inc.",
                values.GetString("ProductType") is { Length: > 0 } pt ? pt : "iPhone",
                udid,
                values.GetString("ProductVersion") ?? "");

            (int Port, bool Ssl) service;
            try
            {
                service = lockdown.StartService(AfcClient.Service);
            }
            catch (LockdownException e) when (pair.EscrowBag is not null)
            {
                // Telefono bloccato: con la escrow bag dell'abbinamento il servizio si apre lo stesso.
                _notes.Add($"AFC senza escrow bag: {e.Error}, riprovo con");
                try
                {
                    service = lockdown.StartService(AfcClient.Service, pair.EscrowBag);
                }
                catch (LockdownException e2)
                {
                    _notes.Add("AFC rifiutato: " + e2.Error);
                    return new OpenResult(info, null, OpenState.LockedOrNotTrusted, _notes.ToList());
                }
            }
            catch (LockdownException e)
            {
                _notes.Add("AFC rifiutato: " + e.Error);
                return new OpenResult(info, null, OpenState.LockedOrNotTrusted, _notes.ToList());
            }

            _afc = ConnectAfc(dev.DeviceId, service, pair);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or FormatException or System.Xml.XmlException)
        {
            throw new DeviceException("Dialogo con l'iPhone interrotto: " + e.Message, DeviceErrors.GenFailure, e);
        }
        catch (UsbmuxException e)
        {
            throw new DeviceException("iPhone non raggiungibile: " + e.Message, DeviceErrors.DeviceNotConnected, e);
        }
        finally
        {
            lockdown.Dispose();
        }

        StorageInfo? storage = null;
        try
        {
            var di = _afc.GetDeviceInfo();
            long total = long.TryParse(di.GetValueOrDefault("FSTotalBytes"), out var t) ? t : 0;
            long free = long.TryParse(di.GetValueOrDefault("FSFreeBytes"), out var f) ? f : 0;
            storage = new StorageInfo("Memoria", total, free);
        }
        catch (Exception e) when (e is AfcException or IOException)
        {
            _notes.Add("Memoria non leggibile: " + e.Message);
        }

        OpenState state;
        try
        {
            var folders = _afc.ReadDirectory(Dcim);
            _notes.Add("Cartelle in DCIM: " + (folders.Count == 0 ? "nessuna" : string.Join(", ", folders.Take(30))));
            state = folders.Count == 0 ? OpenState.NoCameraRoll : OpenState.Ready;
        }
        catch (AfcException e) when (e.Code == AfcException.ObjectNotFound)
        {
            state = OpenState.NoCameraRoll;
        }
        catch (AfcException e)
        {
            _notes.Add("DCIM non leggibile: " + e.Message);
            state = OpenState.LockedOrNotTrusted;
        }
        catch (IOException e)
        {
            throw new DeviceException("Dialogo con l'iPhone interrotto: " + e.Message, DeviceErrors.GenFailure, e);
        }
        _notes.Add($"Canale Apple: {info.Model} iOS {info.Firmware}, " + (_afcEncrypted ? "AFC cifrato" : "AFC"));
        return new OpenResult(info, storage, state, _notes.ToList());
    }

    private bool _afcEncrypted;

    private static Dictionary<string, object> SafeValues(LockdownClient lockdown)
    {
        try { return lockdown.GetValues(); }
        catch (LockdownException) { return new Dictionary<string, object>(); }
    }

    private PairRecord? ReadPair(string udid)
    {
        try
        {
            if (PairRecord.From(_mux.ReadPairRecord(udid)) is { } p)
            {
                _notes.Add("Abbinamento dal servizio Apple");
                return p;
            }
        }
        catch (Exception e) when (e is UsbmuxException or FormatException or System.Xml.XmlException)
        {
            _notes.Add("Abbinamento dal servizio Apple non disponibile: " + e.Message);
        }
        try
        {
            if (PairRecord.From(UsbmuxClient.ReadPairRecordFile(udid)) is { } p)
            {
                _notes.Add("Abbinamento dal file di Windows");
                return p;
            }
        }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException)
        {
            _notes.Add("File di abbinamento non leggibile: " + e.Message);
        }
        return null;
    }

    private AfcClient ConnectAfc(int deviceId, (int Port, bool Ssl) service, PairRecord pair)
    {
        _afcEncrypted = service.Ssl;
        var raw = _mux.Connect(deviceId, service.Port);
        if (!service.Ssl) return new AfcClient(raw);
        try
        {
            var cert = pair.ClientCertificate();
            SslStream ssl = LockdownClient.StartTls(raw, cert);
            return new AfcClient(ssl, new Disposables(raw, cert));
        }
        catch
        {
            raw.Dispose();
            throw;
        }
    }

    private sealed class Disposables(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var i in items) { try { i.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { } }
        }
    }

    private AfcClient Afc => _afc ?? throw new DeviceException("iPhone non aperto", DeviceErrors.WpdDeviceNotOpen);

    // ------------------------------------------------------------------ elenco

    public void Enumerate(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct)
    {
        var buffer = new List<RemoteFile>(256);
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guard(() =>
        {
            Walk(Dcim, "", 0);
            AddEdited();
            return 0;
        });
        if (buffer.Count > 0) batch(buffer.ToList());
        return;

        void Flush()
        {
            if (buffer.Count < 200) return;
            batch(buffer.ToList());
            buffer.Clear();
        }

        void Walk(string path, string folder, int depth)
        {
            foreach (var name in Afc.ReadDirectory(path))
            {
                ct.ThrowIfCancellationRequested();
                var full = path + "/" + name;
                Dictionary<string, string> st;
                try { st = Afc.GetFileInfo(full); }
                catch (AfcException e) when (e.Code == AfcException.ObjectNotFound) { continue; }
                if (st.GetValueOrDefault("st_ifmt") == "S_IFDIR")
                {
                    if (depth < 3) Walk(full, folder.Length == 0 ? name : folder + "/" + name, depth + 1);
                    continue;
                }
                // File vuoti (segnaposto di Foto di iCloud, scatti interrotti): niente da copiare,
                // e una copia di 0 byte passerebbe tutti i controlli.
                if (!long.TryParse(st.GetValueOrDefault("st_size"), out var size) || size <= 0) continue;
                buffer.Add(ToRemote(full, name, folder, st, canDelete: true));
                bases.Add(folder + "/" + Path.GetFileNameWithoutExtension(name));
                Flush();
            }
        }

        // Versioni modificate: iOS le tiene in PhotoData/Mutations/DCIM/<cartella>/<IMG_1234>/Adjustments/FullSizeRender.*.
        // Si presentano come IMG_E1234.* (come fa Windows), così la libreria le abbina all'originale.
        void AddEdited()
        {
            const string root = "/PhotoData/Mutations/DCIM";
            List<string> folders;
            try { folders = Afc.ReadDirectory(root); }
            catch (AfcException) { return; }
            foreach (var folder in folders)
            {
                List<string> items;
                try { items = Afc.ReadDirectory(root + "/" + folder); }
                catch (AfcException) { continue; }
                foreach (var baseName in items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!bases.Contains(folder + "/" + baseName)) continue;
                    var m = BaseName().Match(baseName);
                    if (!m.Success) continue;
                    var adj = $"{root}/{folder}/{baseName}/Adjustments";
                    List<string> files;
                    try { files = Afc.ReadDirectory(adj); }
                    catch (AfcException) { continue; }
                    foreach (var f in files.Where(f => f.StartsWith("FullSizeRender.", StringComparison.OrdinalIgnoreCase)))
                    {
                        var full = adj + "/" + f;
                        Dictionary<string, string> st;
                        try { st = Afc.GetFileInfo(full); }
                        catch (AfcException) { continue; }
                        var name = m.Groups["pre"].Value + "E" + m.Groups["num"].Value + Path.GetExtension(f).ToUpperInvariant();
                        buffer.Add(ToRemote(full, name, folder, st, canDelete: false));
                        Flush();
                    }
                }
            }
        }
    }

    [GeneratedRegex(@"^(?<pre>[A-Za-z]+_)(?<num>\d{3,})$")]
    private static partial Regex BaseName();

    private static RemoteFile ToRemote(string path, string name, string folder, Dictionary<string, string> st, bool canDelete)
    {
        long size = long.TryParse(st.GetValueOrDefault("st_size"), out var s) ? s : 0;
        return new RemoteFile(path, name, folder, size, Time(st.GetValueOrDefault("st_birthtime")) ?? Time(st.GetValueOrDefault("st_mtime")),
            Time(st.GetValueOrDefault("st_mtime")), canDelete, path);
    }

    /// <summary>Nanosecondi dal 1970 → ora locale "di orologio", come le date di WPD.</summary>
    private static DateTime? Time(string? ns)
    {
        if (!long.TryParse(ns, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        var local = DateTimeOffset.FromUnixTimeMilliseconds(n / 1_000_000).LocalDateTime;
        return local.Year >= 1990 ? DateTime.SpecifyKind(local, DateTimeKind.Unspecified) : null;
    }

    // ------------------------------------------------------------------ miniature e file

    public byte[]? GetThumbnail(string objectId) => Guard(() =>
    {
        // iOS tiene le sue miniature in PhotoData/Thumbnails/V2/DCIM/<cartella>/<file>/xxxx.JPG.
        try
        {
            var folder = "/PhotoData/Thumbnails/V2" + objectId;
            var jpg = Afc.ReadDirectory(folder).Where(n => n.EndsWith(".JPG", StringComparison.OrdinalIgnoreCase)).Order().FirstOrDefault();
            if (jpg is not null && Afc.ReadAll(folder + "/" + jpg, 8 << 20) is { Length: > 0 } thumb) return thumb;
        }
        catch (AfcException) { }

        // Altrimenti, per le foto non troppo grandi, la si ricava dal file.
        var ext = Path.GetExtension(objectId).TrimStart('.');
        if (!MediaGrouper.IsImage(ext)) return null;
        try
        {
            var st = Afc.GetFileInfo(objectId);
            if (!long.TryParse(st.GetValueOrDefault("st_size"), out var size) || size > 40L << 20) return null;
            var bytes = Afc.ReadAll(objectId, 41 << 20);
            if (bytes.Length == 0) return null;
            using var img = new MagickImage(bytes);
            img.AutoOrient();
            img.Thumbnail(320, 320);
            img.Quality = 80;
            return img.ToByteArray(MagickFormat.Jpeg);
        }
        catch (AfcException) { return null; }
        catch (MagickException) { return null; }
    });

    public DownloadResult Download(string objectId, string destination, Action<long> progress, CancellationToken ct) => Guard(() =>
    {
        var handle = Afc.Open(objectId);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            using (var output = Local(() => new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)))
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = Afc.Read(handle);
                    if (chunk.Length == 0) break;
                    Local(() => { output.Write(chunk); return 0; });
                    sha.AppendData(chunk);
                    total += chunk.Length;
                    progress(total);
                }
                Local(() => { output.Flush(flushToDisk: true); return 0; });
            }
            return new DownloadResult(total, Convert.ToHexString(sha.GetHashAndReset()));
        }
        finally
        {
            try { Afc.Close(handle); } catch (Exception e) when (e is AfcException or IOException) { }
        }
    });

    public IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds) =>
        objectIds.Select(id => new DeleteResult(id, false, DeviceErrors.NotSupported)).ToList();

    /// <summary>Errore del disco del PC: deve arrivare al motore così com'è (disco pieno…), non come "telefono scollegato".</summary>
    private sealed class LocalFileException(IOException inner) : Exception(inner.Message, inner)
    {
        public IOException Io { get; } = inner;
    }

    private static T Local<T>(Func<T> call)
    {
        try { return call(); }
        catch (IOException e) { throw new LocalFileException(e); }
    }

    /// <summary>Errori AFC e del collegamento → <see cref="DeviceException"/> con i codici che il resto dell'app conosce.</summary>
    private T Guard<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (LocalFileException e)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.Io);
            throw;
        }
        catch (AfcException e)
        {
            int code = e.Code switch
            {
                AfcException.ObjectNotFound => DeviceErrors.ElementNotFound,
                AfcException.PermissionDenied => DeviceErrors.AccessDenied,
                _ => DeviceErrors.GenFailure,
            };
            throw new DeviceException(e.Message, code, e);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            // Il collegamento è caduto: telefono scollegato (o servizio Apple riavviato). Si riapre.
            CloseAfc();
            throw new DeviceException("Collegamento con l'iPhone interrotto: " + e.Message, DeviceErrors.WpdDeviceNotOpen, e);
        }
    }

    private void CloseAfc()
    {
        try { _afc?.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        _afc = null;
    }

    public void Dispose() => CloseAfc();
}
