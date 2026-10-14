using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PonteIPhone.Core.Apple;

namespace PonteIPhone.Tests;

/// <summary>
/// Servizio Apple e iPhone finti, con gli stessi protocolli di quelli veri: usbmux (plist con
/// intestazione di 16 byte), lockdown (lunghezza big-endian, poi TLS con certificato del PC richiesto),
/// AFC (pacchetti "CFA6LPAA"). I file stanno in memoria.
/// </summary>
public sealed class FakeAppleDevice : IDisposable
{
    public const string Udid = "00008110-001A2B3C4D5E6F70";
    private const int DeviceId = 7;
    private const int WifiDeviceId = 8;
    private const int AfcPort = 49152;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly X509Certificate2 _deviceCert;
    public byte[] PairRecordXml { get; }
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public bool Locked { get; set; }
    public bool ServePairRecord { get; set; } = true;
    public bool Usb { get; set; } = true;
    public bool Wifi { get; set; }
    public int SessionsWithClientCert;

    public FakeAppleDevice()
    {
        // Un solo orario per tutti i certificati: il figlio non può scadere dopo chi lo firma.
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = from.AddYears(10);
        using var rootKey = RSA.Create(2048);
        var rootReq = new CertificateRequest("CN=Root", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootReq.CreateSelfSigned(from, to);

        using var hostKey = RSA.Create(2048);
        var hostReq = new CertificateRequest("CN=Host", hostKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var host = hostReq.Create(root, from, to.AddDays(-1), [1, 2, 3, 4]);

        using var devKey = RSA.Create(2048);
        var devReq = new CertificateRequest("CN=Device", devKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var dev = devReq.CreateSelfSigned(from, to);
        _deviceCert = X509CertificateLoader.LoadPkcs12(dev.Export(X509ContentType.Pkcs12), null);

        PairRecordXml = PropertyList.ToXml(new Dictionary<string, object>
        {
            ["HostID"] = "HOST-ID-1",
            ["SystemBUID"] = "BUID-1",
            ["HostCertificate"] = Encoding.ASCII.GetBytes(host.ExportCertificatePem()),
            ["HostPrivateKey"] = Encoding.ASCII.GetBytes(hostKey.ExportRSAPrivateKeyPem()),
            ["RootCertificate"] = Encoding.ASCII.GetBytes(root.ExportCertificatePem()),
            ["EscrowBag"] = new byte[] { 1, 2, 3 },
        });

        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    public UsbmuxClient Client() => new(() =>
    {
        var c = new TcpClient();
        c.Connect(IPAddress.Loopback, ((IPEndPoint)_listener.LocalEndpoint).Port);
        c.NoDelay = true;
        return c.GetStream();
    });

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() =>
            {
                try { using (c) ServeMux(c.GetStream()); }
                catch (Exception e) when (e is IOException or EndOfStreamException or ObjectDisposedException or System.Security.Authentication.AuthenticationException) { }
            });
        }
    }

    // ------------------------------------------------------------------ usbmux

    private void ServeMux(Stream s)
    {
        var h = new byte[16];
        s.ReadExactly(h);
        int len = BinaryPrimitives.ReadInt32LittleEndian(h);
        int tag = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(12));
        var body = new byte[len - 16];
        s.ReadExactly(body);
        var msg = PropertyList.ParseDict(body);
        switch (msg.GetString("MessageType"))
        {
            case "ListDevices":
            {
                // Come il servizio vero: col Wi-Fi attivo lo stesso iPhone compare due volte, con due DeviceID.
                var list = new List<object>();
                if (Wifi) list.Add(Entry(WifiDeviceId, "Network"));
                if (Usb) list.Add(Entry(DeviceId, "USB"));
                MuxReply(s, tag, new Dictionary<string, object> { ["DeviceList"] = list });
                break;
            }
            case "ReadPairRecord":
                MuxReply(s, tag, ServePairRecord
                    ? new Dictionary<string, object> { ["PairRecordData"] = PairRecordXml }
                    : new Dictionary<string, object> { ["MessageType"] = "Result", ["Number"] = 2L });
                break;
            case "Connect":
            {
                var raw = (int)(msg.GetLong("PortNumber") ?? 0);
                int port = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                var id = msg.GetLong("DeviceID");
                if (id == WifiDeviceId) Interlocked.Increment(ref WifiConnections);
                bool ok = ((id == DeviceId && Usb) || (id == WifiDeviceId && Wifi)) && port is LockdownClient.Port or AfcPort;
                MuxReply(s, tag, new Dictionary<string, object> { ["MessageType"] = "Result", ["Number"] = ok ? 0L : 3L });
                if (!ok) return;
                if (port == LockdownClient.Port) ServeLockdown(s);
                else ServeAfc(s);
                break;
            }
        }
    }

