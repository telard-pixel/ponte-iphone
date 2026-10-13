using System.Runtime.InteropServices;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

// Definizioni COM di Windows Portable Devices (PortableDeviceApi.h / PortableDevice.h dell'SDK di Windows).
// L'ordine dei metodi è quello della vtable: non va cambiato, anche per i metodi non usati.
// Riferimento incrociato: MediaDevices (Bassman2, licenza MIT).

namespace PonteIPhone.Wpd.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public uint pid;

    public PropertyKey(Guid f, uint p)
    {
        fmtid = f;
        pid = p;
    }
}

/// <summary>PROPVARIANT: 24 byte su x64 (tipo + 6 byte riservati + unione di 16 byte).</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;
    [FieldOffset(8)] public double doubleValue;
    [FieldOffset(8)] public long longValue;
    [FieldOffset(8)] public int intValue;
    [FieldOffset(8)] public short boolValue;

    public const ushort VT_EMPTY = 0;
    public const ushort VT_I4 = 3;
    public const ushort VT_DATE = 7;
    public const ushort VT_ERROR = 10;
    public const ushort VT_BOOL = 11;
    public const ushort VT_UI4 = 19;
    public const ushort VT_I8 = 20;
    public const ushort VT_UI8 = 21;
    public const ushort VT_LPWSTR = 31;
    public const ushort VT_FILETIME = 64;
    public const ushort VT_CLSID = 72;

    public static PropVariant FromString(string s) => new() { vt = VT_LPWSTR, pointerValue = Marshal.StringToCoTaskMemUni(s) };

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pvar);
}

[ComImport, Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceManager
{
    void GetDevices([In, Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[]? pPnPDeviceIDs,
        ref uint pcPnPDeviceIDs);
    void RefreshDeviceList();
    void GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IntPtr pDeviceFriendlyName, ref uint pcchDeviceFriendlyName);
    void GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IntPtr pDeviceDescription, ref uint pcchDeviceDescription);
    void GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IntPtr pDeviceManufacturer, ref uint pcchDeviceManufacturer);
    void GetDeviceProperty([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, [MarshalAs(UnmanagedType.LPWStr)] string pszDevicePropertyName,
        IntPtr pData, ref uint pcbData, ref uint pdwType);
    void GetPrivateDevices(IntPtr pPnPDeviceIDs, ref uint pcPnPDeviceIDs);
}

