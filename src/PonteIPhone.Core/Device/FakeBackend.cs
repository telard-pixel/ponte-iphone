using System.Security.Cryptography;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Device;

/// <summary>
/// Telefono finto, per i test e per la modalità dimostrativa. Può simulare i problemi veri:
/// blocchi del driver, scollegamenti a metà file, file troncati, cancellazioni rifiutate, telefono bloccato.
/// </summary>
public sealed class FakeBackend : IDeviceBackend
{
    public sealed class FakeFile
    {
        public required RemoteFile Info { get; set; }
        public required Func<byte[]> Content { get; set; }
        public byte[]? Thumbnail { get; init; }
    }

    public DeviceInfo Device { get; init; } = new("fake-1", "iPhone di prova", "Apple Inc.", "iPhone 15 Pro", "SERIALEFINTO", "26.0");
    public StorageInfo Storage { get; init; } = new("Internal Storage", 128_000_000_000, 30_000_000_000);
    public List<FakeFile> Files { get; } = [];
    /// <summary>Foto "solo su iCloud" (file vuoti) da dichiarare dopo l'elenco.</summary>
    public int CloudOnlyFiles { get; set; }

    // --- guasti simulati (contatori che scendono a ogni uso) ---
    /// <summary>Le prossime N letture restano bloccate (finché non vengono annullate o il processo chiuso).</summary>
    public int HangDownloads;
    /// <summary>Le prossime N letture si interrompono a metà con un errore del driver.</summary>
    public int FailDownloads;
    public int FailDownloadsCode = DeviceErrors.GenFailure;
    /// <summary>Le prossime N letture terminano senza errore ma con meno byte del dovuto.</summary>
    public int TruncateDownloads;
    /// <summary>Le prossime N letture risultano "telefono scollegato" e il telefono sparisce dall'elenco.</summary>
    public int DisconnectDuringDownloads;
    /// <summary>
    /// Le prossime N letture si interrompono perché l'iPhone si blocca da solo (Blocco automatico):
    /// "accesso negato", e da lì il telefono resta bloccato finché il test non lo sblocca.
    /// </summary>
    public int LockDuringDownloads;
    /// <summary>La prossima cancellazione trova l'iPhone appena bloccato: tutto "accesso negato".</summary>
    public bool LockOnNextDelete;
    /// <summary>Il telefono rifiuta le cancellazioni (come potrebbe fare iOS).</summary>
    public bool RefuseDelete;
    /// <summary>Il telefono è bloccato: si vede ma la memoria è vuota.</summary>
    public bool Locked;
    /// <summary>Il telefono non è collegato.</summary>
    public bool Unplugged;
    /// <summary>Byte al secondo (0 = istantaneo): la dimostrazione rallenta per far vedere l'avanzamento.</summary>
    public long BytesPerSecond;

    public int DownloadCalls { get; private set; }
    public List<string> Deleted { get; } = [];
    /// <summary>Scatta quando un blocco simulato viene sbloccato (chiusura o annullamento).</summary>
    public CancellationToken HangRelease { get; set; } = CancellationToken.None;
    private bool _open;

    /// <summary>Suddivisione della memoria da restituire (null = il collegamento non la sa dire).</summary>
    public StorageBreakdown? Breakdown { get; set; }

    public StorageBreakdown? GetStorage(CancellationToken ct)
    {
        EnsureReady();
        return Breakdown;
    }

    public IReadOnlyList<DeviceInfo> ListDevices() => Unplugged ? [] : [Device];

    public OpenResult Open(string deviceId)
    {
        if (Unplugged || deviceId != Device.Id)
            throw new DeviceException("Telefono non collegato", DeviceErrors.DeviceNotConnected);
        _open = true;
        return new OpenResult(Device, Locked ? null : Storage, Locked ? OpenState.LockedOrNotTrusted : OpenState.Ready);
    }

    private void EnsureReady()
    {
        if (Unplugged || !_open) throw new DeviceException("Telefono non collegato", DeviceErrors.DeviceNotConnected);
        if (Locked) throw new DeviceException("Telefono bloccato", DeviceErrors.NotReady);
    }

    public void Enumerate(Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct)
    {
        EnsureReady();
        FullEnumerations++;
        foreach (var chunk in Files.Select(f => f.Info).Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            batch(chunk);
        }
    }

    /// <summary>Elenchi completi chiesti finora (le cancellazioni dovrebbero leggere solo le cartelle che servono).</summary>
    public int FullEnumerations { get; private set; }
    /// <summary>Cartelle lette una per una da <see cref="EnumerateFolders"/>.</summary>
    public List<string> FoldersRead { get; } = [];

