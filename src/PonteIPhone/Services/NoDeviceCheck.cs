using System.Runtime.InteropServices;
using System.Text;
using PonteIPhone.Core.Apple;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Services;

/// <summary>
/// Quando l'app non vede nessun iPhone: guarda cosa c'è sul cavo per Windows (Gestione dispositivi, VID Apple 05AC)
/// e cosa vede il servizio Apple (anche in Wi-Fi), per dire all'utente il perché invece di "Collega l'iPhone".
/// Tutto in sola lettura e veloce: si può chiamare ogni pochi secondi.
/// </summary>
public static class NoDeviceCheck
{
    /// <param name="flaps">Sparizioni dal cavo viste dall'app negli ultimi minuti.</param>
    public static NoDeviceDiagnosis Run(int flaps)
    {
        bool serviceOk;
        string? serviceError = null;
        int usb = 0, network = 0;
        try
        {
            var list = new UsbmuxClient().ListDevices();
            network = list.Count(d => d.ConnectionType.Equals("Network", StringComparison.OrdinalIgnoreCase));
            usb = list.Count - network;
            serviceOk = true;
        }
        catch (UsbmuxException e)
        {
            serviceOk = false;
            serviceError = e.Message;
        }
        return NoDeviceDiagnosis.Decide(serviceOk, serviceError, usb, network, AppleUsbDevices(), flaps);
    }

    /// <summary>Dispositivi USB presenti con il produttore Apple, con nome e codice problema di Gestione dispositivi.</summary>
    public static List<AppleUsbDevice> AppleUsbDevices()
    {
        var result = new List<AppleUsbDevice>();
        var set = SetupDiGetClassDevsW(IntPtr.Zero, "USB", IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var id = new StringBuilder(512);
            for (uint i = 0; i < 1000 && SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                id.Clear();
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, id, id.Capacity, out _)) continue;
                var instance = id.ToString();
                if (!instance.Contains("VID_05AC", StringComparison.OrdinalIgnoreCase)) continue;
                int problem = CM_Get_DevNode_Status(out var status, out var code, data.DevInst, 0) == 0
                    && (status & DN_HAS_PROBLEM) != 0 ? (int)code : 0;
                var name = Property(set, ref data, SPDRP_FRIENDLYNAME) ?? Property(set, ref data, SPDRP_DEVICEDESC) ?? "";
                result.Add(new AppleUsbDevice(instance, name, problem));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    private static string? Property(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        var buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, (uint)buffer.Length, out var size)) return null;
        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0');
        return text.Length > 0 ? text : null;
    }

    private const uint DIGCF_PRESENT = 0x2;
    private const uint DIGCF_ALLCLASSES = 0x4;
    private const uint SPDRP_DEVICEDESC = 0x0;
    private const uint SPDRP_FRIENDLYNAME = 0xC;
    private const uint DN_HAS_PROBLEM = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, uint property, out uint type,
        byte[] buffer, uint size, out uint required);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
}
