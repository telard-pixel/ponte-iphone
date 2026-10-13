using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PonteIPhone.Core.Storage;

namespace PonteIPhone.Services;

public sealed record ReleaseInfo(Version Version, string Tag, string ExeUrl, long ExeSize, string SignatureUrl, string Notes);

/// <summary>
/// Aggiornamenti dalle Release pubbliche di GitHub. Il file scaricato si installa solo se la sua firma
/// separata (.sig) corrisponde al certificato incorporato nell'app: un file alterato non passa.
/// Poi la versione nuova si avvia come installatore (<see cref="UpdateInstaller"/>) e prende il posto di questa.
/// </summary>
public sealed class UpdateService(AppLog log)
{
    private const string Repo = "telard-pixel/ponte-iphone";
    public const string ExeName = "PonteIPhone.exe";

    public static Version Current
    {
        get
        {
            var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return Version.TryParse(info?.Split('+')[0], out var v) ? Normalize(v) : new Version(0, 0, 0);
        }
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PonteIPhone", Current.ToString()));
        return http;
    }

    public async Task<ReleaseInfo?> CheckAsync(CancellationToken ct)
    {
        using var http = Http();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        var latest = Parse(json);
        return latest is not null && latest.Version > Current ? latest : null;
    }

    public static ReleaseInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        string? exe = null, sig = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString();
                var url = a.GetProperty("browser_download_url").GetString();
                if (name == ExeName) { exe = url; size = a.GetProperty("size").GetInt64(); }
                else if (name == ExeName + ".sig") sig = url;
            }
        }
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        return exe is null || sig is null ? null : new ReleaseInfo(Normalize(version), tag, exe, size, sig, notes);
    }

    /// <summary>Scarica, verifica e avvia l'installazione. True se l'app deve chiudersi.</summary>
    public async Task<bool> InstallAsync(ReleaseInfo release, CancellationToken ct)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Percorso dell'app sconosciuto");
        Directory.CreateDirectory(AppPaths.Updates);
        foreach (var old in Directory.EnumerateFiles(AppPaths.Updates, "PonteIPhone-*")) TryDelete(old);

        var downloaded = Path.Combine(AppPaths.Updates, $"PonteIPhone-{release.Version}.exe");
        using (var http = Http())
        {
            await DownloadAsync(http, release.ExeUrl, downloaded, ct);
            var signature = await http.GetByteArrayAsync(release.SignatureUrl, ct);
            var bytes = await File.ReadAllBytesAsync(downloaded, ct);
            using var cert = TrustedCertificate();
            using var rsa = cert.GetRSAPublicKey();
            if (bytes.Length != release.ExeSize || rsa is null ||
                !rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                TryDelete(downloaded);
                throw new InvalidOperationException($"La versione {release.Version} non ha una firma valida: non installata");
            }
        }

        log.Info($"Aggiornamento alla {release.Version}: avvio l'installazione");
        var info = new ProcessStartInfo(downloaded) { UseShellExecute = false };
        info.ArgumentList.Add("--installa");
        info.ArgumentList.Add(exePath);
        info.ArgumentList.Add(Current.ToString());
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(info)?.Dispose();
        return true;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var part = path + ".part";
        await using (var file = File.Create(part))
        {
            await resp.Content.CopyToAsync(file, ct);
        }
        File.Move(part, path, overwrite: true);
    }

    public static X509Certificate2 TrustedCertificate()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("PonteIPhone.cer")
            ?? throw new InvalidOperationException("Certificato mancante nelle risorse");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return X509CertificateLoader.LoadCertificate(ms.ToArray());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// La versione nuova avviata con "--installa": aspetta che la vecchia si chiuda, la sostituisce
/// (stesso file, stesso percorso) e la riavvia. Se la nuova non parte entro 2 minuti, rimette la vecchia.
/// </summary>
public static class UpdateInstaller
{
    public static string BackupPath => Path.Combine(AppPaths.Updates, "precedente.exe");

    public static void Run(string target, string? fromVersion, string? oldPid, AppLog log)
    {
        log.Info($"Installazione della {UpdateService.Current} al posto della {fromVersion} in {target}");
        try
        {
            if (int.TryParse(oldPid, out var pid))
            {
                try
                {
                    using var old = Process.GetProcessById(pid);
                    if (!old.WaitForExit(TimeSpan.FromSeconds(60))) { old.Kill(entireProcessTree: false); old.WaitForExit(10_000); }
                }
                catch (ArgumentException) { }
            }
            Directory.CreateDirectory(AppPaths.Updates);
            Retry(() => File.Copy(target, BackupPath, overwrite: true));
            CopyOver(Environment.ProcessPath!, target);
        }
        catch (Exception e)
        {
            log.Error("Installazione dell'aggiornamento", e);
            Start(target, []);
            return;
        }

        var signal = $@"Local\PonteIPhone-partita-{Guid.NewGuid():N}";
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, signal);
        using var p = Start(target, ["--aggiornato-da", fromVersion ?? "?", "--segnale", signal]);
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (p is not null && DateTime.UtcNow < deadline)
        {
            if (ready.WaitOne(TimeSpan.FromSeconds(1)))
            {
                log.Info("Versione nuova partita");
                return;
            }
            if (p.HasExited) break;
        }

        log.Warn("La versione nuova non è partita: rimetto la precedente");
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); } catch { }
        try
        {
            CopyOver(BackupPath, target);
            Start(target, ["--aggiornamento-fallito", UpdateService.Current.ToString()])?.Dispose();
        }
        catch (Exception e)
        {
            log.Error("Ripristino della versione precedente", e);
        }
    }

    /// <summary>Chiamato dalla versione nuova appena la finestra è pronta.</summary>
    public static void SignalReady(string[] args)
    {
        var i = Array.IndexOf(args, "--segnale");
        if (i < 0 || i + 1 >= args.Length) return;
        try
        {
            if (EventWaitHandle.TryOpenExisting(args[i + 1], out var h))
            {
                using (h) h.Set();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException) { }
    }

    private static void CopyOver(string source, string target)
    {
        var temp = target + ".nuovo";
        Retry(() =>
        {
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        });
    }

    private static void Retry(Action action)
    {
        for (int i = 1; ; i++)
        {
            try { action(); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && i < 20) { Thread.Sleep(1000); }
        }
    }

    private static Process? Start(string exe, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) info.ArgumentList.Add(a);
            return Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// "Installa sul PC": copia l'app in %LOCALAPPDATA%\Programs\PonteIPhone (scrivibile, quindi aggiornabile
/// senza permessi di amministratore) e crea i collegamenti nel menu Start e sul desktop.
/// </summary>
public static class SelfInstall
{
    public static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PonteIPhone");

    public static string InstalledExe => Path.Combine(InstallFolder, UpdateService.ExeName);

    public static bool IsInstalled =>
        Environment.ProcessPath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copia, crea i collegamenti e avvia la copia installata. True se questa copia deve chiudersi.</summary>
    public static bool Install(bool desktopShortcut)
    {
        Directory.CreateDirectory(InstallFolder);
        File.Copy(Environment.ProcessPath!, InstalledExe, overwrite: true);
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Ponte iPhone.lnk"));
        if (desktopShortcut)
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ponte iPhone.lnk"));
        Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false, WorkingDirectory = InstallFolder })?.Dispose();
        return true;
    }

    private static void CreateShortcut(string path)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell non disponibile");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = InstalledExe;
            link.WorkingDirectory = InstallFolder;
            link.IconLocation = InstalledExe + ",0";
            link.Description = "Copia e sposta foto e video dall'iPhone";
            link.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
