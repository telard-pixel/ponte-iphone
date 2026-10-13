using System.IO;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Storage;
using PonteIPhone.Services;
using PonteIPhone.Wpd;

namespace PonteIPhone;

/// <summary>
/// Un solo eseguibile, quattro modi:
///   (niente)                         l'app;
///   --worker &lt;pipe&gt; &lt;pid&gt;              il processo separato che parla con l'iPhone;
///   --installa &lt;exe&gt; &lt;versione&gt; &lt;pid&gt;    la versione nuova che si mette al posto della vecchia;
///   --demo [--screenshot &lt;cartella&gt;]   telefono finto (prove e schermate della CI).
/// </summary>
public static class Program
{
    public static string[] Args { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Args = args;
        if (args.Length >= 3 && args[0] == "--worker")
        {
            // Nessuna finestra, nessun WPF: solo il dialogo con l'iPhone.
            return WorkerHost.RunWorkerProcess(args[1], int.Parse(args[2]), () => new WpdBackend());
        }

        bool demo = args.Contains("--demo");
        // La dimostrazione lavora in una cartella temporanea tutta sua: non tocca impostazioni e registro veri.
        if (demo) AppPaths.Root = Path.Combine(Path.GetTempPath(), "PonteIPhone-demo-" + Environment.ProcessId);

        var log = new AppLog(AppPaths.Logs);
        if (args.Length >= 2 && args[0] == "--installa")
        {
            UpdateInstaller.Run(args[1], args.ElementAtOrDefault(2), args.ElementAtOrDefault(3), log);
            return 0;
        }

        string? screenshots = null;
        var shotIndex = Array.IndexOf(args, "--screenshot");
        if (shotIndex >= 0 && shotIndex + 1 < args.Length) screenshots = args[shotIndex + 1];

        // Una sola finestra aperta: una seconda copia porta in primo piano la prima.
        using var single = new Mutex(true, demo ? $"Local\\PonteIPhone-demo-{Environment.ProcessId}" : "Local\\PonteIPhone-istanza", out bool first);
        if (!first)
        {
            SingleInstance.ActivateExisting();
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) log.Error("Errore non gestito", ex);
        };

        var app = new App(log, demo, screenshots);
        app.InitializeComponent();
        return app.Run();
    }
}
