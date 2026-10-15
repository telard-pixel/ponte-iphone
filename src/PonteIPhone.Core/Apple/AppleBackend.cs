using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ImageMagick;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;

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
    /// <summary>Telefono aperto per ultimo: dopo un intoppo si riapre da solo alla chiamata successiva.</summary>
    private string? _openedId;
    private readonly List<string> _notes = [];

    private readonly string _pairFolder;

    /// <param name="pairFolder">Dove tenere gli abbinamenti creati dall'app (nei test una cartella temporanea).</param>
    public AppleBackend(UsbmuxClient? mux = null, string? pairFolder = null)
    {
        _mux = mux ?? new UsbmuxClient();
        _pairFolder = pairFolder ?? Path.Combine(AppPaths.Root, "abbinamenti");
    }

    /// <summary>
    /// iPhone collegati col cavo. Quelli in Wi-Fi si ignorano: con Dispositivi Apple del Microsoft Store
    /// non compaiono mai, e quando compaiono non si possono cancellare foto (scelta del 14/10: solo cavo).
    /// </summary>
    public IReadOnlyList<DeviceInfo> ListDevices() =>
        UsbDevices()
            .Select(d => new DeviceInfo(IdPrefix + d.Udid, "", "Apple Inc.", "iPhone", d.Udid, "", d.ConnectionType))
            .ToList();

    private static bool IsNetwork(UsbmuxDevice d) => d.ConnectionType.Equals("Network", StringComparison.OrdinalIgnoreCase);

    private List<UsbmuxDevice> UsbDevices() =>
        _mux.ListDevices().Where(d => !IsNetwork(d)).GroupBy(d => d.Udid).Select(g => g.First()).ToList();

    /// <summary>UDID del telefono aperto (per scegliere lo stesso telefono anche in WPD).</summary>
    public string? OpenedUdid => _openedId is null ? null : Udid(_openedId);

    /// <summary>Quanti iPhone sono collegati col cavo (con più telefoni la cancellazione deve essere sicura del telefono giusto).</summary>
    public int UsbDeviceCount() => UsbDevices().Count;

    private static string Udid(string deviceId) => deviceId.StartsWith(IdPrefix, StringComparison.Ordinal) ? deviceId[IdPrefix.Length..] : deviceId;

    public OpenResult Open(string deviceId)
    {
        CloseAfc();
        _notes.Clear();
        _openedId = deviceId;
        var udid = Udid(deviceId);
        UsbmuxDevice dev;
        try
        {
            dev = UsbDevices().FirstOrDefault(d => d.Udid == udid)
                  ?? throw new DeviceException("iPhone non collegato", DeviceErrors.DeviceNotConnected);
        }
        catch (UsbmuxException e)
        {
            throw new AppleUnavailableException(e.Message, e);
        }

        var candidates = ReadPairs(udid);
        var info = new DeviceInfo(deviceId, "", "Apple Inc.", "iPhone", udid, "", dev.ConnectionType);

        string step = "collegamento al telefono";
        LockdownClient lockdown;
        try
        {
            lockdown = new LockdownClient(_mux.Connect(dev.DeviceId, LockdownClient.Port));
        }
        catch (UsbmuxException e) when (e.Code == 3)
        {
            // Appena inserito il cavo il telefono è già nell'elenco ma non accetta ancora collegamenti.
            _notes.Add("Telefono non ancora pronto: " + e.Message);
            return new OpenResult(info, null, OpenState.NotReady, _notes.ToList());
        }
        catch (UsbmuxException e)
        {
            throw new AppleUnavailableException("il servizio Apple non raggiunge il telefono: " + e.Message, e);
        }

        try
        {
            step = "sessione cifrata";
            PairRecord? pair = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    lockdown.StartSession(candidate);
                    pair = candidate;
                    break;
                }
                catch (LockdownException e) when (e.Error is "InvalidHostID" or "InvalidPairRecord")
                {
                    // Il telefono ha dimenticato questo abbinamento (ripristino, "Ripristina posizione e privacy"…).
                    _notes.Add("Abbinamento non più valido per il telefono: " + e.Error);
                }
            }
            if (pair is null)
            {
                step = "abbinamento";
                var (outcome, fresh) = Pair(lockdown, dev, udid);
                if (outcome != PairOutcome.Paired || fresh is null)
                {
                    var waiting = outcome switch
                    {
                        PairOutcome.Denied => OpenState.TrustDenied,
                        PairOutcome.Locked => OpenState.LockedOrNotTrusted,
                        _ => OpenState.NotTrusted,
                    };
                    return new OpenResult(info, null, waiting, _notes.ToList());
                }
                pair = fresh;
                step = "sessione cifrata";
                lockdown.StartSession(pair);
            }

            step = "dati del telefono";
            var values = SafeValues(lockdown);
            info = new DeviceInfo(deviceId,
                values.GetString("DeviceName") ?? "",
                "Apple Inc.",
                values.GetString("ProductType") is { Length: > 0 } pt ? pt : "iPhone",
                udid,
                values.GetString("ProductVersion") ?? "",
                dev.ConnectionType);

            step = "apertura di AFC";
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

            step = "collegamento ad AFC";
            _afc = ConnectAfc(dev.DeviceId, service, pair);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or FormatException or System.Xml.XmlException)
        {
            throw new DeviceException($"Il telefono non risponde ({step}): {e.Message}", DeviceErrors.GenFailure, e);
        }
        catch (LockdownException e)
        {
            throw new AppleUnavailableException($"{step}: {e.Message}", e);
        }
        catch (UsbmuxException e)
        {
            throw new AppleUnavailableException($"il servizio Apple non raggiunge il telefono ({step}): {e.Message}", e);
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
            _notes.Add($"Cartelle in DCIM ({folders.Count}): " + (folders.Count == 0 ? "nessuna" : string.Join(", ", folders.Take(30)) + (folders.Count > 30 ? ", …" : "")));
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
        _notes.Add($"Canale Apple: {info.Model} iOS {info.Firmware}, " + (_afcEncrypted ? "AFC cifrato" : "AFC") + (info.IsWifi ? ", via Wi-Fi" : ", via USB"));
        return new OpenResult(info, storage, state, _notes.ToList());
    }

    private bool _afcEncrypted;

    private static Dictionary<string, object> SafeValues(LockdownClient lockdown)
    {
        try { return lockdown.GetValues(); }
        catch (LockdownException) { return new Dictionary<string, object>(); }
    }

    /// <summary>Abbinamenti da provare, nell'ordine: servizio Apple, file di Windows, quello creato dall'app.</summary>
    private List<PairRecord> ReadPairs(string udid)
    {
        var result = new List<PairRecord>();
        void Add(PairRecord? p, string note)
        {
            if (p is null || result.Any(r => r.HostId == p.HostId)) return;
            result.Add(p);
            _notes.Add(note);
        }
        try { Add(PairRecord.From(_mux.ReadPairRecord(udid)), "Abbinamento dal servizio Apple"); }
        catch (Exception e) when (e is UsbmuxException or FormatException or System.Xml.XmlException)
        {
            _notes.Add("Abbinamento dal servizio Apple non disponibile: " + e.Message);
        }
        try { Add(PairRecord.From(UsbmuxClient.ReadPairRecordFile(udid)), "Abbinamento dal file di Windows"); }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException)
        {
            _notes.Add("File di abbinamento non leggibile: " + e.Message);
        }
        try { Add(PairRecord.From(ReadOwnPairFile(udid)), "Abbinamento creato dall'app"); }
        catch (Exception e) when (e is FormatException or System.Xml.XmlException)
        {
            _notes.Add("Abbinamento dell'app non leggibile: " + e.Message);
        }
        if (result.Count == 0) _notes.Add("Nessun abbinamento con questo PC: chiedo \"Autorizza\" sul telefono");
        return result;
    }

    /// <summary>Certificati della richiesta in corso per telefono: restano gli stessi finché l'utente risponde.</summary>
    private readonly Dictionary<string, PairingMaterial> _pairing = new(StringComparer.Ordinal);

    private string OwnPairFile(string udid) => Path.Combine(_pairFolder, udid + ".plist");

    private byte[]? ReadOwnPairFile(string udid)
    {
        try { return File.Exists(OwnPairFile(udid)) ? File.ReadAllBytes(OwnPairFile(udid)) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Chiede "Autorizza questo computer?" sulla stessa connessione (ancora senza sessione) e, se il telefono
    /// accetta, salva l'abbinamento nel servizio Apple (come iTunes) e in una copia dell'app.
    /// </summary>
    private (PairOutcome, PairRecord?) Pair(LockdownClient lockdown, UsbmuxDevice dev, string udid)
    {
        if (!_pairing.TryGetValue(udid, out var material))
        {
            var publicKey = lockdown.Request("GetValue", new Dictionary<string, object> { ["Key"] = "DevicePublicKey" }).GetData("Value")
                            ?? throw new LockdownException("NoPublicKey", "il telefono non ha dato la sua chiave pubblica");
            string buid;
            try { buid = _mux.ReadBuid() ?? Guid.NewGuid().ToString().ToUpperInvariant(); }
            catch (UsbmuxException) { buid = Guid.NewGuid().ToString().ToUpperInvariant(); }
            material = _pairing[udid] = PairingMaterial.Create(publicKey, buid);
        }

        var (outcome, escrow) = lockdown.Pair(material);
        _notes.Add(outcome switch
        {
            PairOutcome.Paired => "Abbinamento accettato sul telefono",
            PairOutcome.WaitingForTrust => "In attesa di \"Autorizza\" sul telefono",
            PairOutcome.Denied => "Sul telefono è stato toccato \"Non autorizzare\"",
            _ => "Telefono bloccato: \"Autorizza\" compare solo da sbloccato",
        });
        if (outcome != PairOutcome.Paired) return (outcome, null);
        _pairing.Remove(udid);

        string? mac = null;
        try { mac = lockdown.Request("GetValue", new Dictionary<string, object> { ["Key"] = "WiFiAddress" }).GetString("Value"); }
        catch (LockdownException) { }
        var xml = material.RecordXml(escrow, mac);
        try
        {
            _notes.Add(_mux.SavePairRecord(udid, dev.DeviceId, xml) ? "Abbinamento salvato nel servizio Apple" : "Il servizio Apple non ha salvato l'abbinamento");
        }
        catch (UsbmuxException e) { _notes.Add("Abbinamento non salvato nel servizio Apple: " + e.Message); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(OwnPairFile(udid))!);
            File.WriteAllBytes(OwnPairFile(udid), xml);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _notes.Add("Copia dell'abbinamento non salvata: " + e.Message);
        }
        return (PairOutcome.Paired, PairRecord.From(xml));
    }

    private AfcClient ConnectAfc(int deviceId, (int Port, bool Ssl) service, PairRecord pair)
    {
        _afcEncrypted = service.Ssl;
        var raw = _mux.Connect(deviceId, service.Port);
        UsbmuxClient.SetTimeout(raw, UsbmuxClient.DataTimeout);
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

    private AfcClient Afc => _afc ?? Reopen();

    /// <summary>Dopo un intoppo (telefono bloccato un attimo, lento, USB che singhiozza) si riapre senza disturbare nessuno.</summary>
    private AfcClient Reopen()
    {
        if (_openedId is null) throw new DeviceException("iPhone non aperto", DeviceErrors.WpdDeviceNotOpen);
        OpenResult r;
        try
        {
            r = Open(_openedId);
        }
        catch (AppleUnavailableException e)
        {
            throw new DeviceException("Il telefono non risponde: " + e.Message, DeviceErrors.GenFailure, e);
        }
        if (!r.Ready || _afc is null) throw new DeviceException("L'iPhone è bloccato: sbloccalo", DeviceErrors.NotReady);
        return _afc;
    }

    /// <summary>Nome, cartella, dimensione e data di un file del Rullino (per ritrovarlo in WPD quando si cancella).</summary>
    public RemoteFile? Describe(string objectId) => Guard(() =>
    {
        if (!objectId.StartsWith(Dcim + "/", StringComparison.Ordinal)) return null;
        Dictionary<string, string> st;
        try { st = Afc.GetFileInfo(objectId); }
        catch (AfcException e) when (e.Code == AfcException.ObjectNotFound) { return null; }
        var rel = objectId[(Dcim.Length + 1)..];
        var slash = rel.LastIndexOf('/');
        return ToRemote(objectId, rel[(slash + 1)..], slash < 0 ? "" : rel[..slash], st, canDelete: true);
    });

    // ------------------------------------------------------------------ elenco

    public int CloudOnlyFiles { get; private set; }

    /// <summary>Com'era fatto l'ultimo elenco (per il rapporto): cartelle, cartelle vuote, file vuoti, miniature senza file.</summary>
    public string? LastEnumerationSummary { get; private set; }

    public void Enumerate(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct)
    {
        var buffer = new List<RemoteFile>(256);
        var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Cartella/nome di tutto ciò che c'è in DCIM, anche i file vuoti: serve a contare le foto che mancano.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var topFolders = new List<string>();
        int emptyFiles = 0, emptyFolders = 0, missing = 0;
        Guard(() =>
        {
            buffer.Clear(); bases.Clear(); seen.Clear(); topFolders.Clear();
            emptyFiles = emptyFolders = missing = 0;
            Walk(Dcim, "", 0);
            AddEdited();
            missing = CountMissingOriginals();
            return 0;
        });
        if (buffer.Count > 0) batch(buffer.ToList());
        CloudOnlyFiles = emptyFiles + missing;
        LastEnumerationSummary = $"{topFolders.Count} cartelle ({emptyFolders} vuote), {emptyFiles} file vuoti, {missing} foto con miniatura ma senza file";
        return;

        // Con "Ottimizza spazio iPhone" l'originale di una foto su iCloud spesso non è proprio in DCIM,
        // ma la libreria ne tiene la miniatura in PhotoData/Thumbnails/V2/DCIM/<cartella>/<nome>.
        int CountMissingOriginals()
        {
            int n = 0;
            foreach (var folder in topFolders)
            {
                ct.ThrowIfCancellationRequested();
                List<string> thumbs;
                try { thumbs = Afc.ReadDirectory("/PhotoData/Thumbnails/V2/DCIM/" + folder); }
                catch (AfcException) { continue; }
                n += thumbs.Count(t => !t.StartsWith('.') && !seen.Contains(folder + "/" + t));
            }
            return n;
        }

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
                    if (depth == 0)
                    {
                        topFolders.Add(name);
                        int before = seen.Count;
                        Walk(full, name, 1);
                        if (seen.Count == before) emptyFolders++;
                        // Un segno di vita per cartella: con tante cartelle vuote l'elenco non tace per un minuto.
                        batch(buffer.ToList());
                        buffer.Clear();
                    }
                    else if (depth < 3) Walk(full, folder + "/" + name, depth + 1);
                    continue;
                }
                // File vuoti (segnaposto di Foto di iCloud, scatti interrotti): niente da copiare,
                // e una copia di 0 byte passerebbe tutti i controlli.
                if (!name.StartsWith('.')) seen.Add(folder + "/" + name);
                if (!long.TryParse(st.GetValueOrDefault("st_size"), out var size) || size <= 0)
                {
                    if (!name.StartsWith('.')) emptyFiles++;
                    continue;
                }
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
            if (!long.TryParse(st.GetValueOrDefault("st_size"), out var size) || size > 15L << 20) return null;
            var bytes = Afc.ReadAll(objectId, 16 << 20);
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
            // Collegamento caduto o risposta in ritardo. Alla prossima chiamata si riapre da solo; qui si dice
            // solo se il telefono è proprio sparito (scollegato) o se è lì ma non ha risposto (si riprova).
            CloseAfc();
            bool present;
            try { present = _openedId is not null && UsbDevices().Any(d => d.Udid == Udid(_openedId)); }
            catch (UsbmuxException) { present = false; }
            throw present
                ? new DeviceException("Il telefono non ha risposto in tempo: " + e.Message, DeviceErrors.GenFailure, e)
                : new DeviceException("iPhone scollegato", DeviceErrors.DeviceNotConnected, e);
        }
    }

    private void CloseAfc()
    {
        try { _afc?.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        _afc = null;
    }

    public void Dispose() => CloseAfc();
}
