using System.Buffers.Binary;
using System.Net.Sockets;

namespace PonteIPhone.Core.Apple;

/// <summary>Un iPhone come lo vede il servizio Apple (Apple Mobile Device Service).</summary>
public sealed record UsbmuxDevice(int DeviceId, string Udid, string ConnectionType);

/// <summary>Errore del servizio Apple: non installato, non avviato, o risposta inattesa.</summary>
public sealed class UsbmuxException(string message, Exception? inner = null, long code = 0) : Exception(message, inner)
{
    /// <summary>Codice del servizio per "Connect" (3 = il telefono ha rifiutato la porta, di solito perché non è ancora pronto).</summary>
    public long Code { get; } = code;
}

/// <summary>
/// Il servizio Apple che fa da centralino verso gli iPhone collegati (usbmuxd; su Windows è
/// Apple Mobile Device Service, installato da Dispositivi Apple o iTunes, in ascolto su 127.0.0.1:27015).
/// Ogni richiesta usa un collegamento nuovo: dopo "Connect" il collegamento diventa il canale verso il telefono.
/// </summary>
public sealed class UsbmuxClient
{
    public const int WindowsPort = 27015;
    /// <summary>
    /// Tempo massimo per una risposta del servizio o del telefono. Senza, un telefono che non risponde
    /// lascerebbe il processo appeso finché il controllo dell'app non lo chiude, e la riserva WPD non partirebbe mai.
    /// </summary>
    public static TimeSpan ReplyTimeout { get; set; } = TimeSpan.FromSeconds(8);
    /// <summary>Per i dati dei file: un blocco da 1 MB in Wi-Fi può metterci qualche secondo.</summary>
    public static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(25);
    private const int MessagePlist = 8;
    private readonly Func<Stream> _connect;
    private int _tag;

    /// <param name="connect">Apre un collegamento con il servizio (nei test, un servizio finto).</param>
    public UsbmuxClient(Func<Stream>? connect = null)
    {
        _connect = connect ?? ConnectToService;
    }

