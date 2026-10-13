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
    private readonly List<string> _log = [];

    /// <summary>Annotazioni per la diagnostica (cosa ha trovato aprendo il telefono).</summary>
    public IReadOnlyList<string> Notes => _log;

    // ------------------------------------------------------------------ elenco dei telefoni

    public IReadOnlyList<DeviceInfo> ListDevices()
    {
        var manager = Wpd.Create<IPortableDeviceManager>(Wpd.CLSID_PortableDeviceManager);
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
            device = Wpd.Create<IPortableDevice>(Wpd.CLSID_PortableDeviceFTM);
        }
        catch (COMException)
        {
            device = Wpd.Create<IPortableDevice>(Wpd.CLSID_PortableDevice);
        }

        var client = Wpd.Create<IPortableDeviceValues>(Wpd.CLSID_PortableDeviceValues);
        try
        {
            var k = Wpd.ClientName; client.SetStringValue(ref k, "Ponte iPhone");
            k = Wpd.ClientMajor; client.SetUnsignedIntegerValue(ref k, 1);
            k = Wpd.ClientMinor; client.SetUnsignedIntegerValue(ref k, 0);
            k = Wpd.ClientRevision; client.SetUnsignedIntegerValue(ref k, 0);
            k = Wpd.ClientSecurityQos; client.SetUnsignedIntegerValue(ref k, Wpd.SECURITY_IMPERSONATION);
            // Lettura e scrittura: la scrittura serve solo per cancellare con "Sposta".
            k = Wpd.ClientDesiredAccess; client.SetUnsignedIntegerValue(ref k, Wpd.GENERIC_READ | Wpd.GENERIC_WRITE);
            k = Wpd.ClientShareMode; client.SetUnsignedIntegerValue(ref k, Wpd.FILE_SHARE_READ | Wpd.FILE_SHARE_WRITE);

            int hr = device.Open(deviceId, client);
            if (hr == DeviceErrors.AccessDenied)
            {
                // Alcuni driver concedono solo la lettura: si apre così, e "Sposta" lo scoprirà.
                _log.Add("Apertura in scrittura rifiutata: apro in sola lettura");
                k = Wpd.ClientDesiredAccess; client.SetUnsignedIntegerValue(ref k, Wpd.GENERIC_READ);
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
            var dev = ReadValues(Wpd.DeviceObjectId, Wpd.DeviceFriendlyName, Wpd.DeviceManufacturer, Wpd.DeviceModel, Wpd.DeviceSerial, Wpd.DeviceFirmware);
            info = new DeviceInfo(deviceId,
                GetString(dev, Wpd.DeviceFriendlyName), GetString(dev, Wpd.DeviceManufacturer), GetString(dev, Wpd.DeviceModel),
                GetString(dev, Wpd.DeviceSerial), GetString(dev, Wpd.DeviceFirmware));
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
        _dcimId = null;
        foreach (var child in Children(Wpd.DeviceObjectId))
        {
            IPortableDeviceValues v;
            try { v = ReadValues(child, Wpd.ObjectName, Wpd.ObjectContentType, Wpd.FunctionalCategory, Wpd.StorageCapacity, Wpd.StorageFree); }
            catch (DeviceException) { continue; }
            try
            {
                bool isStorage = GetGuid(v, Wpd.FunctionalCategory) == Wpd.CategoryStorage;
                bool isFunctional = isStorage || GetGuid(v, Wpd.ObjectContentType) == Wpd.ContentTypeFunctionalObject;
                if (!isFunctional) continue;
                if (isStorage)
                    storage ??= new StorageInfo(GetString(v, Wpd.ObjectName), (long)GetULong(v, Wpd.StorageCapacity), (long)GetULong(v, Wpd.StorageFree));
                var subs = Children(child);
                if (isStorage) storageChildren += subs.Count;
                foreach (var sub in subs)
                {
                    var sv = ReadValues(sub, Wpd.ObjectName, Wpd.ObjectOriginalFileName);
                    var n = GetString(sv, Wpd.ObjectOriginalFileName) is { Length: > 0 } o ? o : GetString(sv, Wpd.ObjectName);
                    Release(sv);
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

        _log.Add($"Aperto {info.Model} iOS {info.Firmware}: memoria {(storage is null ? "non visibile" : storage.Name)}, DCIM {(_dcimId is null ? "assente" : "trovata")}");
        var state = _dcimId is not null ? OpenState.Ready
            : storage is null || storageChildren == 0 ? OpenState.LockedOrNotTrusted
            : OpenState.NoCameraRoll;
        return new OpenResult(info, storage, state);
    }

    private IPortableDeviceKeyCollection BuildFileKeys()
    {
        var keys = Wpd.Create<IPortableDeviceKeyCollection>(Wpd.CLSID_PortableDeviceKeyCollection);
        foreach (var k in new[]
                 {
                     Wpd.ObjectName, Wpd.ObjectOriginalFileName, Wpd.ObjectContentType, Wpd.ObjectSize,
                     Wpd.ObjectDateCreated, Wpd.ObjectDateModified, Wpd.ObjectDateAuthored, Wpd.ObjectCanDelete,
                     Wpd.ObjectPersistentUniqueId,
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
                    var name = GetString(v, Wpd.ObjectOriginalFileName) is { Length: > 0 } o ? o : GetString(v, Wpd.ObjectName);
                    var isFolder = GetGuid(v, Wpd.ObjectContentType) == Wpd.ContentTypeFolder;
                    if (isFolder)
                    {
                        // Sugli iPhone le foto stanno un livello sotto DCIM (100APPLE, 202601_a…).
                        if (depth < 3) Walk(id, folderPath.Length == 0 ? name : folderPath + "/" + name, depth + 1);
                        continue;
                    }
                    var created = GetDate(v, Wpd.ObjectDateAuthored) ?? GetDate(v, Wpd.ObjectDateCreated);
                    var modified = GetDate(v, Wpd.ObjectDateModified);
                    bool canDelete = !HasValue(v, Wpd.ObjectCanDelete) || GetBool(v, Wpd.ObjectCanDelete);
                    buffer.Add(new RemoteFile(id, name, folderPath, (long)GetULong(v, Wpd.ObjectSize), created, modified, canDelete,
                        GetString(v, Wpd.ObjectPersistentUniqueId)));
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
        var key = Wpd.ResourceThumbnail;
        uint optimal = 0;
        int hr = Resources.GetStream(objectId, ref key, Wpd.STGM_READ, ref optimal, out var stream);
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
        var key = Wpd.ResourceDefault;
        uint optimal = 0;
        int hr = Resources.GetStream(objectId, ref key, Wpd.STGM_READ, ref optimal, out var stream);
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
            var ids = Wpd.Create<IPortableDevicePropVariantCollection>(Wpd.CLSID_PortableDevicePropVariantCollection);
            var pv = PropVariant.FromString(id);
            try
            {
                ids.Add(ref pv);
                int hr = Content.Delete(Wpd.PORTABLE_DEVICE_DELETE_NO_RECURSION, ids, IntPtr.Zero);
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
        var coll = Wpd.Create<IPortableDeviceKeyCollection>(Wpd.CLSID_PortableDeviceKeyCollection);
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
