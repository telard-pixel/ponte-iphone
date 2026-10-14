namespace PonteIPhone.Core.Model;

/// <summary>Errore di un'operazione sul telefono, con il codice HRESULT di Windows.</summary>
public class DeviceException(string message, int hresult = 0, Exception? inner = null) : Exception(message, inner)
{
    public int Code => hresult;
    public DeviceErrorKind Kind => DeviceErrors.Classify(hresult);
}

/// <summary>Il processo che parla con il telefono non rispondeva più ed è stato riavviato.</summary>
public sealed class WorkerRestartedException(string message) : DeviceException(message, DeviceErrors.WorkerRestarted);

public enum DeviceErrorKind
{
    /// <summary>Da riprovare: blocco momentaneo, telefono bloccato, USB instabile.</summary>
    Transient,
    /// <summary>Telefono scollegato: si aspetta che torni.</summary>
    Gone,
    /// <summary>Il file non c'è più sul telefono (cancellato nel frattempo).</summary>
    NotFound,
    /// <summary>Il telefono rifiuta l'operazione: inutile riprovare.</summary>
    Refused,
    /// <summary>Problema sul PC (disco pieno, permessi): inutile riprovare.</summary>
    Local,
}

public static class DeviceErrors
{
    // Codici reali visti con gli iPhone su Windows, più quelli generici di WPD.
    public const int AccessDenied = unchecked((int)0x80070005);
    public const int NotSupported = unchecked((int)0x80070032);     // ERROR_NOT_SUPPORTED
    public const int NotImplemented = unchecked((int)0x80004001);   // E_NOTIMPL
    public const int GenFailure = unchecked((int)0x8007001F);       // "Una periferica collegata al sistema non funziona"
    public const int NotReady = unchecked((int)0x80070015);
    public const int Busy = unchecked((int)0x800700AA);
    public const int SemTimeout = unchecked((int)0x80070079);
    public const int IoDevice = unchecked((int)0x8007045D);
    public const int DeviceNotConnected = unchecked((int)0x8007048F);
    public const int NoSuchDevice = unchecked((int)0x800701B1);
    public const int InvalidHandle = unchecked((int)0x80070006);
    public const int FileNotFound = unchecked((int)0x80070002);
    public const int ElementNotFound = unchecked((int)0x80070490);  // ERROR_NOT_FOUND
    public const int WpdDeviceNotOpen = unchecked((int)0x80042002); // E_WPD_DEVICE_NOT_OPEN: va riaperto
    public const int DiskFull = unchecked((int)0x80070070);
    public const int HandleDiskFull = unchecked((int)0x80070027);
    public const int WorkerRestarted = unchecked((int)0xA0050001);  // nostro: processo riavviato dal watchdog
    public const int SizeMismatch = unchecked((int)0xA0050002);     // nostro: arrivati meno byte del dovuto
    public const int VerifyFailed = unchecked((int)0xA0050003);     // nostro: rilettura diversa dall'impronta
    public const int NoWpdMatch = unchecked((int)0xA0050004);       // nostro: file non ritrovato nella via di Windows per cancellarlo

    public static DeviceErrorKind Classify(int hr) => hr switch
    {
        DeviceNotConnected or NoSuchDevice or InvalidHandle or WpdDeviceNotOpen => DeviceErrorKind.Gone,
        FileNotFound or ElementNotFound => DeviceErrorKind.NotFound,
        AccessDenied or NotSupported or NotImplemented => DeviceErrorKind.Refused,
        DiskFull or HandleDiskFull => DeviceErrorKind.Local,
        _ => DeviceErrorKind.Transient,
    };

    public static bool IsRefusal(int hr) => Classify(hr) == DeviceErrorKind.Refused;

    public static bool IsNotFound(int hr) => Classify(hr) == DeviceErrorKind.NotFound;

    /// <summary>Spiegazione per l'utente, in italiano semplice.</summary>
    public static string Describe(int hr) => hr switch
    {
        GenFailure => "l'iPhone ha smesso di rispondere (succede con i video grandi se il telefono converte i formati)",
        NotReady or Busy => "l'iPhone è occupato o bloccato",
        DeviceNotConnected or NoSuchDevice or InvalidHandle or WpdDeviceNotOpen => "l'iPhone è stato scollegato",
        AccessDenied => "l'iPhone non dà il permesso (sbloccalo e controlla di aver toccato Autorizza)",
        NotSupported or NotImplemented => "l'iPhone non permette questa operazione da Windows",
        FileNotFound or ElementNotFound => "il file non c'è più sul telefono",
        DiskFull or HandleDiskFull => "il disco di destinazione è pieno",
        WorkerRestarted => "l'iPhone non rispondeva: collegamento riaperto",
        SizeMismatch => "il file è arrivato incompleto",
        VerifyFailed => "la copia sul disco non corrisponde a quella arrivata",
        NoWpdMatch => "Windows non mostra questo file (serve per cancellare): scollega e ricollega l'iPhone sbloccato",
        0 => "errore sconosciuto",
        _ => $"errore 0x{hr:X8}",
    };
}
