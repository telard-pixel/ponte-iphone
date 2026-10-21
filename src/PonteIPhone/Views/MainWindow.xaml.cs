using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PonteIPhone.Services;
using PonteIPhone.ViewModels;
using Wpf.Ui.Controls;

namespace PonteIPhone.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private DeviceMonitor? _monitor;
    private bool _closing;
    private bool _closed;

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _monitor = new DeviceMonitor(this);
        _monitor.Changed += async () => await _vm.OnDevicesChangedAsync();
        UpdateInstaller.SignalReady(Program.Args);
        await _vm.StartAsync();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return; // seconda chiamata, a lavori finiti: si chiude davvero
        // La finestra resta aperta finché la chiusura (rapporti, aggiornamento, telefono) non è finita: il 17/10 chiuderla
        // subito e finire dopo faceva chiudere a WPF una finestra già in chiusura (InvalidOperationException).
        e.Cancel = true;
        if (_closing || !_vm.ConfirmClose()) return;
        _closing = true;
        _monitor?.Dispose();
        try
        {
            await _vm.ShutdownAsync();
        }
        finally
        {
            _closed = true;
            // Dopo, non dentro questo evento: se la chiusura finisce subito si è ancora dentro Closing, e WPF non permette
            // di richiudere da lì (17/10, 1.0.54). Con Application.Shutdown (installazione sul PC) la finestra si chiude comunque.
            _ = Dispatcher.BeginInvoke(() =>
            {
                if (PresentationSource.FromVisual(this) is not null) Close();
            });
        }
    }

    // ------------------------------------------------------------------ griglia

    private void RowsList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Larghezza utile: tolti i margini interni (20 + 16) e la barra di scorrimento.
        var width = RowsList.ActualWidth - 36 - SystemParameters.VerticalScrollBarWidth;
        if (width > 50) _vm.SetViewportWidth(width);
    }

    private void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TileViewModel tile }) return;
        if (e.ClickCount >= 2)
        {
            // Il primo clic ha già cambiato la selezione: il doppio clic la rimette com'era.
            _vm.ClickTile(tile, shift: false);
            _vm.OpenPreview(tile);
        }
        else
        {
            _vm.ClickTile(tile, shift: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
        e.Handled = true;
    }

    /// <summary>Con il riciclo dei contenitori, una tessera "esce dallo schermo" quando il suo contenitore passa a un'altra.</summary>
    private void Tile_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is TileViewModel old) old.IsVisible = false;
        if (e.NewValue is TileViewModel now) now.IsVisible = true;
    }

    private void InspectorImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm.FocusedTile is { } t) _vm.OpenPreview(t);
    }

    // ------------------------------------------------------------------ tastiera

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.PreviewOpen)
        {
            switch (e.Key)
            {
                case Key.Escape: _vm.ClosePreviewCommand.Execute(null); e.Handled = true; break;
                case Key.Left: _vm.PreviewPreviousCommand.Execute(null); e.Handled = true; break;
                case Key.Right: _vm.PreviewNextCommand.Execute(null); e.Handled = true; break;
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_vm.ConfirmMoveOpen || _vm.ConfirmDeleteOpen || _vm.ResultOpen) _vm.CancelDialogCommand.Execute(null);
            else if (_vm.IsSettingsOpen) _vm.CloseSettingsCommand.Execute(null);
            else _vm.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && !_vm.ConfirmMoveOpen && !_vm.ConfirmDeleteOpen && !_vm.ResultOpen && !_vm.IsSettingsOpen
                 && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            if (_vm.DeleteCommand.CanExecute(null)) _vm.DeleteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && _vm.FocusedTile is { } t && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            _vm.OpenPreview(t);
            e.Handled = true;
        }
    }
}
