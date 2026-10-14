using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PonteIPhone.Services;

/// <summary>
/// Avvisa quando un telefono si collega o si scollega (notifiche dispositivo di Windows per WPD).
/// Le notifiche arrivano a raffica: si aspetta un secondo di calma prima di avvisare.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    private static readonly Guid WpdInterface = new("6AC27878-A6FA-4155-BA85-F98F491D4F33");
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    private readonly HwndSource _source;
    private readonly IntPtr _handle;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;

    public event Action? Changed;

    public DeviceMonitor(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.EnsureHandle());
        _source.AddHook(Hook);
        _debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };

        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = 5, // DBT_DEVTYP_DEVICEINTERFACE
            dbcc_classguid = WpdInterface,
        };
        var buffer = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, buffer, false);
            _handle = RegisterDeviceNotification(_source.Handle, buffer, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && (int)wParam is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _debounce.Stop();
        _source.RemoveHook(Hook);
        if (_handle != IntPtr.Zero) UnregisterDeviceNotification(_handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 255)] public string dbcc_name;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);
}

/// <summary>Il driver USB di Apple (da Dispositivi Apple o iTunes) è installato?</summary>
public static class AppleDriver
{
    public const string StoreUrl = "ms-windows-store://pdp/?productid=9NP83LWLPZ9K";
    public const string WebUrl = "https://apps.microsoft.com/detail/9NP83LWLPZ9K";

    public static bool IsInstalled()
    {
        try
        {
            using var lm = Registry.LocalMachine;
            foreach (var service in new[] { "usbaapl64", "usbaapl", "Apple Mobile Device Service", "AppleMobileDeviceService" })
            {
                using var key = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service);
                if (key is not null) return true;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return false;
    }

    public static void OpenStore()
    {
        if (!Shell.Open(StoreUrl)) Shell.Open(WebUrl);
    }
}

/// <summary>Il PC non va in sospensione durante un trasferimento.</summary>
public static class KeepAwake
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    public static void Set(bool on) => SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
}

/// <summary>Richiama l'attenzione: icona lampeggiante sulla barra delle applicazioni e un suono.</summary>
public static class Attention
{
    private const uint FLASHW_ALL = 3;
    private const uint FLASHW_TIMERNOFG = 12; // finché la finestra non torna in primo piano

    public static void Request(Window? window)
    {
        System.Media.SystemSounds.Exclamation.Play();
        if (window is null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || window.IsActive) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);
}

public static class Shell
{
    public static bool Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Apre Esplora file con il file selezionato.</summary>
    public static void ShowInFolder(string path)
    {
        if (File.Exists(path)) Open("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Open(path);
        else if (Path.GetDirectoryName(path) is { } dir && Directory.Exists(dir)) Open(dir);
    }

    private static void Open(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }
}

/// <summary>Seconda copia dell'app: porta in primo piano la finestra già aperta.</summary>
public static class SingleInstance
{
    public static void ActivateExisting()
    {
        var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
        {
            using (p)
            {
                if (p.Id == me.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                return;
            }
        }
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
}
