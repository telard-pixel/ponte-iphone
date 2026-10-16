using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PonteIPhone.Core.Storage;
using PonteIPhone.ViewModels;
using Wpf.Ui.Appearance;

namespace PonteIPhone.Services;

/// <summary>
/// Modalità "--demo --screenshot cartella": la CI avvia l'app vera con il telefono finto, la porta
/// nelle varie schermate e salva un PNG di ognuna. Così l'aspetto si controlla prima di ogni rilascio.
/// </summary>
public static class ScreenshotRunner
{
    public static async Task RunAsync(Window window, MainViewModel vm, string folder, AppLog log)
    {
        try
        {
            Directory.CreateDirectory(folder);
            window.Width = 1360;
            window.Height = 860;
            window.WindowState = WindowState.Normal;

            foreach (var dark in new[] { false, true })
            {
                ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
                var suffix = dark ? "-scuro" : "";

                await WaitUntil(() => vm.Screen == AppScreen.Library, TimeSpan.FromSeconds(30));
                await Settle(2500);
                vm.DemoSelect(9);
                await Settle(1800);
                Save(window, Path.Combine(folder, $"1-libreria{suffix}.png"));

                if (dark) break; // il resto basta in chiaro

                vm.MoveCommand.Execute(null);
                await Settle(600);
                Save(window, Path.Combine(folder, "2-conferma-sposta.png"));
                vm.CancelDialogCommand.Execute(null);

                vm.DeleteCommand.Execute(null);
                await Settle(600);
                Save(window, Path.Combine(folder, "2b-conferma-elimina.png"));
                vm.CancelDialogCommand.Execute(null);

                vm.DemoShowTransfer();
                await Settle(600);
                Save(window, Path.Combine(folder, "3-trasferimento.png"));
                vm.DemoEndTransfer();

                vm.OpenFocusedPreviewCommand.Execute(null);
                await Settle(1500);
                Save(window, Path.Combine(folder, "4-anteprima.png"));
                vm.ClosePreviewCommand.Execute(null);

                vm.SetFilterCommand.Execute("NotCopied");
                await Settle(1500);
                Save(window, Path.Combine(folder, "5-da-copiare.png"));
                vm.SetFilterCommand.Execute("All");

                vm.OpenSettingsCommand.Execute(null);
                await Settle(600);
                Save(window, Path.Combine(folder, "6-impostazioni.png"));
                vm.CloseSettingsCommand.Execute(null);

                await vm.OpenStorageCommand.ExecuteAsync(null);
                await Settle(800);
                Save(window, Path.Combine(folder, "6b-spazio.png"));
                vm.SetFilterCommand.Execute("All");

                // Copia vera sul telefono finto, fino in fondo: prova anche il motore dentro l'app.
                vm.Settings.DestinationRoot = Path.Combine(Path.GetTempPath(), "PonteIPhone-demo-copie");
                vm.Settings.ConfirmMove = false;
                vm.CopyCommand.Execute(null);
                await WaitUntil(() => vm.ResultOpen, TimeSpan.FromSeconds(90));
                await Settle(800);
                Save(window, Path.Combine(folder, "7-esito-copia.png"));
                vm.CancelDialogCommand.Execute(null);
                await Settle(800);
                Save(window, Path.Combine(folder, "8-dopo-la-copia.png"));

                vm.DemoShowNoDevice();
                await Settle(600);
                Save(window, Path.Combine(folder, "9-nessun-iphone.png"));

                vm.DemoShowDrop();
                await Settle(600);
                Save(window, Path.Combine(folder, "9b-iphone-bloccato-ios26.png"));

                // Di nuovo la libreria per la versione scura.
                await vm.ReloadCommand.ExecuteAsync(null);
                await vm.OnDevicesChangedAsync();
            }
            log.Info("Schermate salvate in " + folder);
            Environment.Exit(0);
        }
        catch (Exception e)
        {
            log.Error("Schermate", e);
            File.WriteAllText(Path.Combine(folder, "ERRORE.txt"), e.ToString());
            Environment.Exit(2);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Condizione non raggiunta in tempo");
            await Task.Delay(200);
        }
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Save(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        root.UpdateLayout();
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        // Sfondo pieno (la Mica di Windows non entra nell'immagine), poi la finestra così com'è.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var bg = Application.Current.TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
        }
        bmp.Render(visual);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
