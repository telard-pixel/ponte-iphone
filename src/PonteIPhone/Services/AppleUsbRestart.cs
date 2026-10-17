using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace PonteIPhone.Services;

/// <summary>
/// "Scollega e ricollega il cavo", fatto da Windows: riavvia il dispositivo USB dell'iPhone (pnputil /restart-device).
/// Il 17/10 Windows mostrava la memoria dell'iPhone vuota finché il cavo non veniva ricollegato, e per eliminare serve
/// che Windows veda le foto. Riavviare un dispositivo richiede i permessi da amministratore: Windows chiede conferma.
/// </summary>
public static class AppleUsbRestart
{
    public enum Outcome { Restarted, Declined, NotFound, Failed }

    /// <summary>
    /// Il dispositivo intero dell'iPhone (non le sue interfacce &amp;MI_xx): quello con il numero di serie dell'iPhone aperto,
    /// o l'unico presente.
    /// </summary>
    public static string? FindInstance(string? serial)
    {
        var all = NoDeviceCheck.AppleUsbDevices()
            .Where(d => !d.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (!string.IsNullOrWhiteSpace(serial))
        {
            var key = Norm(serial);
            var mine = all.Where(d => Norm(d.InstanceId).EndsWith(key, StringComparison.Ordinal)).ToList();
            if (mine.Count == 1) return mine[0].InstanceId;
        }
        return all.Count == 1 ? all[0].InstanceId : null;
    }

    /// <summary>Chiede a Windows di riavviare il dispositivo e aspetta che finisca (al massimo 30 s).</summary>
    public static async Task<(Outcome Result, string Detail)> RestartAsync(string? serial)
    {
        var id = FindInstance(serial);
        if (id is null) return (Outcome.NotFound, "dispositivo USB dell'iPhone non trovato");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), $"/restart-device \"{id}\"")
        {
            UseShellExecute = true, // serve per "runas"
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return (Outcome.Failed, "pnputil non partito");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0 ? (Outcome.Restarted, id) : (Outcome.Failed, $"pnputil ha risposto {p.ExitCode} per {id}");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            return (Outcome.Declined, "permesso non dato"); // "No" nella richiesta di Windows
        }
        catch (Exception e) when (e is Win32Exception or OperationCanceledException or InvalidOperationException)
        {
            return (Outcome.Failed, e.Message);
        }
    }
}