    public IReadOnlyList<string> EnumerateFolders(Func<string, bool> wanted, Action<IReadOnlyList<RemoteFile>> batch, CancellationToken ct)
    {
        EnsureReady();
        var read = new List<string>();
        foreach (var folder in Files.Select(f => f.Info.Folder.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!wanted(folder)) continue;
            read.Add(folder);
            FoldersRead.Add(folder);
            batch(Files.Select(f => f.Info).Where(f => f.Folder.Split('/')[0].Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList());
        }
        return read;
    }

    public byte[]? GetThumbnail(string objectId)
    {
        EnsureReady();
        return Find(objectId).Thumbnail;
    }

    public DownloadResult Download(string objectId, string destination, Action<long> progress, CancellationToken ct)
    {
        EnsureReady();
        DownloadCalls++;
        var file = Find(objectId);
        var data = file.Content();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        int chunk = 64 * 1024;
        long written = 0;
        // Un solo guasto per lettura, nell'ordine: così ogni contatore corrisponde a un tentativo.
        bool hang = Take(ref HangDownloads);
        bool fail = !hang && Take(ref FailDownloads);
        bool truncate = !hang && !fail && Take(ref TruncateDownloads);
        bool disconnect = !hang && !fail && !truncate && Take(ref DisconnectDuringDownloads);
        bool locks = !hang && !fail && !truncate && !disconnect && Take(ref LockDuringDownloads);
        long stopAt = truncate || fail || disconnect || hang || locks ? data.Length / 2 : data.Length;
        var started = DateTime.UtcNow;

        while (written < stopAt)
        {
            ct.ThrowIfCancellationRequested();
            int n = (int)Math.Min(chunk, stopAt - written);
            output.Write(data, (int)written, n);
            sha.AppendData(data, (int)written, n);
            written += n;
            progress(written);
            if (BytesPerSecond > 0)
            {
                var due = started + TimeSpan.FromSeconds((double)written / BytesPerSecond);
                var wait = due - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) ct.WaitHandle.WaitOne(wait);
            }
        }

        if (hang)
        {
            // Come il driver vero: nessun byte, nessun errore. Si sblocca solo chiudendo.
            WaitHandle.WaitAny([ct.WaitHandle, HangRelease.WaitHandle]);
            ct.ThrowIfCancellationRequested();
            throw new DeviceException("Blocco simulato terminato", DeviceErrors.GenFailure);
        }
        if (fail) throw new DeviceException("Errore simulato del driver", FailDownloadsCode);
        if (locks)
        {
            Locked = true;
            throw new DeviceException("Bloccato durante la copia", DeviceErrors.AccessDenied);
        }
        if (disconnect)
        {
            Unplugged = true;
            _open = false;
            throw new DeviceException("Scollegato durante la copia", DeviceErrors.DeviceNotConnected);
        }

        output.Flush(flushToDisk: true);
        return new DownloadResult(written, Convert.ToHexString(sha.GetHashAndReset()));
    }

    public IReadOnlyList<DeleteResult> Delete(IReadOnlyList<string> objectIds)
    {
        EnsureReady();
        if (LockOnNextDelete)
        {
            LockOnNextDelete = false;
            Locked = true;
            return objectIds.Select(id => new DeleteResult(id, false, DeviceErrors.AccessDenied)).ToList();
        }
        var results = new List<DeleteResult>();
        foreach (var id in objectIds)
        {
            var f = Files.FirstOrDefault(x => x.Info.ObjectId == id);
            if (f is null) { results.Add(new DeleteResult(id, false, DeviceErrors.ElementNotFound)); continue; }
            if (RefuseDelete || !f.Info.CanDelete) { results.Add(new DeleteResult(id, false, DeviceErrors.AccessDenied)); continue; }
            Files.Remove(f);
            Deleted.Add(f.Info.Name);
            results.Add(new DeleteResult(id, true, 0));
        }
        return results;
    }

    private FakeFile Find(string objectId) => Files.FirstOrDefault(f => f.Info.ObjectId == objectId)
        ?? throw new DeviceException("File non trovato", DeviceErrors.ElementNotFound);

    private static bool Take(ref int counter)
    {
        if (counter <= 0) return false;
        counter--;
        return true;
    }

    /// <summary>Simula lo scollegamento e il ricollegamento: gli ID oggetto cambiano come dopo un nuovo collegamento USB.</summary>
    public void Replug(string idPrefix = "r")
    {
        Unplugged = false;
        foreach (var f in Files) f.Info = f.Info with { ObjectId = idPrefix + f.Info.ObjectId };
    }

    /// <summary>Aggiunge un file con contenuto deterministico.</summary>
    public FakeFile Add(string folder, string name, int size, DateTime? created = null, byte[]? content = null,
        byte[]? thumbnail = null, bool canDelete = true)
    {
        var id = "o" + (Files.Count + 1).ToString("X");
        var bytes = content ?? Deterministic(folder + "/" + name, size);
        var f = new FakeFile
        {
            Info = new RemoteFile(id, name, folder, bytes.Length, created, created, canDelete, "puid-" + id),
            Content = () => bytes,
            Thumbnail = thumbnail,
        };
        Files.Add(f);
        return f;
    }

    public static byte[] Deterministic(string seed, int size)
    {
        var data = new byte[size];
        // Seme stabile tra un'esecuzione e l'altra (string.GetHashCode cambia a ogni avvio).
        var h = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        new Random(BitConverter.ToInt32(h, 0) ^ size).NextBytes(data);
        return data;
    }

    public void Dispose() { }
}
