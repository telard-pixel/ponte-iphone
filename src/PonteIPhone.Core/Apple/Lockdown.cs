using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PonteIPhone.Core.Apple;

/// <summary>Il telefono ha risposto con un errore (es. "PasswordProtected" se è bloccato).</summary>
public sealed class LockdownException(string error, string message) : Exception(message)
{
    public string Error { get; } = error;
}

/// <summary>
/// L'abbinamento tra questo PC e l'iPhone, creato quando si tocca "Autorizza": certificati
/// per la sessione cifrata e la "escrow bag" che permette di aprire i servizi anche col telefono bloccato.
/// </summary>
public sealed class PairRecord
{
    public required string HostId { get; init; }
    public required string SystemBuid { get; init; }
    public required byte[] HostCertificate { get; init; }
    public required byte[] HostPrivateKey { get; init; }
    public byte[]? EscrowBag { get; init; }

    public static PairRecord? From(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        var d = PropertyList.ParseDict(data);
        var hostId = d.GetString("HostID");
        var buid = d.GetString("SystemBUID");
        var cert = d.GetData("HostCertificate");
        var key = d.GetData("HostPrivateKey");
        if (hostId is null || cert is null || key is null) return null;
        return new PairRecord
        {
            HostId = hostId,
            SystemBuid = buid ?? "",
            HostCertificate = cert,
            HostPrivateKey = key,
            EscrowBag = d.GetData("EscrowBag"),
        };
    }

    /// <summary>Certificato con chiave privata, in una forma che il TLS di Windows accetta.</summary>
    public X509Certificate2 ClientCertificate()
    {
        static string Pem(byte[] b) => Encoding.ASCII.GetString(b).TrimEnd('\0', '\n', '\r', ' ');
        using var pem = X509Certificate2.CreateFromPem(Pem(HostCertificate), Pem(HostPrivateKey));
        // Con una chiave "effimera" il TLS di Windows non riesce a firmare: si ricarica da PKCS#12.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
    }
}

/// <summary>
/// lockdownd, il "portinaio" dell'iPhone (porta 62078): dà le informazioni sul telefono e apre i servizi,
/// tra cui AFC per i file del Rullino. Messaggi: lunghezza su 4 byte (big-endian) + plist XML.
/// </summary>
public sealed class LockdownClient : IDisposable
{
    public const int Port = 62078;
    private const string Label = "PonteIPhone";
    private readonly Stream _raw;
    private Stream _stream;
    private string? _sessionId;
    private X509Certificate2? _cert;

    public LockdownClient(Stream stream)
    {
        _raw = stream;
        _stream = stream;
    }

    public Dictionary<string, object> Request(string request, Dictionary<string, object>? extra = null)
    {
        var msg = new Dictionary<string, object> { ["Label"] = Label, ["Request"] = request };
        if (extra is not null) foreach (var (k, v) in extra) msg[k] = v;
        Send(msg);
        var reply = Receive();
        if (reply.GetString("Error") is { } error)
            throw new LockdownException(error, $"{request}: il telefono risponde \"{error}\"");
        return reply;
    }

    public string QueryType() => Request("QueryType").GetString("Type") ?? "";

    /// <summary>Tutti i valori pubblici (nome, modello, versione di iOS…). Meglio dopo <see cref="StartSession"/>.</summary>
    public Dictionary<string, object> GetValues(string? key = null, string? domain = null)
    {
        var extra = new Dictionary<string, object>();
        if (key is not null) extra["Key"] = key;
        if (domain is not null) extra["Domain"] = domain;
        var reply = Request("GetValue", extra);
        return reply.TryGetValue("Value", out var v) && v is Dictionary<string, object> d ? d : new Dictionary<string, object>();
    }

    /// <summary>Sessione con l'abbinamento: da qui il collegamento è cifrato con i certificati dell'abbinamento.</summary>
    public void StartSession(PairRecord pair)
    {
        var reply = Request("StartSession", new Dictionary<string, object>
        {
            ["HostID"] = pair.HostId,
            ["SystemBUID"] = pair.SystemBuid,
            ["ProtocolVersion"] = "2",
        });
        _sessionId = reply.GetString("SessionID");
        if (reply.GetBool("EnableSessionSSL"))
        {
            _cert = pair.ClientCertificate();
            _stream = StartTls(_raw, _cert);
        }
    }

    /// <summary>
    /// Chiede "Autorizza questo computer?" (va fatto senza sessione). Restituisce l'esito e, se abbinato,
    /// la escrow bag che il telefono consegna per aprire i servizi anche da bloccato.
    /// </summary>
    public (PairOutcome Outcome, byte[]? EscrowBag) Pair(PairingMaterial material)
    {
        try
        {
            var reply = Request("Pair", new Dictionary<string, object>
            {
                ["PairRecord"] = material.ForDevice(),
                ["ProtocolVersion"] = "2",
                ["PairingOptions"] = new Dictionary<string, object> { ["ExtendedPairingErrors"] = true },
            });
            return (PairOutcome.Paired, reply.GetData("EscrowBag"));
        }
        catch (LockdownException e) when (e.Error == "PairingDialogResponsePending") { return (PairOutcome.WaitingForTrust, null); }
        catch (LockdownException e) when (e.Error == "UserDeniedPairing") { return (PairOutcome.Denied, null); }
        catch (LockdownException e) when (e.Error == "PasswordProtected") { return (PairOutcome.Locked, null); }
    }

    /// <summary>Apre un servizio del telefono: restituisce la porta e se va cifrato anche quello.</summary>
    public (int Port, bool Ssl) StartService(string service, byte[]? escrowBag = null)
    {
        var extra = new Dictionary<string, object> { ["Service"] = service };
        if (escrowBag is not null) extra["EscrowBag"] = escrowBag;
        var reply = Request("StartService", extra);
        var port = reply.GetLong("Port") ?? throw new LockdownException("NoPort", "il telefono non ha dato la porta del servizio " + service);
        return ((int)port, reply.GetBool("EnableServiceSSL"));
    }

    /// <summary>TLS come lo vuole l'iPhone: certificato dell'abbinamento, certificato del telefono non verificabile (è suo).</summary>
    public static SslStream StartTls(Stream inner, X509Certificate2 clientCert)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: true);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = "",
            ClientCertificates = new X509CertificateCollection { clientCert },
            LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCert,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        try
        {
            ssl.AuthenticateAsClient(options);
        }
        catch (Exception e) when (e is AuthenticationException or IOException)
        {
            ssl.Dispose();
            throw new LockdownException("SslFailed", "sessione cifrata con l'iPhone non riuscita: " + e.Message);
        }
        return ssl;
    }

    private void Send(Dictionary<string, object> msg)
    {
        var body = PropertyList.ToXml(msg);
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        _stream.Write(len);
        _stream.Write(body);
        _stream.Flush();
    }

    private Dictionary<string, object> Receive()
    {
        var len = new byte[4];
        _stream.ReadExactly(len);
        var n = BinaryPrimitives.ReadUInt32BigEndian(len);
        if (n == 0 || n > 16 << 20) throw new IOException("risposta del telefono non valida");
        var body = new byte[n];
        _stream.ReadExactly(body);
        return PropertyList.ParseDict(body);
    }

    public void Dispose()
    {
        try
        {
            if (_sessionId is not null) Request("StopSession", new Dictionary<string, object> { ["SessionID"] = _sessionId });
        }
        catch (Exception e) when (e is IOException or LockdownException or ObjectDisposedException or FormatException) { }
        if (!ReferenceEquals(_stream, _raw)) _stream.Dispose();
        _raw.Dispose();
        _cert?.Dispose();
    }
}
