using System.Windows;
using System.Windows.Threading;
using PonteIPhone.Core.Storage;
using PonteIPhone.Services;
using PonteIPhone.ViewModels;
using PonteIPhone.Views;
using Wpf.Ui.Appearance;

namespace PonteIPhone;

public partial class App : Application
{
    private readonly AppLog _log;
    private readonly bool _demo;
    private readonly string? _screenshots;

    public App(AppLog log, bool demo, string? screenshots)
    {
        _log = log;
        _demo = demo;
        _screenshots = screenshots;
        DispatcherUnhandledException += OnUnhandled;
    }

    // Costruttore per il designer di XAML.
    public App() : this(new AppLog(AppPaths.Logs), false, null) { }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _log.Info($"Avvio di Ponte iPhone {UpdateService.Current}{(_demo ? " (dimostrazione)" : "")}");

        var settings = AppSettings.Load(AppPaths.Settings);
        var vm = new MainViewModel(settings, _log, _demo);
        var window = new MainWindow(vm);
        MainWindow = window;

        if (_screenshots is null)
        {
            // Tema chiaro/scuro di Windows, anche quando l'utente lo cambia con l'app aperta.
            SystemThemeWatcher.Watch(window);
        }
        window.Show();
        if (_screenshots is not null) _ = ScreenshotRunner.RunAsync(window, vm, _screenshots, _log);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error("Errore nell'interfaccia", e.Exception);
        // In modalità schermate un errore deve far fallire la CI, non restare nascosto.
        if (_screenshots is not null)
        {
            Environment.Exit(3);
            return;
        }
        MessageBox.Show("Si è verificato un errore imprevisto:\n\n" + e.Exception.Message +
                        "\n\nIl dettaglio è nel registro (Impostazioni → Diagnostica).", "Ponte iPhone",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
