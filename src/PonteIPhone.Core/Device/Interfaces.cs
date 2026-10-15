using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Device;

/// <summary>
/// Accesso diretto al telefono (WPD vero, o il telefono finto dei test e della dimostrazione).
/// Chiamate sincrone, sempre dallo stesso thread: chi lo usa le mette in coda una alla volta.
/// </summary>
public interface IDeviceBackend : IDisposable
{
    IReadOnlyList<DeviceInfo> ListDevices();

    /// <summary>Apre il telefono e cerca il Rullino (DCIM). Chiude l'eventuale telefono aperto prima.</summary>
    OpenResult Open(string deviceId);

    /// <summary>Tutti i file sotto DCIM, a blocchi, man mano che si leggono.</summary>
    void Enumerate(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct);

    /// <summary>
    /// File vuoti visti dall'ultimo <see cref="Enumerate"/>: con "Ottimizza spazio iPhone" sono le foto rimaste
    /// solo su iCloud, che il telefono via cavo non consegna. Non si elencano, ma si contano per dirlo.
    /// </summary>
    int CloudOnlyFiles => 0;

    /// <summary>Com'era fatto l'ultimo elenco, per il rapporto (cartelle vuote, file vuoti…), o null.</summary>
    string? LastEnumerationSummary => null;

    /// <summary>Miniatura JPEG fornita dal telefono, o null se non ne ha.</summary>
    byte[]? GetThumbnail(string objectId);

    /// <summary>
    /// Scarica il file in <paramref name="destination"/> (sovrascrive), calcolando lo SHA-256 mentre arriva
    /// e forzando la scrittura su disco alla fine. <paramref name="progress"/> riceve i byte arrivati finora.
    /// </summary>
    DownloadResult Download(string objectId, string destination, Action<long> progress, CancellationToken ct);

    /// <summary>Cancella dal telefono, un file alla volta: l'esito di ognuno è nel risultato, non in un'eccezione.</summary>
    IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds);
}

/// <summary>Il telefono visto dall'app: tutto asincrono, mai bloccante per l'interfaccia.</summary>
public interface IDeviceConnection : IAsyncDisposable
{
    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct);
    Task<OpenResult> OpenAsync(string deviceId, CancellationToken ct);
    Task<EnumerateSummary> EnumerateAsync(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct);
    Task<byte[]?> GetThumbnailAsync(string objectId, CancellationToken ct);
    Task<DownloadResult> DownloadAsync(string objectId, string destination, Action<long>? progress, CancellationToken ct);
    Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<string> objectIds, CancellationToken ct);
}

/// <summary>Com'è andato un elenco: file consegnati e foto rimaste solo su iCloud (vedi <see cref="IDeviceBackend.CloudOnlyFiles"/>).</summary>
public sealed record EnumerateSummary(long Files, int CloudOnly, string? Detail = null);