[ComImport, Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevice
{
    [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string pszPnPDeviceID, IPortableDeviceValues pClientInfo);
    void SendCommand(uint dwFlags, IPortableDeviceValues pParameters, out IPortableDeviceValues ppResults);
    void Content(out IPortableDeviceContent ppContent);
    void Capabilities([MarshalAs(UnmanagedType.IUnknown)] out object ppCapabilities);
    [PreserveSig] int Cancel();
    [PreserveSig] int Close();
    void Advise(uint dwFlags, IntPtr pCallback, IntPtr pParameters, IntPtr ppszCookie);
    void Unadvise([MarshalAs(UnmanagedType.LPWStr)] string pszCookie);
    void GetPnPDeviceID([MarshalAs(UnmanagedType.LPWStr)] out string ppszPnPDeviceID);
}

[ComImport, Guid("6A96ED84-7C73-4480-9938-BF5AF477D426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceContent
{
    [PreserveSig]
    int EnumObjects(uint dwFlags, [MarshalAs(UnmanagedType.LPWStr)] string pszParentObjectID, IPortableDeviceValues? pFilter,
        out IEnumPortableDeviceObjectIDs ppEnum);
    void Properties(out IPortableDeviceProperties ppProperties);
    void Transfer(out IPortableDeviceResources ppResources);
    void CreateObjectWithPropertiesOnly(IntPtr pValues, IntPtr ppszObjectID);
    void CreateObjectWithPropertiesAndData(IntPtr pValues, IntPtr ppData, IntPtr pdwOptimalWriteBufferSize, IntPtr ppszCookie);
    [PreserveSig] int Delete(uint dwOptions, IPortableDevicePropVariantCollection pObjectIDs, IntPtr ppResults);
    void GetObjectIDsFromPersistentUniqueIDs(IntPtr pPersistentUniqueIDs, IntPtr ppObjectIDs);
    [PreserveSig] int Cancel();
    void Move(IntPtr pObjectIDs, IntPtr pszDestinationFolderObjectID, IntPtr ppResults);
    void Copy(IntPtr pObjectIDs, IntPtr pszDestinationFolderObjectID, IntPtr ppResults);
}

[ComImport, Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumPortableDeviceObjectIDs
{
    [PreserveSig]
    int Next(uint cObjects, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 0)] string[] pObjIDs,
        ref uint pcFetched);
    [PreserveSig] int Skip(uint cObjects);
    [PreserveSig] int Reset();
    void Clone(out IEnumPortableDeviceObjectIDs ppEnum);
    [PreserveSig] int Cancel();
}

[ComImport, Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceProperties
{
    void GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, out IPortableDeviceKeyCollection ppKeys);
    void GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, ref PropertyKey key, out IPortableDeviceValues ppAttributes);
    [PreserveSig]
    int GetValues([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, IPortableDeviceKeyCollection? pKeys, out IPortableDeviceValues ppValues);
    void SetValues(IntPtr pszObjectID, IntPtr pValues, IntPtr ppResults);
    void Delete(IntPtr pszObjectID, IntPtr pKeys);
    [PreserveSig] int Cancel();
}

[ComImport, Guid("FD8878AC-D841-4D17-891C-E6829CDB6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceResources
{
    void GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, out IPortableDeviceKeyCollection ppKeys);
    void GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, ref PropertyKey key, out IPortableDeviceValues ppResourceAttributes);
    [PreserveSig]
    int GetStream([MarshalAs(UnmanagedType.LPWStr)] string pszObjectID, ref PropertyKey key, uint dwMode, ref uint pdwOptimalBufferSize,
        out IStream ppStream);
    void Delete(IntPtr pszObjectID, IntPtr pKeys);
    [PreserveSig] int Cancel();
    void CreateResource(IntPtr pResourceAttributes, IntPtr ppData, IntPtr pdwOptimalWriteBufferSize, IntPtr ppszCookie);
}

[ComImport, Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceKeyCollection
{
    void GetCount(ref uint pcElems);
    void GetAt(uint dwIndex, ref PropertyKey pKey);
    void Add(ref PropertyKey key);
    void Clear();
    void RemoveAt(uint dwIndex);
}

[ComImport, Guid("89B2E422-4F1B-4316-BCEF-A44AFEA83EB3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDevicePropVariantCollection
{
    void GetCount(ref uint pcElems);
    void GetAt(uint dwIndex, ref PropVariant pValue);
    void Add(ref PropVariant pValue);
    void GetType(out ushort pvt);
    void ChangeType(ushort vt);
    void Clear();
    void RemoveAt(uint dwIndex);
}

[ComImport, Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPortableDeviceValues
{
    [PreserveSig] int GetCount(ref uint pcelt);
    [PreserveSig] int GetAt(uint index, ref PropertyKey pKey, ref PropVariant pValue);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant pValue);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pValue);
    [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string pValue);
    [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);
    [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint pValue);
    [PreserveSig] int SetSignedIntegerValue(ref PropertyKey key, int value);
    [PreserveSig] int GetSignedIntegerValue(ref PropertyKey key, out int pValue);
    [PreserveSig] int SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);
    [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong pValue);
    [PreserveSig] int SetSignedLargeIntegerValue(ref PropertyKey key, long value);
    [PreserveSig] int GetSignedLargeIntegerValue(ref PropertyKey key, out long pValue);
    [PreserveSig] int SetFloatValue(ref PropertyKey key, float value);
    [PreserveSig] int GetFloatValue(ref PropertyKey key, out float pValue);
    [PreserveSig] int SetErrorValue(ref PropertyKey key, int value);
    [PreserveSig] int GetErrorValue(ref PropertyKey key, out int pValue);
    [PreserveSig] int SetKeyValue(ref PropertyKey key, ref PropertyKey value);
    [PreserveSig] int GetKeyValue(ref PropertyKey key, out PropertyKey pValue);
    [PreserveSig] int SetBoolValue(ref PropertyKey key, int value);
    [PreserveSig] int GetBoolValue(ref PropertyKey key, out int pValue);
    [PreserveSig] int SetIUnknownValue(ref PropertyKey key, IntPtr pValue);
    [PreserveSig] int GetIUnknownValue(ref PropertyKey key, out IntPtr ppValue);
    [PreserveSig] int SetGuidValue(ref PropertyKey key, ref Guid value);
    [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid pValue);
    [PreserveSig] int SetBufferValue(ref PropertyKey key, IntPtr pValue, uint cbValue);
    [PreserveSig] int GetBufferValue(ref PropertyKey key, out IntPtr ppValue, out uint pcbValue);
    [PreserveSig] int SetIPortableDeviceValuesValue(ref PropertyKey key, IntPtr pValue);
    [PreserveSig] int GetIPortableDeviceValuesValue(ref PropertyKey key, out IntPtr ppValue);
    [PreserveSig] int SetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, IntPtr pValue);
    [PreserveSig] int GetIPortableDevicePropVariantCollectionValue(ref PropertyKey key, out IntPtr ppValue);
    [PreserveSig] int SetIPortableDeviceKeyCollectionValue(ref PropertyKey key, IntPtr pValue);
    [PreserveSig] int GetIPortableDeviceKeyCollectionValue(ref PropertyKey key, out IntPtr ppValue);
    [PreserveSig] int SetIPortableDeviceValuesCollectionValue(ref PropertyKey key, IntPtr pValue);
    [PreserveSig] int GetIPortableDeviceValuesCollectionValue(ref PropertyKey key, out IntPtr ppValue);
    [PreserveSig] int RemoveValue(ref PropertyKey key);
    [PreserveSig] int CopyValuesFromPropertyStore(IntPtr pStore);
    [PreserveSig] int CopyValuesToPropertyStore(IntPtr pStore);
    [PreserveSig] int Clear();
}

/// <summary>Classi COM di WPD e chiavi delle proprietà usate.</summary>
internal static class Wpd
{
    public static readonly Guid CLSID_PortableDeviceManager = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");
    public static readonly Guid CLSID_PortableDeviceFTM = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");
    public static readonly Guid CLSID_PortableDevice = new("728A21C5-3D9E-48D7-9810-864848F0F404");
    public static readonly Guid CLSID_PortableDeviceValues = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    public static readonly Guid CLSID_PortableDeviceKeyCollection = new("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");
    public static readonly Guid CLSID_PortableDevicePropVariantCollection = new("08A99E2F-6D6D-4B80-AF5A-BAF2BCBE4CB9");

    private static readonly Guid ObjectV1 = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
    private static readonly Guid DeviceV1 = new("26D4979A-E643-4626-9E2B-736DC0C92FDC");
    private static readonly Guid StorageV1 = new("01A3057A-74D6-4E80-BEA7-DC4C212CE50A");
    private static readonly Guid ClientV1 = new("204D9F0C-2292-4080-9F42-40664E70F859");
    private static readonly Guid FunctionalV1 = new("8F052D93-ABCA-4FC5-A5AC-B01DF4DBE598");

    public static readonly PropertyKey ObjectParentId = new(ObjectV1, 3);
    public static readonly PropertyKey ObjectName = new(ObjectV1, 4);
    public static readonly PropertyKey ObjectPersistentUniqueId = new(ObjectV1, 5);
    public static readonly PropertyKey ObjectContentType = new(ObjectV1, 7);
    public static readonly PropertyKey ObjectSize = new(ObjectV1, 11);
    public static readonly PropertyKey ObjectOriginalFileName = new(ObjectV1, 12);
    public static readonly PropertyKey ObjectDateCreated = new(ObjectV1, 18);
    public static readonly PropertyKey ObjectDateModified = new(ObjectV1, 19);
    public static readonly PropertyKey ObjectDateAuthored = new(ObjectV1, 20);
    public static readonly PropertyKey ObjectCanDelete = new(ObjectV1, 26);

    public static readonly PropertyKey DeviceFirmware = new(DeviceV1, 3);
    public static readonly PropertyKey DeviceManufacturer = new(DeviceV1, 7);
    public static readonly PropertyKey DeviceModel = new(DeviceV1, 8);
    public static readonly PropertyKey DeviceSerial = new(DeviceV1, 9);
    public static readonly PropertyKey DeviceFriendlyName = new(DeviceV1, 12);

    public static readonly PropertyKey StorageCapacity = new(StorageV1, 4);
    public static readonly PropertyKey StorageFree = new(StorageV1, 5);

    public static readonly PropertyKey FunctionalCategory = new(FunctionalV1, 2);
    public static readonly Guid CategoryStorage = new("23F05BBC-15DE-4C2A-A55B-A9AF5CE412EF");

    public static readonly PropertyKey ClientName = new(ClientV1, 2);
    public static readonly PropertyKey ClientMajor = new(ClientV1, 3);
    public static readonly PropertyKey ClientMinor = new(ClientV1, 4);
    public static readonly PropertyKey ClientRevision = new(ClientV1, 5);
    public static readonly PropertyKey ClientSecurityQos = new(ClientV1, 8);
    public static readonly PropertyKey ClientDesiredAccess = new(ClientV1, 9);
    public static readonly PropertyKey ClientShareMode = new(ClientV1, 10);

    public static readonly PropertyKey ResourceDefault = new(new Guid("E81E79BE-34F0-41BF-B53F-F1A06AE87842"), 0);
    public static readonly PropertyKey ResourceThumbnail = new(new Guid("C7C407BA-98FA-46B5-9960-23FEC124CFDE"), 0);

    public static readonly Guid ContentTypeFolder = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    public static readonly Guid ContentTypeFunctionalObject = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");

    public const string DeviceObjectId = "DEVICE";
    public const uint STGM_READ = 0;
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint SECURITY_IMPERSONATION = 0x00020000; // SECURITY_SQOS_PRESENT | SecurityImpersonation
    public const uint PORTABLE_DEVICE_DELETE_NO_RECURSION = 0;

    public static T Create<T>(Guid clsid) where T : class =>
        (T)(Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, throwOnError: true)!)
            ?? throw new COMException("Classe COM non creata: " + clsid));
}
