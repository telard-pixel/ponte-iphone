using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PonteIPhone.Core.Apple;

/// <summary>Come è andata la richiesta di abbinamento ("Autorizza questo computer?").</summary>
public enum PairOutcome
{
    /// <summary>Abbinato: il record è pronto e salvato.</summary>
    Paired,
    /// <summary>La domanda è sul telefono, l'utente non ha ancora risposto.</summary>
    WaitingForTrust,
    /// <summary>L'utente ha toccato "Non autorizzare".</summary>
    Denied,
    /// <summary>Il telefono è bloccato: la domanda compare solo da sbloccato.</summary>
    Locked,
}

/// <summary>
/// L'abbinamento creato dall'app, come fanno libimobiledevice e pymobiledevice3: certificato radice,
/// certificato del PC e certificato del telefono (con la sua chiave pubblica), tutti firmati dalla radice.
/// Serve quando il servizio Apple di Windows non ha ancora un abbinamento con questo telefono:
/// prima dalla 1.0.26 nessuno chiedeva "Autorizza", e l'app restava su "Sblocca l'iPhone".
/// I certificati si tengono gli stessi finché l'utente risponde: il telefono accetta la richiesta ripetuta.
/// </summary>
public sealed class PairingMaterial
{
    public required string HostId { get; init; }
    public required string SystemBuid { get; init; }
    public required byte[] RootCertificate { get; init; }
    public required byte[] RootPrivateKey { get; init; }
    public required byte[] HostCertificate { get; init; }
    public required byte[] HostPrivateKey { get; init; }
    public required byte[] DeviceCertificate { get; init; }

    public static PairingMaterial Create(byte[] devicePublicKeyPem, string systemBuid)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = from.AddYears(10);
        var empty = new X500DistinguishedName("");

        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest(empty, rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootReq.CreateSelfSigned(from, to);

        using var hostKey = RSA.Create(2048);
        using var host = Leaf(hostKey, root, from, to, 1);

        using var deviceKey = RSA.Create();
        deviceKey.ImportFromPem(Encoding.ASCII.GetString(devicePublicKeyPem).TrimEnd('\0', '\n', '\r', ' '));
        using var device = Leaf(deviceKey, root, from, to, 2);

        return new PairingMaterial
        {
            HostId = Guid.NewGuid().ToString().ToUpperInvariant(),
            SystemBuid = systemBuid,
            RootCertificate = Pem(root.ExportCertificatePem()),
            RootPrivateKey = Pem(rootKey.ExportRSAPrivateKeyPem()),
            HostCertificate = Pem(host.ExportCertificatePem()),
            HostPrivateKey = Pem(hostKey.ExportRSAPrivateKeyPem()),
            DeviceCertificate = Pem(device.ExportCertificatePem()),
        };
    }

    private static X509Certificate2 Leaf(RSA key, X509Certificate2 root, DateTimeOffset from, DateTimeOffset to, byte serial)
    {
        var req = new CertificateRequest(new X500DistinguishedName(""), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.Create(root, from, to, [serial]);
    }

    private static byte[] Pem(string s) => Encoding.ASCII.GetBytes(s + "\n");

    /// <summary>Quello che si manda al telefono con "Pair" (senza chiavi private).</summary>
    public Dictionary<string, object> ForDevice() => new()
    {
        ["DeviceCertificate"] = DeviceCertificate,
        ["HostCertificate"] = HostCertificate,
        ["RootCertificate"] = RootCertificate,
        ["HostID"] = HostId,
        ["SystemBUID"] = SystemBuid,
    };

    /// <summary>Il record completo da salvare (servizio Apple o file dell'app), nello stesso formato di quello di iTunes.</summary>
    public byte[] RecordXml(byte[]? escrowBag, string? wifiMac)
    {
        var d = ForDevice();
        d["HostPrivateKey"] = HostPrivateKey;
        d["RootPrivateKey"] = RootPrivateKey;
        if (escrowBag is not null) d["EscrowBag"] = escrowBag;
        if (!string.IsNullOrEmpty(wifiMac)) d["WiFiMACAddress"] = wifiMac;
        return PropertyList.ToXml(d);
    }
}
