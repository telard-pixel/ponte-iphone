using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Wpd.Interop;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace PonteIPhone.Wpd;

/// <summary>
/// iPhone via Windows Portable Devices. Gira nel processo separato, su un solo thread MTA.
/// Gli ID oggetto di WPD si usano direttamente (nessuna ricerca per nome, che con migliaia di foto
/// vorrebbe dire rileggere la cartella a ogni file).
/// </summary>
public sealed class WpdBackend : IDeviceBackend
{
    private const int ChunkSize = 256 * 1024;

    private IPortableDevice? _device;
    private IPortableDeviceContent? _content;
    private IPortableDeviceProperties? _properties;
    private IPortableDeviceResources? _resources;
    private IPortableDeviceKeyCollection? _fileKeys;
    private string? _dcimId;
    /// <summary>Nessuna cartella DCIM: si legge tutta la memoria, tenendo solo foto e video.</summary>
    private bool _wholeStorage;
    private readonly List<string> _log = [];

    /// <summary>Annotazioni per la diagnostica (cosa ha trovato aprendo il telefono).</summary>
    public IReadOnlyList<string> Notes => _log;

    // ------------------------------------------------------------------ elenco dei telefoni

    public IReadOnlyList<DeviceInfo> ListDevices()
    {
        var manager = WpdApi.Create<IPortableDeviceManager>(WpdApi.CLSID_PortableDeviceManager);
        try
        {
            manager.RefreshDeviceList();
            uint count = 0;
            manager.GetDevices(null, ref count);
            if (count == 0) return [];
            var ids = new string[count];
            manager.GetDevices(ids, ref count);

            var result = new List<DeviceInfo>();
            foreach (var id in ids.Take((int)count))
            {
                if (string.IsNullOrEmpty(id)) continue;
                var friendly = ReadManagerString(manager, id, 0);
                var manufacturer = ReadManagerString(manager, id, 2);
                var description = ReadManagerString(manager, id, 1);
                var info = new DeviceInfo(id, friendly, manufacturer, description, "", "");
                if (!info.IsApple) continue; // solo iPhone e iPad: chiavette, fotocamere e Android restano fuori
                result.Add(info);
            }
            return result;
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static string ReadManagerString(IPortableDeviceManager m, string id, int which)
    {
        uint len = 0;
        try
        {
            switch (which)
            {
                case 0: m.GetDeviceFriendlyName(id, IntPtr.Zero, ref len); break;
                case 1: m.GetDeviceDescription(id, IntPtr.Zero, ref len); break;
                default: m.GetDeviceManufacturer(id, IntPtr.Zero, ref len); break;
            }
            if (len == 0) return "";
            var buffer = Marshal.AllocHGlobal((int)len * 2 + 2);
            try
            {
                switch (which)
                {
                    case 0: m.GetDeviceFriendlyName(id, buffer, ref len); break;
                    case 1: m.GetDeviceDescription(id, buffer, ref len); break;
                    default: m.GetDeviceManufacturer(id, buffer, ref len); break;
                }
                return Marshal.PtrToStringUni(buffer)?.Trim() ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (COMException)
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ apertura

    public OpenResult Open(string deviceId)
    {
        CloseDevice();
        IPortableDevice device;
        try
        {
            device = WpdApi.Create<IPortableDevice>(WpdApi.CLSID_PortableDeviceFTM);
        }
        catch (COMException)
        {
            device = WpdApi.Create<IPortableDevice>(WpdApi.CLSID_PortableDevice);
        }

        var client = WpdApi.Create<IPortableDeviceValues>(WpdApi.CLSID_PortableDeviceValues);
        try
        {
            var k = WpdApi.ClientName; client.SetStringValue(ref k, "Ponte iPhone");
            k = WpdApi.ClientMajor; client.SetUnsignedIntegerValue(ref k, 1);
            k = WpdApi.ClientMinor; client.SetUnsignedIntegerValue(ref k, 0);
            k = WpdApi.ClientRevision; client.SetUnsignedIntegerValue(ref k, 0);
            k = WpdApi.ClientSecurityQos; client.SetUnsignedIntegerValue(ref k, WpdApi.SECURITY_IMPERSONATION);
            // Lettura e scrittura: la scrittura serve solo per cancellare con "Sposta".
            k = WpdApi.ClientDesiredAccess; client.SetUnsignedIntegerValue(ref k, WpdApi.GENERIC_READ | WpdApi.GENERIC_WRITE);
            k = WpdApi.ClientShareMode; client.SetUnsignedIntegerValue(ref k, WpdApi.FILE_SHARE_READ | WpdApi.FILE_SHARE_WRITE);

            int hr = device.Open(deviceId, client);
            if (hr == DeviceErrors.AccessDenied)
            {
                // Alcuni driver concedono solo la lettura: si apre così, e "Sposta" lo scoprirà.
                _log.Add("Apertura in scrittura rifiutata: apro in sola lettura");
                k = WpdApi.ClientDesiredAccess; client.SetUnsignedIntegerValue(ref k, WpdApi.GENERIC_READ);
                hr = device.Open(deviceId, client);
            }
            if (hr < 0) throw new DeviceException("Impossibile aprire l'iPhone: " + DeviceErrors.Describe(hr), hr);
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }

        _device = device;
        device.Content(out var content);
        _content = content;
        content.Properties(out var props);
        _properties = props;
        content.Transfer(out var resources);
        _resources = resources;
        _fileKeys = BuildFileKeys();

        // Dati del telefono (se il driver non li dà, si va avanti con quelli dell'elenco).
        var info = new DeviceInfo(deviceId, "", "Apple Inc.", "", "", "");
        try
        {
            var dev = ReadValues(WpdApi.DeviceObjectId, WpdApi.DeviceFriendlyName, WpdApi.DeviceManufacturer, WpdApi.DeviceModel, WpdApi.DeviceSerial, WpdApi.DeviceFirmware);
            info = new DeviceInfo(deviceId,
                GetString(dev, WpdApi.DeviceFriendlyName), GetString(dev, WpdApi.DeviceManufacturer), GetString(dev, WpdApi.DeviceModel),
                GetString(dev, WpdApi.DeviceSerial), GetString(dev, WpdApi.DeviceFirmware));
            Release(dev);
        }
        catch (DeviceException e)
        {
            _log.Add("Dati del telefono non leggibili: " + e.Message);
        }

        // Memoria e cartella DCIM: DEVICE → "Internal Storage" → DCIM.
        // Con il telefono bloccato o non autorizzato la memoria c'è ma è vuota.
        StorageInfo? storage = null;
        int storageChildren = 0;
        string? storageId = null;
        var seen = new List<string>();
        _dcimId = null;
        _wholeStorage = false;
        _log.Clear();
        foreach (var child in Children(WpdApi.DeviceObjectId))
        {
            IPortableDeviceValues v;
            try { v = ReadValues(child, WpdApi.ObjectName, WpdApi.ObjectContentType, WpdApi.FunctionalCategory, WpdApi.StorageCapacity, WpdApi.StorageFree); }
            catch (DeviceException) { continue; }
            try
            {
                bool isStorage = GetGuid(v, WpdApi.FunctionalCategory) == WpdApi.CategoryStorage;
                bool isFunctional = isStorage || GetGuid(v, WpdApi.ObjectContentType) == WpdApi.ContentTypeFunctionalObject;
                if (!isFunctional) continue;
                if (isStorage)
                {
                    storage ??= new StorageInfo(GetString(v, WpdApi.ObjectName), (long)GetULong(v, WpdApi.StorageCapacity), (long)GetULong(v, WpdApi.StorageFree));
                    storageId ??= child;
                }
                var subs = Children(child);
                if (isStorage) storageChildren += subs.Count;
                _log.Add($"Oggetto {GetString(v, WpdApi.ObjectName)} ({(isStorage ? "memoria" : "funzione")}): {subs.Count} elementi");
                foreach (var sub in subs)
                {
                    string n;
                    try
                    {
                        var sv = ReadValues(sub, WpdApi.ObjectName, WpdApi.ObjectOriginalFileName);
                        n = (GetString(sv, WpdApi.ObjectOriginalFileName) is { Length: > 0 } o ? o : GetString(sv, WpdApi.ObjectName)).Trim();
                        Release(sv);
                    }
                    catch (DeviceException e)
                    {
                        seen.Add($"? ({e.Message})");
                        continue;
                    }
                    if (seen.Count < 30) seen.Add(n);
                    if (n.Equals("DCIM", StringComparison.OrdinalIgnoreCase))
                    {
                        _dcimId = sub;
                        break;
                    }
                }
                if (_dcimId is not null) break;
            }
            finally
            {
                Release(v);
            }
        }

        _log.Add("Cartelle viste: " + (seen.Count == 0 ? "nessuna" : string.Join(", ", seen)));
        if (_dcimId is null && storageId is not null && storageChildren > 0)
        {
            // La memoria c'è ma la cartella non si chiama DCIM (iOS diversi, driver diversi):
            // si legge tutta, tenendo solo foto e video. Se non c'è niente, l'app lo dirà.
            _dcimId = storageId;
            _wholeStorage = true;
        }
        _log.Add($"Aperto {info.Model} iOS {info.Firmware}: memoria {(storage is null ? "non visibile" : storage.Name)}, " +
                 (_wholeStorage ? "DCIM non trovata, leggo tutta la memoria" : _dcimId is null ? "DCIM assente" : "DCIM trovata"));
        var state = _dcimId is not null ? OpenState.Ready
            : storage is null || storageChildren == 0 ? OpenState.LockedOrNotTrusted
            : OpenState.NoCameraRoll;
        return new OpenResult(info, storage, state, _log.ToList());
    }

    private IPortableDeviceKeyCollection BuildFileKeys()
    {
        var keys = WpdApi.Create<IPortableDeviceKeyCollection>(WpdApi.CLSID_PortableDeviceKeyCollection);
        foreach (var k in new[]
                 {
                     WpdApi.ObjectName, WpdApi.ObjectOriginalFileName, WpdApi.ObjectContentType, WpdApi.ObjectSize,
                     WpdApi.ObjectDateCreated, WpdApi.ObjectDateModified, WpdApi.ObjectDateAuthored, WpdApi.ObjectCanDelete,
                     WpdApi.ObjectPersistentUniqueId,
                 })
        {
            var key = k;
            keys.Add(ref key);
        }
        return keys;
    }

    // ------------------------------------------------------------------ elenco dei file

    public void Enumerate(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct)
    {
        var dcim = _dcimId ?? throw new DeviceException("iPhone non aperto o senza Rullino", DeviceErrors.WpdDeviceNotOpen);
        var buffer = new List<RemoteFile>(256);
        Walk(dcim, "", depth: 0);
        if (buffer.Count > 0) batch(buffer.ToList());
        return;

        void Walk(string folderId, string folderPath, int depth)
        {
            foreach (var id in Children(folderId))
            {
                ct.ThrowIfCancellationRequested();
                var v = ReadFileValues(id);
                if (v is null) continue;
                try
                {
                    var name = GetString(v, WpdApi.ObjectOriginalFileName) is { Length: > 0 } o ? o : GetString(v, WpdApi.ObjectName);
                    var isFolder = GetGuid(v, WpdApi.ObjectContentType) == WpdApi.ContentTypeFolder;
                    if (isFolder)
                    {
                        // Sugli iPhone le foto stanno un livello sotto DCIM (100APPLE, 202601_a…).
                        if (depth < (_wholeStorage ? 5 : 3)) Walk(id, folderPath.Length == 0 ? name : folderPath + "/" + name, depth + 1);
                        continue;
                    }
                    var ext = Path.GetExtension(name).TrimStart('.');
                    if (_wholeStorage && !(MediaGrouper.IsImage(ext) || MediaGrouper.IsVideo(ext) || MediaGrouper.IsSidecar(ext))) continue;
                    var created = GetDate(v, WpdApi.ObjectDateAuthored) ?? GetDate(v, WpdApi.ObjectDateCreated);
                    var modified = GetDate(v, WpdApi.ObjectDateModified);
                    bool canDelete = !HasValue(v, WpdApi.ObjectCanDelete) || GetBool(v, WpdApi.ObjectCanDelete);
                    buffer.Add(new RemoteFile(id, name, folderPath, (long)GetULong(v, WpdApi.ObjectSize), created, modified, canDelete,
                        GetString(v, WpdApi.ObjectPersistentUniqueId)));
                }
                finally
                {
                    Release(v);
                }
                if (buffer.Count >= 200)
                {
                    batch(buffer.ToList());
                    buffer.Clear();
                }
            }
        }
    }

    private IPortableDeviceValues? ReadFileValues(string id)
    {
        int hr = Props.GetValues(id, _fileKeys, out var values);
        // Alcune proprietà possono mancare (hr positivo, S_FALSE): i valori presenti ci sono comunque.
        return hr < 0 ? null : values;
    }

    // ------------------------------------------------------------------ miniature e file

    public byte[]? GetThumbnail(string objectId)
    {
        var key = WpdApi.ResourceThumbnail;
        uint optimal = 0;
        int hr = Resources.GetStream(objectId, ref key, WpdApi.STGM_READ, ref optimal, out var stream);
        if (hr < 0 || stream is null) return null; // il telefono non ha la miniatura di questo file
        try
        {
            using var ms = new MemoryStream();
            var buf = new byte[Math.Max((int)Math.Min(optimal, 1u << 20), 64 * 1024)];
            ReadAll(stream, buf, (b, n) => ms.Write(b, 0, n), CancellationToken.None, _ => { });
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    public DownloadResult Download(string objectId, string destination, Action<long> progress, CancellationToken ct)
    {
        var key = WpdApi.ResourceDefault;
        uint optimal = 0;
        int hr = Resources.GetStream(objectId, ref key, WpdApi.STGM_READ, ref optimal, out var stream);
        if (hr < 0 || stream is null) throw new DeviceException("Il telefono non fornisce il file: " + DeviceErrors.Describe(hr), hr);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total;
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                // Blocchi tra 256 KB e 4 MB: abbastanza grandi per la velocità, abbastanza piccoli per l'avanzamento.
                var buf = new byte[Math.Clamp((int)Math.Min(optimal, 4u << 20), ChunkSize, 4 << 20)];
                total = ReadAll(stream, buf, (b, n) =>
                {
                    output.Write(b, 0, n);
                    sha.AppendData(b, 0, n);
                }, ct, progress);
                output.Flush(flushToDisk: true);
            }
            return new DownloadResult(total, Convert.ToHexString(sha.GetHashAndReset()));
        }
        finally
        {
            Marshal.ReleaseComObject(stream);
        }
    }

    private static long ReadAll(IStream stream, byte[] buf, Action<byte[], int> sink, CancellationToken ct, Action<long> progress)
    {
        long total = 0;
        var readPtr = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Marshal.WriteInt32(readPtr, 0);
                try
                {
                    stream.Read(buf, buf.Length, readPtr);
                }
                catch (COMException e)
                {
                    throw new DeviceException("Lettura interrotta: " + DeviceErrors.Describe(e.HResult), e.HResult, e);
                }
                int n = Marshal.ReadInt32(readPtr);
                if (n <= 0) break;
                sink(buf, n);
                total += n;
                progress(total);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(readPtr);
        }
        return total;
    }

    // ------------------------------------------------------------------ cancellazione

    public IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds)
    {
        var results = new List<DeleteResult>(objectIds.Count);
        foreach (var id in objectIds)
        {
            // Uno alla volta: l'esito di ognuno è chiaro anche se il driver non compila i risultati multipli.
            var ids = WpdApi.Create<IPortableDevicePropVariantCollection>(WpdApi.CLSID_PortableDevicePropVariantCollection);
            var pv = PropVariant.FromString(id);
            try
            {
                ids.Add(ref pv);
                int hr = Content.Delete(WpdApi.PORTABLE_DEVICE_DELETE_NO_RECURSION, ids, IntPtr.Zero);
                results.Add(new DeleteResult(id, hr >= 0, hr));
            }
            catch (COMException e)
            {
                results.Add(new DeleteResult(id, false, e.HResult));
            }
            finally
            {
                PropVariant.PropVariantClear(ref pv);
                Marshal.ReleaseComObject(ids);
            }
        }
        return results;
    }

    // ------------------------------------------------------------------ utilità

    private IPortableDeviceContent Content => _content ?? throw new DeviceException("iPhone non aperto", DeviceErrors.WpdDeviceNotOpen);
    private IPortableDeviceProperties Props => _properties ?? throw new DeviceException("iPhone non aperto", DeviceErrors.WpdDeviceNotOpen);
    private IPortableDeviceResources Resources => _resources ?? throw new DeviceException("iPhone non aperto", DeviceErrors.WpdDeviceNotOpen);

    private List<string> Children(string parentId)
    {
        var result = new List<string>();
        int hr = Content.EnumObjects(0, parentId, null, out var en);
        if (hr < 0 || en is null)
        {
            if (DeviceErrors.Classify(hr) == DeviceErrorKind.Gone) throw new DeviceException("iPhone scollegato", hr);
            return result;
        }
        try
        {
            var ids = new string[100];
            while (true)
            {
                uint fetched = 0;
                hr = en.Next((uint)ids.Length, ids, ref fetched);
                if (hr < 0) throw new DeviceException("Lettura dell'elenco interrotta: " + DeviceErrors.Describe(hr), hr);
                for (int i = 0; i < fetched; i++) if (!string.IsNullOrEmpty(ids[i])) result.Add(ids[i]);
                if (fetched == 0 || hr == 1) break; // S_FALSE: finiti
                Array.Clear(ids);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(en);
        }
        return result;
    }

    private IPortableDeviceValues ReadValues(string objectId, params PropertyKey[] keys)
    {
        var coll = WpdApi.Create<IPortableDeviceKeyCollection>(WpdApi.CLSID_PortableDeviceKeyCollection);
        try
        {
            foreach (var k in keys)
            {
                var key = k;
                coll.Add(ref key);
            }
            int hr = Props.GetValues(objectId, coll, out var values);
            if (hr < 0 || values is null) throw new DeviceException("Proprietà non leggibili: " + DeviceErrors.Describe(hr), hr);
            return values;
        }
        finally
        {
            Marshal.ReleaseComObject(coll);
        }
    }

    private static string GetString(IPortableDeviceValues v, PropertyKey key) =>
        v.GetStringValue(ref key, out var s) >= 0 ? s ?? "" : "";

    private static ulong GetULong(IPortableDeviceValues v, PropertyKey key) =>
        v.GetUnsignedLargeIntegerValue(ref key, out var n) >= 0 ? n
        : v.GetUnsignedIntegerValue(ref key, out var m) >= 0 ? m : 0;

    private static bool GetBool(IPortableDeviceValues v, PropertyKey key) =>
        v.GetBoolValue(ref key, out var b) >= 0 && b != 0;

    private static Guid GetGuid(IPortableDeviceValues v, PropertyKey key) =>
        v.GetGuidValue(ref key, out var g) >= 0 ? g : Guid.Empty;

    private static bool HasValue(IPortableDeviceValues v, PropertyKey key)
    {
        if (v.GetValue(ref key, out var pv) < 0) return false;
        try { return pv.vt is not (PropVariant.VT_EMPTY or PropVariant.VT_ERROR); }
        finally { PropVariant.PropVariantClear(ref pv); }
    }

    /// <summary>Date WPD: di solito VT_DATE (ora locale), a volte FILETIME o testo.</summary>
    private static DateTime? GetDate(IPortableDeviceValues v, PropertyKey key)
    {
        if (v.GetValue(ref key, out var pv) < 0) return null;
        try
        {
            DateTime? d = pv.vt switch
            {
                PropVariant.VT_DATE when pv.doubleValue > 0 => DateTime.FromOADate(pv.doubleValue),
                PropVariant.VT_FILETIME when pv.longValue > 0 => DateTime.FromFileTime(pv.longValue),
                PropVariant.VT_LPWSTR when pv.pointerValue != IntPtr.Zero && DateTime.TryParse(Marshal.PtrToStringUni(pv.pointerValue),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) => parsed,
                _ => null,
            };
            return d is { Year: >= 1990 } ok ? DateTime.SpecifyKind(ok, DateTimeKind.Unspecified) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        finally
        {
            PropVariant.PropVariantClear(ref pv);
        }
    }

    private static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }

    private void CloseDevice()
    {
        Release(_fileKeys);
        Release(_resources);
        Release(_properties);
        Release(_content);
        if (_device is not null)
        {
            try { _device.Close(); } catch (COMException) { }
            Release(_device);
        }
        _fileKeys = null;
        _resources = null;
        _properties = null;
        _content = null;
        _device = null;
        _dcimId = null;
    }

    public void Dispose() => CloseDevice();
}