    private static Stream ConnectToService()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            socket.Connect(System.Net.IPAddress.Loopback, WindowsPort);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            throw new UsbmuxException("Servizio Apple non raggiungibile (Dispositivi Apple installato?): " + e.Message, e);
        }
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>Tempo massimo di lettura e scrittura sul flusso (se lo permette): scaduto, arriva un'IOException.</summary>
    public static void SetTimeout(Stream s, TimeSpan t)
    {
        if (!s.CanTimeout) return;
        s.ReadTimeout = (int)t.TotalMilliseconds;
        s.WriteTimeout = (int)t.TotalMilliseconds;
    }

    /// <summary>Ogni collegamento nasce con il tempo massimo di risposta, qualunque sia il modo di aprirlo.</summary>
    private Stream Open()
    {
        var s = _connect();
        SetTimeout(s, ReplyTimeout);
        return s;
    }

    private static Dictionary<string, object> Base(string type) => new()
    {
        ["MessageType"] = type,
        ["ClientVersionString"] = "PonteIPhone",
        ["ProgName"] = "PonteIPhone",
        ["kLibUSBMuxVersion"] = 3L,
    };

    public IReadOnlyList<UsbmuxDevice> ListDevices()
    {
        using var s = Open();
        var reply = Exchange(s, Base("ListDevices"));
        var result = new List<UsbmuxDevice>();
        if (reply.TryGetValue("DeviceList", out var v) && v is List<object> list)
        {
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                var props = item.GetDict("Properties") ?? item;
                var id = props.GetLong("DeviceID") ?? item.GetLong("DeviceID");
                var udid = props.GetString("SerialNumber");
                if (id is null || string.IsNullOrEmpty(udid)) continue;
                result.Add(new UsbmuxDevice((int)id, udid, props.GetString("ConnectionType") ?? ""));
            }
        }
        return result;
    }

    /// <summary>L'abbinamento salvato dal servizio Apple quando si è toccato "Autorizza", o null se non c'è.</summary>
    public byte[]? ReadPairRecord(string udid)
    {
        using var s = Open();
        var msg = Base("ReadPairRecord");
        msg["PairRecordID"] = udid;
        var reply = Exchange(s, msg);
        return reply.GetData("PairRecordData");
    }

    /// <summary>Identificativo di questo PC presso il servizio Apple (va nell'abbinamento), o null.</summary>
    public string? ReadBuid()
    {
        using var s = Open();
        return Exchange(s, Base("ReadBUID")).GetString("BUID");
    }

    /// <summary>Salva un abbinamento nel servizio Apple (come fa iTunes). False se il servizio lo rifiuta.</summary>
    public bool SavePairRecord(string udid, int deviceId, byte[] record)
    {
        using var s = Open();
        var msg = Base("SavePairRecord");
        msg["PairRecordID"] = udid;
        msg["PairRecordData"] = record;
        msg["DeviceID"] = (long)deviceId;
        return (Exchange(s, msg).GetLong("Number") ?? -1) == 0;
    }

    /// <summary>Apre un canale verso una porta del telefono. Il chiamante possiede il flusso restituito.</summary>
    public Stream Connect(int deviceId, int port)
    {
        var s = Open();
        try
        {
            var msg = Base("Connect");
            msg["DeviceID"] = (long)deviceId;
            // La porta va in ordine di rete dentro un intero (come fanno tutti i client usbmux).
            msg["PortNumber"] = (long)(ushort)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
            var reply = Exchange(s, msg);
            var number = reply.GetLong("Number") ?? -1;
            if (number != 0)
                throw new UsbmuxException(number switch
                {
                    2 => "telefono non collegato",
                    3 => "il telefono ha rifiutato il collegamento alla porta " + port,
                    _ => $"collegamento rifiutato (codice {number})",
                }, code: number);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resta in ascolto degli iPhone che si collegano e scollegano ("Attached"/"Detached" con il DeviceID).
    /// Serve ad accorgersi subito che il telefono è sparito: da iOS 26 l'iPhone chiude i dati appena si blocca
    /// ("Accessori cablati") e una lettura in corso altrimenti aspetterebbe 25 secondi prima di arrendersi.
    /// Chiudere il valore restituito ferma l'ascolto.
    /// </summary>
    public IDisposable Listen(Action<string, int> onEvent)
    {
        var s = Open();
        try
        {
            var reply = Exchange(s, Base("Listen"));
            if ((reply.GetLong("Number") ?? -1) != 0) throw new UsbmuxException("il servizio Apple rifiuta l'ascolto");
            if (s.CanTimeout) s.ReadTimeout = Timeout.Infinite;
        }
        catch
        {
            s.Dispose();
            throw;
        }
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var msg = ReadMessage(s);
                    var id = msg.GetLong("DeviceID") ?? msg.GetDict("Properties")?.GetLong("DeviceID") ?? -1;
                    onEvent(msg.GetString("MessageType") ?? "", (int)id);
                }
            }
            catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or FormatException
                                          or System.Xml.XmlException or UsbmuxException) { }
        }) { IsBackground = true, Name = "usbmux ascolto" };
        thread.Start();
        return s;
    }

    private static Dictionary<string, object> ReadMessage(Stream s)
    {
        var h = new byte[16];
        s.ReadExactly(h);
        int len = BinaryPrimitives.ReadInt32LittleEndian(h);
        if (len < 16 || len > 64 << 20) throw new UsbmuxException("risposta del servizio Apple non valida");
        var payload = new byte[len - 16];
        s.ReadExactly(payload);
        return PropertyList.ParseDict(payload);
    }

    private Dictionary<string, object> Exchange(Stream s, Dictionary<string, object> msg)
    {
        var tag = Interlocked.Increment(ref _tag);
        var body = PropertyList.ToXml(msg);
        var header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, 16 + body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1); // versione 1 = plist
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), MessagePlist);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), tag);
        try
        {
            s.Write(header);
            s.Write(body);
            s.Flush();

            return ReadMessage(s);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or FormatException or System.Xml.XmlException)
        {
            throw new UsbmuxException("dialogo con il servizio Apple interrotto: " + e.Message, e);
        }
    }

    /// <summary>
    /// Copia salvata su disco dal servizio Apple per Windows (serve se il servizio non la dà su richiesta).
    /// Di solito leggibile; se Windows nega l'accesso si ritorna null.
    /// </summary>
    public static byte[]? ReadPairRecordFile(string udid)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Apple", "Lockdown");
            foreach (var name in new[] { udid, udid.Replace("-", "") })
            {
                var path = Path.Combine(folder, name + ".plist");
                if (File.Exists(path)) return File.ReadAllBytes(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
