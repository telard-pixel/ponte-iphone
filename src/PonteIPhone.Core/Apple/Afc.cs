using System.Buffers.Binary;
using System.Text;

namespace PonteIPhone.Core.Apple;

/// <summary>Errore AFC con il codice del telefono.</summary>
public sealed class AfcException(ulong code, string message) : Exception(message)
{
    public ulong Code { get; } = code;

    public const ulong ObjectNotFound = 8;
    public const ulong PermissionDenied = 10;
    public const ulong EndOfData = 14;
}

/// <summary>
/// Apple File Conduit: i file della cartella Media dell'iPhone (DCIM, PhotoData…), lo stesso servizio
/// che usano iTunes e Dispositivi Apple. Pacchetti: intestazione di 40 byte (little-endian) + dati.
/// Non adatto a più thread: chi lo usa fa una chiamata alla volta.
/// </summary>
public sealed class AfcClient : IDisposable
{
    public const string Service = "com.apple.afc";
    private static readonly byte[] Magic = "CFA6LPAA"u8.ToArray();
    private const int HeaderSize = 40;

    private const ulong OpStatus = 0x01;
    private const ulong OpData = 0x02;
    private const ulong OpReadDir = 0x03;
    private const ulong OpGetFileInfo = 0x0A;
    private const ulong OpGetDevInfo = 0x0B;
    private const ulong OpFileOpen = 0x0D;
    private const ulong OpFileOpenResult = 0x0E;
    private const ulong OpFileRead = 0x0F;
    private const ulong OpFileClose = 0x14;

    public const ulong ReadOnly = 1;
    /// <summary>Quanto chiedere a ogni lettura: abbastanza per la velocità, poco per l'avanzamento.</summary>
    public const int ReadChunk = 1 << 20;

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private ulong _packet;

    /// <param name="owner">Cosa chiudere insieme al flusso (es. il collegamento di base sotto il TLS).</param>
    public AfcClient(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    public List<string> ReadDirectory(string path)
    {
        var data = Execute(OpReadDir, PathArg(path), OpData);
        return SplitZero(data).Where(n => n is not ("." or "..")).ToList();
    }

    /// <summary>st_size, st_ifmt (S_IFREG/S_IFDIR), st_mtime e st_birthtime in nanosecondi…</summary>
    public Dictionary<string, string> GetFileInfo(string path) => Pairs(Execute(OpGetFileInfo, PathArg(path), OpData));

    /// <summary>FSTotalBytes, FSFreeBytes, Model…</summary>
    public Dictionary<string, string> GetDeviceInfo() => Pairs(Execute(OpGetDevInfo, [], OpData));

    public ulong Open(string path, ulong mode = ReadOnly)
    {
        var p = PathArg(path);
        var args = new byte[8 + p.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(args, mode);
        p.CopyTo(args, 8);
        var data = Execute(OpFileOpen, args, OpFileOpenResult);
        if (data.Length < 8) throw new IOException("risposta AFC senza handle");
        return BinaryPrimitives.ReadUInt64LittleEndian(data);
    }

    /// <summary>Legge fino a <paramref name="max"/> byte; un array vuoto vuol dire fine del file.</summary>
    public byte[] Read(ulong handle, int max = ReadChunk)
    {
        var args = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(args, handle);
        BinaryPrimitives.WriteUInt64LittleEndian(args.AsSpan(8), (ulong)max);
        try
        {
            return Execute(OpFileRead, args, OpData);
        }
        catch (AfcException e) when (e.Code == AfcException.EndOfData)
        {
            return [];
        }
    }

    public void Close(ulong handle)
    {
        var args = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(args, handle);
        Execute(OpFileClose, args, OpStatus);
    }

    /// <summary>Un file piccolo tutto in memoria (miniature).</summary>
    public byte[] ReadAll(string path, int limit)
    {
        var h = Open(path);
        try
        {
            using var ms = new MemoryStream();
            while (true)
            {
                var chunk = Read(h);
                if (chunk.Length == 0) break;
                ms.Write(chunk);
                if (ms.Length > limit) throw new IOException("file troppo grande per la memoria");
            }
            return ms.ToArray();
        }
        finally
        {
            Close(h);
        }
    }

    // ------------------------------------------------------------------ pacchetti

    private byte[] Execute(ulong op, byte[] args, ulong expected)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header, 0);
        ulong total = (ulong)(HeaderSize + args.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), total);   // lunghezza intera
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16), total);  // lunghezza di questa parte
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), _packet++);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), op);
        _stream.Write(header);
        if (args.Length > 0) _stream.Write(args);
        _stream.Flush();

        var h = new byte[HeaderSize];
        _stream.ReadExactly(h);
        if (!h.AsSpan(0, 8).SequenceEqual(Magic)) throw new IOException("risposta AFC non valida");
        var entire = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8));
        var replyOp = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32));
        if (entire < HeaderSize || entire > (64UL << 20)) throw new IOException("lunghezza AFC non valida");
        var data = new byte[entire - HeaderSize];
        _stream.ReadExactly(data);

        if (replyOp == OpStatus)
        {
            ulong code = data.Length >= 8 ? BinaryPrimitives.ReadUInt64LittleEndian(data) : 0;
            if (code != 0) throw new AfcException(code, Describe(code));
            // "Fatto" senza dati: atteso per la chiusura, e alcuni iOS lo usano per una cartella vuota o la fine del file.
            return [];
        }
        if (replyOp != expected) throw new IOException($"risposta AFC inattesa (0x{replyOp:X} invece di 0x{expected:X})");
        return data;
    }

    private static byte[] PathArg(string path)
    {
        var b = Encoding.UTF8.GetBytes(path);
        var r = new byte[b.Length + 1];
        b.CopyTo(r, 0);
        return r;
    }

    private static IEnumerable<string> SplitZero(byte[] data) =>
        Encoding.UTF8.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    private static Dictionary<string, string> Pairs(byte[] data)
    {
        var parts = Encoding.UTF8.GetString(data).Split('\0');
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < parts.Length; i += 2)
            if (parts[i].Length > 0) d[parts[i]] = parts[i + 1];
        return d;
    }

    public static string Describe(ulong code) => code switch
    {
        AfcException.ObjectNotFound => "file non trovato sul telefono",
        AfcException.PermissionDenied => "il telefono nega l'accesso (bloccato?)",
        9 => "è una cartella",
        17 => "file occupato sul telefono",
        20 => "errore di lettura sul telefono",
        _ => $"errore AFC {code}",
    };

    public void Dispose()
    {
        try { _stream.Dispose(); } catch (IOException) { }
        _owner?.Dispose();
    }
}