    public int WifiConnections;

    private static Dictionary<string, object> Entry(int id, string type) => new()
    {
        ["DeviceID"] = (long)id,
        ["MessageType"] = "Attached",
        ["Properties"] = new Dictionary<string, object> { ["ConnectionType"] = type, ["DeviceID"] = (long)id, ["SerialNumber"] = Udid },
    };

    private static void MuxReply(Stream s, int tag, Dictionary<string, object> msg)
    {
        var body = PropertyList.ToXml(msg);
        var h = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(h, 16 + body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8), 8);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(12), tag);
        s.Write(h);
        s.Write(body);
    }

    // ------------------------------------------------------------------ lockdown

    private void ServeLockdown(Stream raw)
    {
        Stream s = raw;
        while (true)
        {
            var len = new byte[4];
            try { s.ReadExactly(len); }
            catch (EndOfStreamException) { return; }
            var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(len)];
            s.ReadExactly(body);
            var msg = PropertyList.ParseDict(body);
            var req = msg.GetString("Request");
            Dictionary<string, object> reply = new() { ["Request"] = req ?? "" };
            switch (req)
            {
                case "QueryType": reply["Type"] = "com.apple.mobile.lockdown"; break;
                case "StartSession":
                    if (msg.GetString("HostID") != "HOST-ID-1") reply["Error"] = "InvalidHostID";
                    else { reply["SessionID"] = "S1"; reply["EnableSessionSSL"] = true; }
                    break;
                case "GetValue":
                    reply["Value"] = new Dictionary<string, object>
                    {
                        ["DeviceName"] = "iPhone di Prova", ["ProductType"] = "iPhone14,2", ["ProductVersion"] = "18.1", ["UniqueDeviceID"] = Udid,
                    };
                    break;
                case "StartService":
                    if (Locked && msg.GetData("EscrowBag") is null) reply["Error"] = "PasswordProtected";
                    else { reply["Port"] = (long)AfcPort; reply["EnableServiceSSL"] = false; }
                    break;
                case "StopSession": break;
                default: reply["Error"] = "UnknownRequest"; break;
            }
            var outBody = PropertyList.ToXml(reply);
            var outLen = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(outLen, (uint)outBody.Length);
            s.Write(outLen);
            s.Write(outBody);
            s.Flush();

            if (req == "StartSession" && reply.ContainsKey("SessionID"))
            {
                var ssl = new SslStream(raw, leaveInnerStreamOpen: true);
                ssl.AuthenticateAsServer(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _deviceCert,
                    ClientCertificateRequired = true,
                    RemoteCertificateValidationCallback = (_, _, _, _) => true,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                });
                if (ssl.RemoteCertificate is not null) Interlocked.Increment(ref SessionsWithClientCert);
                s = ssl;
            }
        }
    }

    // ------------------------------------------------------------------ AFC

    private void ServeAfc(Stream s)
    {
        var handles = new Dictionary<ulong, (byte[] Data, int Pos)>();
        ulong next = 1;
        while (true)
        {
            var h = new byte[40];
            try { s.ReadExactly(h); }
            catch (EndOfStreamException) { return; }
            Assert.Equal("CFA6LPAA", Encoding.ASCII.GetString(h, 0, 8));
            var entire = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8));
            var packet = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(24));
            var op = BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(32));
            var data = new byte[entire - 40];
            s.ReadExactly(data);
            string Path0(int offset) => Encoding.UTF8.GetString(data, offset, data.Length - offset).TrimEnd('\0');

            void Reply(ulong rop, byte[] payload)
            {
                var r = new byte[40];
                Encoding.ASCII.GetBytes("CFA6LPAA").CopyTo(r, 0);
                BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(8), (ulong)(40 + payload.Length));
                BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(16), 40);
                BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(24), packet);
                BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), rop);
                s.Write(r);
                s.Write(payload);
            }
            void Status(ulong code)
            {
                var p = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(p, code);
                Reply(1, p);
            }
            byte[] Zeros(IEnumerable<string> parts) => Encoding.UTF8.GetBytes(string.Concat(parts.Select(p => p + "\0")));

            if (Locked && op is 0x03 or 0x0A or 0x0D) { Status(AfcException.PermissionDenied); continue; }
            switch (op)
            {
                case 0x03: // READ_DIR
                {
                    var dir = Path0(0).TrimEnd('/') + "/";
                    var children = Files.Keys.Where(k => k.StartsWith(dir, StringComparison.Ordinal))
                        .Select(k => k[dir.Length..].Split('/')[0]).Distinct().ToList();
                    if (children.Count == 0) { Status(AfcException.ObjectNotFound); break; }
                    Reply(2, Zeros(new[] { ".", ".." }.Concat(children)));
                    break;
                }
                case 0x0A: // GET_FILE_INFO
                {
                    var path = Path0(0);
                    if (Files.TryGetValue(path, out var bytes))
                        Reply(2, Zeros(["st_size", bytes.Length.ToString(), "st_ifmt", "S_IFREG",
                            "st_birthtime", "1760000000000000000", "st_mtime", "1760000000000000000"]));
                    else if (Files.Keys.Any(k => k.StartsWith(path + "/", StringComparison.Ordinal)))
                        Reply(2, Zeros(["st_size", "0", "st_ifmt", "S_IFDIR"]));
                    else Status(AfcException.ObjectNotFound);
                    break;
                }
                case 0x0B: // GET_DEVINFO
                    Reply(2, Zeros(["Model", "iPhone14,2", "FSTotalBytes", "128000000000", "FSFreeBytes", "28000000000"]));
                    break;
                case 0x0D: // FILE_OPEN
                {
                    var path = Path0(8);
                    if (!Files.TryGetValue(path, out var bytes)) { Status(AfcException.ObjectNotFound); break; }
                    var hnd = next++;
                    handles[hnd] = (bytes, 0);
                    var p = new byte[8];
                    BinaryPrimitives.WriteUInt64LittleEndian(p, hnd);
                    Reply(0x0E, p);
                    break;
                }
                case 0x0F: // FILE_READ
                {
                    var hnd = BinaryPrimitives.ReadUInt64LittleEndian(data);
                    var want = (int)BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(8));
                    var (bytes, pos) = handles[hnd];
                    // Come il telefono vero, a volte meno di quanto chiesto.
                    int n = Math.Min(Math.Min(want, 300_000), bytes.Length - pos);
                    handles[hnd] = (bytes, pos + n);
                    Reply(2, bytes.AsSpan(pos, n).ToArray());
                    break;
                }
                case 0x14: // FILE_CLOSE
                    handles.Remove(BinaryPrimitives.ReadUInt64LittleEndian(data));
                    Status(0);
                    break;
                default:
                    Status(15);
                    break;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _deviceCert.Dispose();
    }
}
