using System.Text.Json.Serialization;

namespace PonteIPhone.Core.Model;

/// <summary>Un telefono visto da Windows (WPD).</summary>
public sealed record DeviceInfo(string Id, string Name, string Manufacturer, string Model, string Serial, string Firmware)
{
    /// <summary>
    /// Identità stabile del telefono per il registro delle copie: il numero di serie se il driver lo dà,
    /// altrimenti l'ID di Windows (che cambia se si usa un'altra porta USB, ma resta meglio di niente).
    /// </summary>
    [JsonIgnore]
    public string Key => string.IsNullOrWhiteSpace(Serial) ? Id : Serial.Trim();

    [JsonIgnore]
    public bool IsApple =>
        Manufacturer.Contains("Apple", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
        Model.Contains("iPhone", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nome da mostrare: quello scelto sul telefono, se il driver lo passa, altrimenti il modello.</summary>
    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
        : !string.IsNullOrWhiteSpace(Model) ? Model.Trim() : "iPhone";
}

/// <summary>Memoria del telefono (byte). Zero quando il driver non la dichiara.</summary>
public sealed record StorageInfo(string Name, long Capacity, long Free)
{
    [JsonIgnore]
    public long Used => Capacity > Free ? Capacity - Free : 0;
}

/// <summary>Esito dell'apertura del telefono.</summary>
public sealed record OpenResult(DeviceInfo Device, StorageInfo? Storage, OpenState State)
{
    [JsonIgnore]
    public bool Ready => State == OpenState.Ready;
}

public enum OpenState
{
    /// <summary>Cartella DCIM trovata: si possono leggere foto e video.</summary>
    Ready,
    /// <summary>Il telefono c'è ma non mostra la memoria: bloccato o "Autorizza" non ancora toccato.</summary>
    LockedOrNotTrusted,
    /// <summary>Memoria visibile ma senza DCIM: telefono senza foto o con tutto solo su iCloud.</summary>
    NoCameraRoll,
}

/// <summary>Un file del Rullino come lo dichiara il telefono.</summary>
public sealed record RemoteFile(
    string ObjectId,
    string Name,
    string Folder,
    long Size,
    DateTime? Created,
    DateTime? Modified,
    bool CanDelete,
    string? PersistentId = null)
{
    /// <summary>
    /// Chiave che sopravvive a scollegamenti e riavvii (gli ID oggetto di Windows no):
    /// cartella, nome e dimensione insieme non si ripetono sullo stesso telefono.
    /// </summary>
    [JsonIgnore]
    public string Key => $"{Folder.ToUpperInvariant()}/{Name.ToUpperInvariant()}|{Size}";

    [JsonIgnore]
    public string Extension => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
}

/// <summary>Esito di una cancellazione dal telefono, file per file.</summary>
public sealed record DeleteResult(string ObjectId, bool Ok, int HResult)
{
    [JsonIgnore]
    public bool Refused => !Ok && DeviceErrors.IsRefusal(HResult);
}

/// <summary>Byte scritti e impronta SHA-256 calcolata mentre arrivavano.</summary>
public sealed record DownloadResult(long Bytes, string Sha256);
