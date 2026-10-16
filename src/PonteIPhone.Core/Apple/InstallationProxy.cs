using System.Buffers.Binary;
using PonteIPhone.Core.Model;

namespace PonteIPhone.Core.Apple;

/// <summary>
/// Il servizio dell'iPhone che elenca le app installate (lo stesso che usano iTunes e libimobiledevice).
/// Messaggi come lockdown: lunghezza su 4 byte big-endian + plist. Si usa solo per leggere: nome e
/// dimensioni (programma e dati), per la pagina "Spazio sull'iPhone".
/// </summary>
public static class InstallationProxy
{
    public const string Service = "com.apple.mobile.installation_proxy";

    private static readonly List<object> Attributes =
        ["CFBundleIdentifier", "CFBundleDisplayName", "CFBundleName", "StaticDiskUsage", "DynamicDiskUsage"];

    /// <summary>App installate dall'utente (non quelle di sistema), con le dimensioni dichiarate dal telefono.</summary>
    public static List<AppUsage> BrowseUserApps(Stream s, CancellationToken ct)
    {
        Write(s, new Dictionary<string, object>
        {
            ["Command"] = "Browse",
            ["ClientOptions"] = new Dictionary<string, object>
            {
                ["ApplicationType"] = "User",
                ["ReturnAttributes"] = Attributes,
            },
        });
        var apps = new List<AppUsage>();
        // Il telefono risponde a pezzi ("BrowsingApplications" con CurrentList) fino a "Complete".
        for (int guard = 0; guard < 10_000; guard++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = Read(s);
            if (reply.GetString("Error") is { } error)
                throw new LockdownException(error, "elenco delle app: il telefono risponde \"" + error + "\"");
            if (reply.TryGetValue("CurrentList", out var v) && v is List<object> list)
            {
                foreach (var app in list.OfType<Dictionary<string, object>>())
                {
                    var id = app.GetString("CFBundleIdentifier") ?? "";
                    var name = app.GetString("CFBundleDisplayName") is { Length: > 0 } dn ? dn
                        : app.GetString("CFBundleName") is { Length: > 0 } n ? n : id;
                    apps.Add(new AppUsage(name, id, Math.Max(0, app.GetLong("StaticDiskUsage") ?? 0), Math.Max(0, app.GetLong("DynamicDiskUsage") ?? 0)));
                }
            }
            if (reply.GetString("Status") == "Complete") return apps;
        }
        return apps;
    }

    internal static void Write(Stream s, Dictionary<string, object> msg)
    {
        var body = PropertyList.ToXml(msg);
        var len = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
        s.Write(len);
        s.Write(body);
        s.Flush();
    }

    internal static Dictionary<string, object> Read(Stream s)
    {
        var len = new byte[4];
        s.ReadExactly(len);
        var n = BinaryPrimitives.ReadUInt32BigEndian(len);
        if (n == 0 || n > 64 << 20) throw new IOException("risposta del telefono non valida");
        var body = new byte[n];
        s.ReadExactly(body);
        return PropertyList.ParseDict(body);
    }
}
