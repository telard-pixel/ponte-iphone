namespace PonteIPhone.Core.Model;

/// <summary>Un dispositivo Apple sul cavo come lo vede Windows (anche quando il servizio Apple non lo passa all'app).</summary>
/// <param name="InstanceId">Es. "USB\VID_05AC&amp;PID_12A8\00008101…".</param>
/// <param name="Problem">Codice problema di Gestione dispositivi (0 = funziona, 28 = driver mancante, 10 = non si avvia…).</param>
public sealed record AppleUsbDevice(string InstanceId, string Name, int Problem);

public enum NoDeviceVerdict
{
    /// <summary>Nessun iPhone sul cavo, né per Windows né per il servizio Apple.</summary>
    NothingOnCable,
    /// <summary>Il servizio Apple (Apple Mobile Device Service) non risponde.</summary>
    ServiceDown,
    /// <summary>Windows vede l'iPhone sul cavo ma il suo driver ha un problema.</summary>
    DriverProblem,
    /// <summary>Windows vede l'iPhone sul cavo, il servizio Apple no: l'iPhone non passa i dati (bloccato, "Consenti" non toccato).</summary>
    DataBlocked,
    /// <summary>Il servizio Apple vede l'iPhone solo in Wi-Fi.</summary>
    OnlyWifi,
}

/// <summary>
/// Perché l'app non vede l'iPhone. Il 16/10 l'iPhone con iOS 27 "non veniva rilevato" e non c'era nessuna traccia:
/// senza telefono l'app non scriveva niente e non mandava rapporti. Ora si guarda a tre livelli (cavo per Windows,
/// servizio Apple, Wi-Fi) e la schermata dice cosa fare in quel caso preciso.
/// </summary>
public sealed record NoDeviceDiagnosis(NoDeviceVerdict Verdict, bool ServiceOk, string? ServiceError, int MuxUsb, int MuxNetwork,
    IReadOnlyList<AppleUsbDevice> AppleUsb)
{
    /// <summary>Solo i dispositivi interi (non le loro interfacce "&amp;MI_xx").</summary>
    public static IEnumerable<AppleUsbDevice> Roots(IEnumerable<AppleUsbDevice> all) =>
        all.Where(d => !d.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase));

    public static NoDeviceDiagnosis Decide(bool serviceOk, string? serviceError, int muxUsb, int muxNetwork, IReadOnlyList<AppleUsbDevice> appleUsb)
    {
        var roots = Roots(appleUsb).ToList();
        var verdict =
            roots.Any(d => d.Problem != 0) ? NoDeviceVerdict.DriverProblem
            : !serviceOk ? NoDeviceVerdict.ServiceDown
            : roots.Count > 0 && muxUsb == 0 ? NoDeviceVerdict.DataBlocked
            : muxNetwork > 0 ? NoDeviceVerdict.OnlyWifi
            : NoDeviceVerdict.NothingOnCable;
        return new NoDeviceDiagnosis(verdict, serviceOk, serviceError, muxUsb, muxNetwork, appleUsb);
    }

    public string Title => Verdict switch
    {
        NoDeviceVerdict.ServiceDown => "Il servizio Apple di Windows non risponde",
        NoDeviceVerdict.DriverProblem => "Windows vede l'iPhone, ma il driver Apple non funziona",
        NoDeviceVerdict.DataBlocked => "L'iPhone è sul cavo ma non passa i dati",
        NoDeviceVerdict.OnlyWifi => "L'iPhone si vede solo in Wi-Fi",
        _ => "Collega l'iPhone con il cavo USB",
    };

    public string Text => Verdict switch
    {
        NoDeviceVerdict.ServiceDown =>
            "Senza questo servizio (si installa con Dispositivi Apple) nessuna app vede l'iPhone. Apri una volta l'app Dispositivi Apple: di solito il servizio riparte. Se non basta, riavvia il PC.",
        NoDeviceVerdict.DriverProblem =>
            "Apri l'app Dispositivi Apple e aggiornala dal Microsoft Store (le versioni nuove di iOS a volte richiedono il driver aggiornato), poi stacca e riattacca il cavo. Se non basta, riavvia il PC.",
        NoDeviceVerdict.DataBlocked =>
            "Sblocca l'iPhone e guarda lo schermo: se chiede di consentire all'accessorio di connettersi tocca Consenti; se chiede di autorizzare il computer tocca Autorizza e inserisci il codice. Se non chiede niente, stacca e riattacca il cavo con il telefono sbloccato.",
        NoDeviceVerdict.OnlyWifi =>
            "Per copiare serve il cavo: collega l'iPhone al PC con un cavo USB e sbloccalo.",
        _ => "Windows non vede nessun iPhone sul cavo. Sblocca il telefono; se non compare, prova un altro cavo (alcuni servono solo a caricare) o un'altra porta USB, meglio direttamente sul PC e non su un hub o una tastiera.",
    };

    /// <summary>Una riga per il registro e il rapporto.</summary>
    public string Summary
    {
        get
        {
            var usb = Roots(AppleUsb).ToList();
            var cable = usb.Count == 0 ? "nessun dispositivo Apple sul cavo"
                : string.Join(", ", usb.Select(d => $"{d.Name} [{d.InstanceId}]" + (d.Problem != 0 ? $" problema {d.Problem}" : "")))
                  + $" ({AppleUsb.Count - usb.Count} interfacce)";
            var service = ServiceOk ? $"servizio Apple ok, {MuxUsb} via cavo e {MuxNetwork} in Wi-Fi" : "servizio Apple non risponde: " + ServiceError;
            return $"{Verdict}: Windows: {cable}; {service}";
        }
    }
}
