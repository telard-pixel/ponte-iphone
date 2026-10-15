using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Storage;
using PonteIPhone.Core.Transfer;
using PonteIPhone.Services;

namespace PonteIPhone.ViewModels;

public enum AppScreen
{
    /// <summary>Nessun iPhone collegato.</summary>
    NoDevice,
    /// <summary>iPhone collegato ma bloccato o non autorizzato.</summary>
    Locked,
    /// <summary>iPhone senza Rullino visibile.</summary>
    NoCameraRoll,
    /// <summary>Lettura dell'elenco in corso (la griglia si riempie man mano).</summary>
    Loading,
    Library,
}

public enum LibraryFilter { All, Photos, Live, Videos, Screenshots, NotCopied }

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private readonly AppLog _log;
    private readonly bool _demo;
    private readonly CopyRegistry _registry;
    private readonly IImageConverter _converter = new MagickImageConverter();
    private readonly UpdateService _updates;
    private readonly SemaphoreSlim _deviceLock = new(1, 1);
    private readonly Dispatcher _dispatcher = Application.Current.Dispatcher;
    private readonly DispatcherTimer _poll;
    private FakeBackend? _demoBackend;
    private IDeviceConnection? _browse;
    private ThumbnailService? _thumbs;
    private PreviewService? _previews;
    private readonly List<RemoteFile> _files = [];
    private List<MediaItem> _items = [];
    private readonly Dictionary<string, TileViewModel> _tiles = new(StringComparer.Ordinal);
    private Dictionary<string, CopyRecord> _copies = new(StringComparer.Ordinal);
    private List<TileViewModel> _visibleOrder = [];
    private TileViewModel? _anchor;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _transferCts;
    private CancellationTokenSource? _focusCts;
    private PauseController? _pause;
    private double _viewportWidth = 900;
    private ReleaseInfo? _release;
    private readonly ProblemReporter _reporter;

    public AppSettings Settings { get; }

    public MainViewModel(AppSettings settings, AppLog log, bool demo)
    {
        Settings = settings;
        _log = log;
        _demo = demo;
        _registry = new CopyRegistry(AppPaths.Registry);
        _updates = new UpdateService(log);
        _reporter = new ProblemReporter(log);
        _tileSize = settings.ThumbnailSize;
        _newestFirst = settings.NewestFirst;
        _showInspector = false; // pannello dettagli tolto: l'anteprima si apre col doppio clic
        // App semplice (14/10): queste scelte non sono più impostazioni, valgono sempre così.
        settings.ByYearMonth = true;
        settings.SkipAlreadyCopied = true;
        settings.KeepHeicOriginal = false;
        settings.IncludeEdited = true;
        settings.IncludeSidecars = false;
        settings.ConfirmMove = true;
        settings.KeepAwake = true;
        settings.AutoUpdate = true;
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += async (_, _) =>
        {
            // Un errore imprevisto nel controllo periodico non deve aprire una finestra d'errore ogni 3 secondi.
            try { await PollAsync(); }
            catch (Exception e) { _log.Error("Controllo periodico", e); }
        };
    }

    // ================================================================== stato generale

    [ObservableProperty] private AppScreen _screen = AppScreen.NoDevice;
    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _driverInstalled = true;
    [ObservableProperty] private DeviceInfo? _device;
    [ObservableProperty] private StorageInfo? _storage;
    [ObservableProperty] private string _loadingText = "";
    [ObservableProperty] private string? _bannerText;
    [ObservableProperty] private bool _bannerIsError;

    public bool HasDevice => Device is not null && Screen is AppScreen.Library or AppScreen.Loading;
    public bool ShowNoDevice => !IsSettingsOpen && Screen == AppScreen.NoDevice;
    public bool ShowLocked => !IsSettingsOpen && Screen == AppScreen.Locked;
    public bool ShowNoCameraRoll => !IsSettingsOpen && Screen == AppScreen.NoCameraRoll;
    public bool ShowLibrary => !IsSettingsOpen && Screen is AppScreen.Library or AppScreen.Loading;
    public bool IsLoading => Screen == AppScreen.Loading;
    public bool ShowBottomBar => ShowLibrary || IsTransferring;
    public bool ShowActions => ShowLibrary && !IsTransferring;
    /// <summary>Voce attiva della barra laterale ("Settings" o il nome del filtro).</summary>
    public string ActiveNav => IsSettingsOpen ? "Settings" : Filter.ToString();

    partial void OnScreenChanged(AppScreen value) => RaiseScreen();
    partial void OnIsSettingsOpenChanged(bool value) => RaiseScreen();

    private void RaiseScreen()
    {
        OnPropertyChanged(nameof(HasDevice));
        OnPropertyChanged(nameof(ShowNoDevice));
        OnPropertyChanged(nameof(ShowLocked));
        OnPropertyChanged(nameof(ShowNoCameraRoll));
        OnPropertyChanged(nameof(ShowLibrary));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(ShowBottomBar));
        OnPropertyChanged(nameof(ShowActions));
        OnPropertyChanged(nameof(ActiveNav));
        OnPropertyChanged(nameof(IsFilteredEmpty));
        OnPropertyChanged(nameof(ShowInspectorPanel));
        OnPropertyChanged(nameof(DeviceStatus));
        CopyCommand.NotifyCanExecuteChanged();
        MoveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnDeviceChanged(DeviceInfo? value)
    {
        OnPropertyChanged(nameof(DeviceName));
        OnPropertyChanged(nameof(DeviceModel));
        OnPropertyChanged(nameof(DeviceStatus));
    }

    partial void OnStorageChanged(StorageInfo? value)
    {
        OnPropertyChanged(nameof(StorageText));
        OnPropertyChanged(nameof(StorageFraction));
        OnPropertyChanged(nameof(HasStorage));
    }

    public string DeviceName => Device?.DisplayName ?? "Nessun iPhone";

    public string DeviceModel => Device is null ? "" :
        string.Join(" · ", new[] { Device.Model, string.IsNullOrWhiteSpace(Device.Firmware) ? "" : "iOS " + Device.Firmware }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string DeviceStatus => Screen switch
    {
        AppScreen.NoDevice => "Non collegato",
        AppScreen.Locked => "Bloccato: sbloccalo",
        AppScreen.NoCameraRoll => "Collegato, senza foto",
        AppScreen.Loading => "Lettura in corso…",
        _ => !IsTransferring ? "Collegato" : _transferMode == TransferMode.Delete ? "Eliminazione in corso" : "Trasferimento in corso",
    };

    private TransferMode _transferMode;

    public bool HasStorage => Storage is { Capacity: > 0 };
    public string StorageText => Storage is { Capacity: > 0 } s
        ? $"{TransferEngine.Format(s.Used)} usati di {TransferEngine.Format(s.Capacity)}" : "";
    public double StorageFraction => Storage is { Capacity: > 0 } s ? (double)s.Used / s.Capacity : 0;

    // ================================================================== avvio e telefono

    public async Task StartAsync()
    {
        DriverInstalled = _demo || AppleDriver.IsInstalled();
        UpdateSidebarSummary();
        if (_demo)
        {
            _demoBackend = DemoLibrary.Create();
            _browse = new InProcessConnection(_demoBackend, "iPhone (dimostrazione)");
        }
        else
        {
            _browse = new WorkerConnection(new ProcessWorkerLauncher(Environment.ProcessPath!), log: _log.Info);
        }
        await RefreshDevicesAsync();
        _poll.Start();
        if (!_demo) _ = CheckUpdatesAsync(automatic: true);
    }

    /// <summary>Notifica di Windows: un dispositivo si è collegato o scollegato.</summary>
    public Task OnDevicesChangedAsync() => RefreshDevicesAsync();

    private async Task PollAsync()
    {
        // Senza telefono (o bloccato) si ricontrolla ogni 3 secondi: le notifiche di Windows a volte non arrivano.
        if (IsTransferring || Screen is AppScreen.Library or AppScreen.Loading) return;
        await RefreshDevicesAsync();
    }

    private async Task RefreshDevicesAsync()
    {
        if (_browse is null || IsTransferring) return;
        if (!await _deviceLock.WaitAsync(0)) return;
        try
        {
            IReadOnlyList<DeviceInfo> list;
            try
            {
                list = await _browse.ListDevicesAsync(CancellationToken.None);
            }
            catch (DeviceException e)
            {
                _log.Warn("Elenco dei telefoni: " + e.Message);
                return;
            }

            if (Device is { } current && Screen is AppScreen.Library or AppScreen.Loading)
            {
                if (list.Any(d => d.Id == current.Id)) return;
                _log.Info("iPhone scollegato");
                CloseLibrary();
                Screen = AppScreen.NoDevice;
                ShowBanner(null);
                return;
            }

            if (list.Count == 0)
            {
                SetLocked(false);
                if (Screen != AppScreen.NoDevice) { CloseLibrary(); Screen = AppScreen.NoDevice; }
                DriverInstalled = _demo || AppleDriver.IsInstalled();
                return;
            }
            await OpenDeviceAsync(list[0]);
        }
        finally
        {
            _deviceLock.Release();
        }
    }

    private async Task OpenDeviceAsync(DeviceInfo dev)
    {
        OpenResult result;
        try
        {
            result = await _browse!.OpenAsync(dev.Id, CancellationToken.None);
        }
        catch (DeviceException e)
        {
            _log.Warn("Apertura dell'iPhone: " + e.Message);
            Device = dev;
            Screen = AppScreen.Locked;
            SetLocked(true);
            return;
        }

        Device = result.Device with { Name = string.IsNullOrWhiteSpace(result.Device.Name) ? dev.Name : result.Device.Name };
        Storage = result.Storage;
        _openNotes = result.Notes ?? [];
        DriverInstalled = _demo || AppleDriver.IsInstalled();
        foreach (var n in _openNotes) _log.Info("iPhone: " + n);
        switch (result.State)
        {
            case OpenState.LockedOrNotTrusted:
                Screen = AppScreen.Locked;
                SetLocked(true);
                return;
            case OpenState.NoCameraRoll:
                Screen = AppScreen.NoCameraRoll;
                ReportProblem("nessuna foto visibile");
                SetLocked(true);
                return;
        }
        SetLocked(false);
        _log.Info($"Aperto {Device.DisplayName} ({Device.Model}, iOS {Device.Firmware})");
        await LoadLibraryAsync();
    }

    private DateTime? _lockedSince;

    /// <summary>Foto viste sul telefono ma rimaste solo su iCloud (file vuoti): non si possono copiare via cavo.</summary>
    [ObservableProperty] private int _cloudOnlyCount;
    public bool HasCloudOnly => CloudOnlyCount > 0;
    public string CloudOnlyText =>
        $"Sull'iPhone ci sono {CloudOnlyCount.ToString("N0", It)} foto e video, ma sono solo su iCloud (“Ottimizza spazio iPhone”): via cavo il telefono non li consegna, nemmeno a Windows.";
    partial void OnCloudOnlyCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasCloudOnly));
        OnPropertyChanged(nameof(CloudOnlyText));
    }

    /// <summary>Da quanto il telefono è collegato ma senza foto visibili (bloccato, non autorizzato, driver).</summary>
    [ObservableProperty] private bool _lockedForAWhile;

    private void SetLocked(bool locked)
    {
        if (!locked) _lockedSince = null;
        else _lockedSince ??= DateTime.UtcNow;
        LockedForAWhile = _lockedSince is { } t && DateTime.UtcNow - t > TimeSpan.FromSeconds(20);
        if (_lockedSince is { } since && DateTime.UtcNow - since > TimeSpan.FromMinutes(1))
            ReportProblem("iPhone collegato ma bloccato da più di un minuto");
    }

    private void CloseLibrary()
    {
        _loadCts?.Cancel();
        _thumbs?.Dispose();
        _thumbs = null;
        _previews = null;
        _files.Clear();
        CloudOnlyCount = 0;
        _items = [];
        _tiles.Clear();
        Rows.Clear();
        FocusedTile = null;
        Device = null;
        Storage = null;
        UpdateCounts();
        UpdateSelection();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsTransferring || Device is null) return;
        var dev = Device;
        CloseLibrary();
        Screen = AppScreen.NoDevice;
        await _deviceLock.WaitAsync();
        try { await OpenDeviceAsync(dev); }
        finally { _deviceLock.Release(); }
    }

    private Stopwatch? _loadClock;

    /// <summary>Legge l'elenco dal telefono; la griglia si aggiorna ogni secondo e mezzo mentre arriva.</summary>
    private async Task LoadLibraryAsync(int attempt = 1)
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        Screen = AppScreen.Loading;
        LoadingText = "Leggo le foto dal telefono…";
        _files.Clear();
        CloudOnlyCount = 0;
        _tiles.Clear();
        _copies = _registry.Load(Device!.Key);
        _thumbs?.Dispose();
        _thumbs = new ThumbnailService(_browse!, Device.Key, _dispatcher, _log);
        _previews = new PreviewService(_browse!, Device.Key);
        UpdateSidebarSummary();

        _loadClock = Stopwatch.StartNew();
        var lastRefresh = Stopwatch.StartNew();
        try
        {
            var summary = await _browse!.EnumerateAsync(batch =>
            {
                _dispatcher.Invoke(() =>
                {
                    _files.AddRange(batch);
                    LoadingText = $"Leggo le foto dal telefono… {_files.Count.ToString("N0", It)} file";
                    if (lastRefresh.Elapsed > TimeSpan.FromSeconds(1.5))
                    {
                        lastRefresh.Restart();
                        Regroup();
                    }
                });
            }, ct);
            EnumerationSeconds = _loadClock.Elapsed.TotalSeconds;
            CloudOnlyCount = summary.CloudOnly;
            Regroup();
            if (_items.Count == 0)
            {
                // Aperto ma senza foto né video: si dice, invece di mostrare una griglia vuota.
                _log.Warn($"Nessuna foto o video trovati ({_files.Count} file, {summary.CloudOnly} solo su iCloud)");
                Screen = AppScreen.NoCameraRoll;
                ReportProblem(summary.CloudOnly > 0 ? "foto solo su iCloud" : "nessuna foto visibile");
                return;
            }
            Screen = AppScreen.Library;
            _log.Info($"Libreria letta: {_files.Count} file, {_items.Count} elementi, {summary.CloudOnly} solo su iCloud, in {EnumerationSeconds:0.0} s");
            if (summary.CloudOnly > 0)
            {
                ShowBanner($"Altre {summary.CloudOnly.ToString("N0", It)} foto e video sono solo su iCloud (“Ottimizza spazio iPhone”): via cavo l'iPhone non li dà. "
                    + "Per copiarli: sull'iPhone Impostazioni › [il tuo nome] › iCloud › Foto › “Scarica e mantieni originali”, aspetta che finisca e premi Aggiorna.");
                ReportProblem("foto solo su iCloud");
            }
            else ShowBanner(null);
            _ = VerifyCopiesOnDiskAsync();
        }
        catch (OperationCanceledException) { }
        catch (DeviceException e)
        {
            _log.Warn("Lettura della libreria: " + e.Message);
            Regroup();
            if (e.Kind == DeviceErrorKind.Gone)
            {
                CloseLibrary();
                Screen = AppScreen.NoDevice;
            }
            else if (attempt < 3 && !ct.IsCancellationRequested)
            {
                // Telefono lento o bloccato un attimo: il collegamento si riapre da solo, si rilegge.
                LoadingText = "Il telefono non risponde: riprovo…";
                try { await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct); }
                catch (OperationCanceledException) { return; }
                await LoadLibraryAsync(attempt + 1);
            }
            else
            {
                Screen = _items.Count > 0 ? AppScreen.Library : AppScreen.Locked;
                ShowBanner("Il telefono non risponde. Sbloccalo e premi Aggiorna (in alto a destra).", error: true);
                ReportProblem("lettura della libreria non riuscita");
            }
        }
    }

    public double EnumerationSeconds { get; private set; }

    private IReadOnlyList<string> _openNotes = [];

    /// <summary>
    /// Il registro dice "copiato": si controlla in sottofondo che i file siano ancora sul PC
    /// (se l'utente li ha cancellati o spostati, il bollino sparisce e tornano "da copiare").
    /// </summary>
    private async Task VerifyCopiesOnDiskAsync()
    {
        var snapshot = _copies.Values.ToList();
        var gone = await Task.Run(() => snapshot.Where(r => !CopyRegistry.StillOnDisk(r)).Select(r => r.FileKey).ToHashSet());
        if (gone.Count == 0) return;
        foreach (var k in gone) _copies.Remove(k);
        foreach (var t in _tiles.Values) t.IsOnPc = IsOnPc(t.Item);
        UpdateCounts();
        RefreshFocused();
        if (Filter == LibraryFilter.NotCopied) BuildRows();
    }

    private bool IsOnPc(MediaItem item) =>
        item.FilesToCopy(Settings.IncludeEdited, Settings.IncludeSidecars).All(f => _copies.ContainsKey(f.File.Key));

    // ================================================================== griglia

    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty] private LibraryFilter _filter = LibraryFilter.All;
    [ObservableProperty] private int _tileSize;
    [ObservableProperty] private double _tileWidth = 120;
    [ObservableProperty] private bool _newestFirst;
    [ObservableProperty] private int _countAll;
    [ObservableProperty] private int _countPhotos;
    [ObservableProperty] private int _countLive;
    [ObservableProperty] private int _countVideos;
    [ObservableProperty] private int _countScreenshots;
    [ObservableProperty] private int _countNotCopied;
    [ObservableProperty] private string _libraryTitle = "Foto e video";
    [ObservableProperty] private string _librarySubtitle = "";

    partial void OnFilterChanged(LibraryFilter value)
    {
        LibraryTitle = value switch
        {
            LibraryFilter.Photos => "Foto",
            LibraryFilter.Live => "Live Photo",
            LibraryFilter.Videos => "Video",
            LibraryFilter.Screenshots => "Screenshot",
            LibraryFilter.NotCopied => "Da copiare",
            _ => "Foto e video",
        };
        IsSettingsOpen = false;
        OnPropertyChanged(nameof(ActiveNav));
        BuildRows();
    }

    [RelayCommand]
    private void SetFilter(string name)
    {
        if (Enum.TryParse<LibraryFilter>(name, out var f))
        {
            if (f == Filter) { IsSettingsOpen = false; return; }
            Filter = f;
        }
    }

    partial void OnTileSizeChanged(int value)
    {
        Settings.ThumbnailSize = value;
        SaveSettings();
        BuildRows();
    }

    partial void OnNewestFirstChanged(bool value)
    {
        Settings.NewestFirst = value;
        SaveSettings();
        OnPropertyChanged(nameof(SortLabel));
        BuildRows();
    }

    public string SortLabel => NewestFirst ? "Più recenti prima" : "Meno recenti prima";

    [RelayCommand]
    private void ToggleSort() => NewestFirst = !NewestFirst;

    [RelayCommand]
    private void ToggleInspector() => ShowInspector = !ShowInspector;

    public void SetViewportWidth(double width)
    {
        if (Math.Abs(width - _viewportWidth) < 1) return;
        var oldColumns = Columns;
        _viewportWidth = width;
        if (Columns != oldColumns) BuildRows();
        else UpdateTileWidth();
    }

    private const double Gap = 4;

    private int Columns => Math.Max(1, (int)((_viewportWidth + Gap) / (TileSize + Gap)));

    private void UpdateTileWidth() => TileWidth = Math.Floor((_viewportWidth - Gap * (Columns - 1)) / Columns) - 0.5;

    private void Regroup()
    {
        _items = MediaGrouper.Group(_files);
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _items)
        {
            live.Add(item.Id);
            if (_tiles.TryGetValue(item.Id, out var t)) t.Update(item);
            else _tiles[item.Id] = t = new TileViewModel(item, _thumbs!);
            t.IsOnPc = IsOnPc(item);
        }
        foreach (var k in _tiles.Keys.Where(k => !live.Contains(k)).ToList()) _tiles.Remove(k);
        UpdateCounts();
        BuildRows();
        UpdateSelection();
    }

    private bool Passes(MediaItem i) => Filter switch
    {
        LibraryFilter.Photos => i.Kind is MediaKind.Photo or MediaKind.Live,
        LibraryFilter.Live => i.Kind == MediaKind.Live,
        LibraryFilter.Videos => i.Kind == MediaKind.Video,
        LibraryFilter.Screenshots => i.Kind == MediaKind.Screenshot,
        LibraryFilter.NotCopied => !_tiles[i.Id].IsOnPc,
        _ => true,
    };

    private void UpdateCounts()
    {
        CountAll = _items.Count;
        CountPhotos = _items.Count(i => i.Kind is MediaKind.Photo or MediaKind.Live);
        CountLive = _items.Count(i => i.Kind == MediaKind.Live);
        CountVideos = _items.Count(i => i.Kind == MediaKind.Video);
        CountScreenshots = _items.Count(i => i.Kind == MediaKind.Screenshot);
        CountNotCopied = _items.Count(i => _tiles.TryGetValue(i.Id, out var t) && !t.IsOnPc);
        UpdateSidebarSummary();
    }

    private void BuildRows()
    {
        UpdateTileWidth();
        var ordered = _items.Where(Passes);
        ordered = NewestFirst
            ? ordered.OrderByDescending(i => i.Date ?? DateTime.MinValue)
            : ordered.OrderBy(i => i.Date ?? DateTime.MaxValue);
        var list = ordered.Select(i => _tiles[i.Id]).ToList();
        _visibleOrder = list;

        var rows = new List<object>();
        int columns = Columns;
        foreach (var month in list.GroupBy(t => t.Item.Date is { } d ? new DateTime(d.Year, d.Month, 1) : DateTime.MinValue))
        {
            var tiles = month.ToList();
            var title = month.Key == DateTime.MinValue ? "Senza data"
                : It.TextInfo.ToTitleCase(month.Key.ToString("MMMM yyyy", It));
            int todo = tiles.Count(t => !t.IsOnPc);
            var subtitle = $"{tiles.Count.ToString("N0", It)} elementi" +
                           (todo == 0 ? " · tutto già sul PC" : todo < tiles.Count ? $" · {todo.ToString("N0", It)} da copiare" : "");
            rows.Add(new HeaderRow(title, subtitle, tiles));
            for (int i = 0; i < tiles.Count; i += columns)
                rows.Add(new TileRow(tiles.Skip(i).Take(columns).ToList()));
        }

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        LibrarySubtitle = $"{list.Count.ToString("N0", It)} elementi";
        OnPropertyChanged(nameof(IsFilteredEmpty));
    }

    public bool IsFilteredEmpty => Screen == AppScreen.Library && _visibleOrder.Count == 0;

    // ================================================================== selezione

    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _selectionText = "Nessun elemento selezionato";
    [ObservableProperty] private string _selectionDetail = "Clic per scegliere · Maiusc+clic per un intervallo · doppio clic per l'anteprima";

    public void ClickTile(TileViewModel tile, bool shift)
    {
        if (IsTransferring) { Focus(tile); return; }
        if (shift && _anchor is not null)
        {
            int a = _visibleOrder.IndexOf(_anchor), b = _visibleOrder.IndexOf(tile);
            if (a >= 0 && b >= 0)
            {
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) _visibleOrder[i].IsSelected = true;
            }
        }
        else
        {
            tile.IsSelected = !tile.IsSelected;
        }
        _anchor = tile;
        Focus(tile);
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectGroup(HeaderRow header)
    {
        if (IsTransferring) return;
        bool all = header.Tiles.All(t => t.IsSelected);
        foreach (var t in header.Tiles) t.IsSelected = !all;
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectAll()
    {
        if (IsTransferring) return;
        foreach (var t in _visibleOrder) t.IsSelected = true;
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectNotCopied()
    {
        if (IsTransferring) return;
        foreach (var t in _visibleOrder) t.IsSelected = !t.IsOnPc;
        UpdateSelection();
    }

    [RelayCommand]
    private void ClearSelection()
    {
        if (IsTransferring) return;
        foreach (var t in _tiles.Values) t.IsSelected = false;
        UpdateSelection();
    }

    private List<TileViewModel> Selected => _tiles.Values.Where(t => t.IsSelected).ToList();

    private void UpdateSelection()
    {
        var sel = Selected;
        SelectedCount = sel.Count;
        if (sel.Count == 0)
        {
            SelectionText = "Nessun elemento selezionato";
            SelectionDetail = "Clic per scegliere · Maiusc+clic per un intervallo · doppio clic per l'anteprima";
        }
        else
        {
            var bytes = sel.Sum(t => t.Item.FilesToCopy(Settings.IncludeEdited, Settings.IncludeSidecars).Sum(f => f.File.Size));
            int onPc = sel.Count(t => t.IsOnPc);
            SelectionText = $"{sel.Count.ToString("N0", It)} selezionati · {TransferEngine.Format(bytes)}";
            SelectionDetail = onPc == 0 ? $"{sel.Sum(t => t.Item.Files.Count).ToString("N0", It)} file sul telefono"
                : onPc == sel.Count ? "Tutti già sul PC: Copia li salta, Sposta li toglie dal telefono"
                : $"{onPc.ToString("N0", It)} già sul PC (verranno saltati)";
        }
        CopyCommand.NotifyCanExecuteChanged();
        MoveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    // ================================================================== pannello dettagli e anteprima

    [ObservableProperty] private TileViewModel? _focusedTile;
    [ObservableProperty] private ImageSource? _focusedImage;
    [ObservableProperty] private bool _focusedImageLoading;
    [ObservableProperty] private bool _showInspector;

    public bool ShowInspectorPanel => false;

    partial void OnShowInspectorChanged(bool value)
    {
        Settings.ShowInspector = value;
        SaveSettings();
        OnPropertyChanged(nameof(ShowInspectorPanel));
    }

    partial void OnFocusedTileChanged(TileViewModel? oldValue, TileViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsFocused = false;
        if (newValue is not null) newValue.IsFocused = true;
        RefreshFocused();
    }

    private void RefreshFocused()
    {
        OnPropertyChanged(nameof(FocusedDestination));
        OnPropertyChanged(nameof(FocusedCopyPath));
        OnPropertyChanged(nameof(FocusedOnPcText));
    }

    public string FocusedDestination => FocusedTile?.Item.Date is { } d && Settings.ByYearMonth
        ? Path.Combine(d.Year.ToString(CultureInfo.InvariantCulture), DestinationPlanner.MonthFolder(d.Month))
        : Settings.ByYearMonth ? "Senza data" : "(cartella principale)";

    public string? FocusedCopyPath => FocusedTile is { } t && _copies.TryGetValue(t.Item.Primary.Key, out var r) ? r.Destination : null;

    public string FocusedOnPcText => FocusedTile is { } t && _copies.TryGetValue(t.Item.Primary.Key, out var r)
        ? $"Sul PC dal {r.CopiedAt.ToLocalTime().ToString("d/M/yyyy", It)}" : "Non ancora copiato";

    private void Focus(TileViewModel tile)
    {
        FocusedTile = tile;
        _ = LoadFocusedImageAsync(tile);
    }

    /// <summary>Anteprima nel pannello: miniatura subito, poi (per le foto) l'immagine vera dopo una breve pausa.</summary>
    private async Task LoadFocusedImageAsync(TileViewModel tile)
    {
        _focusCts?.Cancel();
        var cts = _focusCts = new CancellationTokenSource();
        FocusedImage = tile.Thumbnail;
        if (!ShowInspector || tile.Item.Kind == MediaKind.Video || _previews is null || IsTransferring) return;
        try
        {
            await Task.Delay(350, cts.Token);
            FocusedImageLoading = true;
            var path = await _previews.GetFileAsync(tile.Item.Primary, cts.Token);
            var img = await Task.Run(() => PreviewService.Render(path, 900), cts.Token);
            if (!cts.IsCancellationRequested && img is not null) FocusedImage = img;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is DeviceException or IOException)
        {
            _log.Warn("Anteprima nel pannello: " + e.Message);
        }
        finally
        {
            if (_focusCts == cts) FocusedImageLoading = false;
        }
    }

    [ObservableProperty] private bool _previewOpen;
    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private bool _previewLoading;
    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string? _previewMessage;
    [ObservableProperty] private bool _previewIsVideo;
    private TileViewModel? _previewTile;
    private CancellationTokenSource? _previewCts;

    public void OpenPreview(TileViewModel tile)
    {
        if (_previews is null) return;
        Focus(tile);
        _previewTile = tile;
        PreviewOpen = true;
        _ = LoadPreviewAsync(tile);
    }

    private async Task LoadPreviewAsync(TileViewModel tile)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        PreviewTitle = $"{tile.Title} · {tile.DateText}";
        PreviewIsVideo = tile.IsVideo;
        PreviewMessage = null;
        PreviewImage = tile.Thumbnail;
        if (tile.IsVideo)
        {
            PreviewMessage = "I video si aprono con il lettore di Windows.";
            return;
        }
        PreviewLoading = true;
        try
        {
            var path = await _previews!.GetFileAsync(tile.Item.Primary, cts.Token);
            var img = await Task.Run(() => PreviewService.Render(path, 2400), cts.Token);
            if (cts.IsCancellationRequested) return;
            if (img is not null) PreviewImage = img;
            else PreviewMessage = "Questo formato non si riesce a mostrare: la copia sul PC sarà comunque intatta.";
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is DeviceException or IOException)
        {
            PreviewMessage = "Anteprima non disponibile: " + (e is DeviceException de ? DeviceErrors.Describe(de.Code) : e.Message);
        }
        finally
        {
            if (_previewCts == cts) PreviewLoading = false;
        }
    }

    [RelayCommand]
    private void OpenFocusedPreview()
    {
        if (FocusedTile is { } t) OpenPreview(t);
    }

    [RelayCommand]
    private void ClosePreview()
    {
        _previewCts?.Cancel();
        PreviewOpen = false;
        PreviewImage = null;
    }

    [RelayCommand]
    private void PreviewNext() => MovePreview(+1);

    [RelayCommand]
    private void PreviewPrevious() => MovePreview(-1);

    private void MovePreview(int delta)
    {
        if (_previewTile is null || !PreviewOpen) return;
        var i = _visibleOrder.IndexOf(_previewTile);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _visibleOrder.Count) return;
        _previewTile = _visibleOrder[j];
        Focus(_previewTile);
        _ = LoadPreviewAsync(_previewTile);
    }

    [RelayCommand]
    private async Task PlayVideoAsync()
    {
        var tile = _previewTile ?? FocusedTile;
        if (tile is null || _previews is null) return;
        PreviewLoading = true;
        PreviewMessage = "Preparo il video…";
        try
        {
            var path = await _previews.GetFileAsync(tile.Item.Primary, CancellationToken.None);
            PreviewMessage = Shell.Open(path) ? null : "Nessun programma per aprire questo video (serve l'estensione HEVC di Microsoft).";
        }
        catch (Exception e) when (e is DeviceException or IOException)
        {
            PreviewMessage = "Video non disponibile: " + e.Message;
        }
        finally
        {
            PreviewLoading = false;
        }
    }

    [RelayCommand]
    private void ShowFocusedInFolder()
    {
        if (FocusedCopyPath is { } p) Shell.ShowInFolder(p);
    }

    // ================================================================== destinazione

    public string DestinationText => Settings.ByYearMonth
        ? Path.Combine(Settings.DestinationRoot, "Anno", "Mese") : Settings.DestinationRoot;

    [RelayCommand]
    private void ChooseDestination()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Dove salvare foto e video dell'iPhone",
            InitialDirectory = Directory.Exists(Settings.DestinationRoot) ? Settings.DestinationRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog() != true) return;
        Settings.DestinationRoot = dlg.FolderName;
        Settings.RememberDestination(dlg.FolderName);
        SaveSettings();
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(DestinationRoot));
    }

    public string DestinationRoot => Settings.DestinationRoot;

    [RelayCommand]
    private void OpenDestination()
    {
        Directory.CreateDirectory(Settings.DestinationRoot);
        Shell.Open(Settings.DestinationRoot);
    }

    // ================================================================== copia e sposta

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand), nameof(MoveCommand), nameof(DeleteCommand))]
    private bool _isTransferring;

    [ObservableProperty] private bool _confirmMoveOpen;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmFiles = "";
    [ObservableProperty] private bool _deleteRefusedBefore;

    [ObservableProperty] private string _transferTitle = "";
    [ObservableProperty] private double _transferFraction;
    [ObservableProperty] private string _transferDetail = "";
    [ObservableProperty] private string _transferCurrent = "";
    [ObservableProperty] private string? _transferNotice;
    [ObservableProperty] private string _transferCounts = "";
    [ObservableProperty] private bool _isPaused;

    public string PauseLabel => IsPaused ? "Riprendi" : "Pausa";

    /// <summary>Consiglio sul Blocco automatico, quando non c'è un avviso più importante da mostrare.</summary>
    public bool ShowTransferTip => TransferNotice is null;

    partial void OnTransferNoticeChanged(string? value) => OnPropertyChanged(nameof(ShowTransferTip));

    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(PauseLabel));

    [ObservableProperty] private bool _resultOpen;
    [ObservableProperty] private string _resultTitle = "";
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private bool _resultHasProblems;
    private string? _resultFolder;

    partial void OnIsTransferringChanged(bool value)
    {
        OnPropertyChanged(nameof(DeviceStatus));
        OnPropertyChanged(nameof(ShowBottomBar));
        OnPropertyChanged(nameof(ShowActions));
    }

    private bool CanTransfer() => !IsTransferring && SelectedCount > 0 && Screen == AppScreen.Library;

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private Task CopyAsync() => RunTransferAsync(TransferMode.Copy);

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private Task MoveAsync()
    {
        if (!Settings.ConfirmMove) return RunTransferAsync(TransferMode.Move);
        var sel = Selected;
        var files = sel.Sum(t => t.Item.Files.Count);
        var bytes = sel.Sum(t => t.Item.TotalSize);
        ConfirmTitle = sel.Count == 1 ? "Spostare 1 elemento sul PC?" : $"Spostare {sel.Count.ToString("N0", It)} elementi sul PC?";
        ConfirmFiles = $"{files.ToString("N0", It)} file ({TransferEngine.Format(bytes)}) vengono copiati in {Settings.DestinationRoot} e verificati uno per uno.";
        DeleteRefusedBefore = Device is not null && Settings.DeleteRefusedDevices.Contains(Device.Key);
        ConfirmMoveOpen = true;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ConfirmMoveAsync()
    {
        ConfirmMoveOpen = false;
        return RunTransferAsync(TransferMode.Move);
    }

    [ObservableProperty] private bool _confirmDeleteOpen;
    [ObservableProperty] private string _confirmDeleteTitle = "";
    [ObservableProperty] private string _confirmDeleteText = "";
    [ObservableProperty] private string? _confirmDeleteNotOnPc;

    [RelayCommand(CanExecute = nameof(CanTransfer))]
    private void Delete()
    {
        var sel = Selected;
        if (sel.Count == 0) return;
        int notOnPc = sel.Count(t => !t.IsOnPc);
        ConfirmDeleteTitle = sel.Count == 1 ? "Eliminare 1 elemento dall'iPhone?" : $"Eliminare {sel.Count.ToString("N0", It)} elementi dall'iPhone?";
        ConfirmDeleteText = $"{sel.Sum(t => t.Item.Files.Count).ToString("N0", It)} file ({TransferEngine.Format(sel.Sum(t => t.Item.TotalSize))}) " +
                            "vengono tolti dal telefono, senza copiarli. Non è detto che passino da “Eliminati di recente”: considerala un'eliminazione definitiva.";
        ConfirmDeleteNotOnPc = notOnPc == 0 ? null
            : notOnPc == sel.Count ? (sel.Count == 1 ? "Questo elemento NON è sul PC: se lo elimini, lo perdi." : "Nessuno di questi è sul PC: se li elimini, li perdi.")
            : $"{notOnPc.ToString("N0", It)} di questi NON sono sul PC: se li elimini, li perdi. Per tenerli usa Sposta.";
        DeleteRefusedBefore = Device is not null && Settings.DeleteRefusedDevices.Contains(Device.Key);
        ConfirmDeleteOpen = true;
    }

    [RelayCommand]
    private Task ConfirmDeleteAsync()
    {
        ConfirmDeleteOpen = false;
        return RunTransferAsync(TransferMode.Delete);
    }

    [RelayCommand]
    private void CancelDialog()
    {
        ConfirmMoveOpen = false;
        ConfirmDeleteOpen = false;
        ResultOpen = false;
    }

    private async Task RunTransferAsync(TransferMode mode)
    {
        if (Device is null || IsTransferring) return;
        var items = Selected.Select(t => t.Item).OrderBy(i => i.Date ?? DateTime.MinValue).ToList();
        if (items.Count == 0) return;

        _transferMode = mode;
        IsTransferring = true;
        _transferCts = new CancellationTokenSource();
        _pause = new PauseController();
        IsPaused = false;
        var what = items.Count == 1 ? "1 elemento" : $"{items.Count.ToString("N0", It)} elementi";
        TransferTitle = mode switch
        {
            TransferMode.Delete => $"Elimino {what} dall'iPhone",
            TransferMode.Move => $"Sposto {what} in {Settings.DestinationRoot}",
            _ => $"Copio {what} in {Settings.DestinationRoot}",
        };
        TransferFraction = 0;
        TransferDetail = "Preparo…";
        TransferCurrent = "";
        TransferNotice = null;
        TransferCounts = "";
        if (Settings.KeepAwake) KeepAwake.Set(true);
        _log.Info(mode == TransferMode.Delete ? $"Delete: {items.Count} elementi" : $"{mode}: {items.Count} elementi verso {Settings.DestinationRoot}");

        // Un collegamento tutto suo: le miniature e le anteprime non rallentano la copia (e viceversa).
        // (Nella dimostrazione il telefono finto è uno solo e non regge due thread: si usa lo stesso collegamento.)
        IDeviceConnection conn = _demo
            ? _browse!
            : new WorkerConnection(new ProcessWorkerLauncher(Environment.ProcessPath!), log: _log.Info);
        TransferReport report;
        try
        {
            try
            {
                var opened = await conn.OpenAsync(Device.Id, _transferCts.Token);
                if (!opened.Ready) throw new DeviceException("L'iPhone è bloccato: sbloccalo e riprova", DeviceErrors.NotReady);
            }
            catch (OperationCanceledException)
            {
                return; // annullato mentre si apriva il telefono
            }
            catch (DeviceException e)
            {
                ShowResult(mode == TransferMode.Delete ? "Eliminazione non partita" : "Trasferimento non partito",
                    e.Kind == DeviceErrorKind.Gone ? "L'iPhone non è collegato." : "L'iPhone non risponde: sbloccalo e riprova.", problems: true, folder: null);
                return;
            }

            var engine = new TransferEngine(conn, Device, _registry, _converter, _log);
            var options = TransferOptions.From(Settings);
            var progress = new Progress<TransferStatus>(OnTransferStatus);
            try
            {
                var ct = _transferCts.Token;
                report = mode == TransferMode.Delete
                    ? await Task.Run(() => engine.DeleteAsync(items, options, progress, ct))
                    : await Task.Run(() => engine.RunAsync(items, mode, options, progress, _pause, ct));
            }
            catch (Exception e)
            {
                // Imprevisto: niente è stato tolto dal telefono (la cancellazione avviene solo alla fine, dopo le verifiche).
                _log.Error("Trasferimento", e);
                ReportProblem("errore imprevisto nel trasferimento");
                ShowResult("Trasferimento interrotto", "Errore imprevisto: " + e.Message +
                    "\n\nQuello già copiato resta sul PC. Il dettaglio è nel registro.", problems: true, folder: null);
                return;
            }
        }
        finally
        {
            if (!_demo) await conn.DisposeAsync();
            KeepAwake.Set(false);
            IsTransferring = false;
        }

        ApplyReport(report);
    }

    private TransferPhase _lastPhase;

    private void OnTransferStatus(TransferStatus s)
    {
        // L'iPhone si è bloccato (o scollegato): chi è lontano dal PC deve accorgersene.
        if (s.Phase == TransferPhase.WaitingForDevice && _lastPhase != TransferPhase.WaitingForDevice && !_demo)
            Attention.Request(Application.Current.MainWindow);
        _lastPhase = s.Phase;
        TransferFraction = s.Phase == TransferPhase.Deleting
            ? (s.ItemsTotal == 0 ? 1 : (double)s.ItemsDone / s.ItemsTotal)
            : s.BytesTotal > 0 ? Math.Clamp((double)s.BytesDone / s.BytesTotal, 0, 1) : (s.ItemsTotal == 0 ? 0 : (double)s.ItemsDone / s.ItemsTotal);
        TransferDetail = s.Phase switch
        {
            TransferPhase.Preparing => "Preparo…",
            TransferPhase.Deleting => $"Tolgo dal telefono: {s.ItemsDone.ToString("N0", It)} di {s.ItemsTotal.ToString("N0", It)}",
            TransferPhase.WaitingForDevice => "In pausa: aspetto l'iPhone sbloccato",
            _ => $"{s.ItemsDone.ToString("N0", It)} di {s.ItemsTotal.ToString("N0", It)} · {TransferEngine.Format(s.BytesDone)} di {TransferEngine.Format(s.BytesTotal)}"
                 + (s.BytesPerSecond > 1 ? $" · {TransferEngine.Format((long)s.BytesPerSecond)}/s" : "")
                 + (s.Remaining is { } r ? " · " + Remaining(r) : ""),
        };
        TransferCurrent = s.CurrentFile is null ? "" : s.CurrentFolder is null ? s.CurrentFile : $"{s.CurrentFile} → {s.CurrentFolder}";
        TransferNotice = s.Notice;
        var parts = new List<string>();
        if (s.Copied > 0) parts.Add($"✓ {s.Copied.ToString("N0", It)} copiati e verificati");
        if (s.Skipped > 0) parts.Add($"{s.Skipped.ToString("N0", It)} già sul PC");
        if (s.Retries > 0) parts.Add($"↻ {s.Retries} nuovi tentativi");
        if (s.Failed > 0) parts.Add($"✕ {s.Failed} non riusciti");
        TransferCounts = string.Join("   ", parts);
    }

    private static string Remaining(TimeSpan r) => r.TotalSeconds < 60 ? "meno di un minuto"
        : r.TotalMinutes < 60 ? $"circa {Math.Ceiling(r.TotalMinutes):0} min"
        : $"circa {(int)r.TotalHours} h {r.Minutes:00} min";

    [RelayCommand]
    private void CancelTransfer() => _transferCts?.Cancel();

    [RelayCommand]
    private void TogglePause()
    {
        if (_pause is null) return;
        if (_pause.IsPaused) _pause.Resume();
        else _pause.Pause();
        IsPaused = _pause.IsPaused;
    }

    private void ApplyReport(TransferReport r)
    {
        // Libreria: bollini "sul PC" ed elementi tolti dal telefono.
        if (Device is not null) _copies = _registry.Load(Device.Key);
        if (r.Removed.Count > 0)
        {
            var removedKeys = r.Removed.SelectMany(i => i.Files).Select(f => f.File.Key).ToHashSet();
            _files.RemoveAll(f => removedKeys.Contains(f.Key));
        }
        foreach (var item in r.Safe) if (_tiles.TryGetValue(item.Id, out var t)) t.IsSelected = false;
        Regroup();
        if (FocusedTile is { } focused && !_tiles.ContainsKey(focused.Item.Id)) FocusedTile = null; // tolto dal telefono
        RefreshFocused();

        if (Device is not null && r.Mode is TransferMode.Move or TransferMode.Delete)
        {
            if (r.DeleteRefused && !Settings.DeleteRefusedDevices.Contains(Device.Key)) Settings.DeleteRefusedDevices.Add(Device.Key);
            if (r.Deleted > 0)
            {
                Settings.DeleteRefusedDevices.Remove(Device.Key);
                if (!Settings.DeleteWorkedDevices.Contains(Device.Key)) Settings.DeleteWorkedDevices.Add(Device.Key);
            }
            SaveSettings();
        }

        var lines = new StringBuilder();
        if (r.Copied > 0) lines.AppendLine($"✓ {r.Copied.ToString("N0", It)} elementi copiati e verificati ({TransferEngine.Format(r.Bytes)}).");
        if (r.Skipped > 0) lines.AppendLine($"• {r.Skipped.ToString("N0", It)} erano già sul PC e non sono stati ricopiati.");
        if (r.Mode == TransferMode.Move)
        {
            if (r.Deleted > 0) lines.AppendLine($"✓ {r.Deleted.ToString("N0", It)} tolti dall'iPhone.");
            if (r.DeleteRefused)
                lines.AppendLine("• L'iPhone non permette di cancellare da Windows: le copie sul PC ci sono, sul telefono è rimasto tutto. " +
                                 "Puoi cancellarli dall'app Foto: nella libreria hanno il bollino ✓ PC.");
            else if (r.DeleteFailed > 0) lines.AppendLine($"• {r.DeleteFailed.ToString("N0", It)} non tolti dal telefono (le copie sul PC ci sono)" +
                                                          (r.DeleteProblem is { } why ? ": " + why + "." : "."));
        }
        if (r.Mode == TransferMode.Delete)
        {
            if (r.Deleted > 0) lines.AppendLine($"✓ {r.Deleted.ToString("N0", It)} elementi eliminati dall'iPhone.");
            if (r.DeleteRefused)
                lines.AppendLine("• L'iPhone non permette di eliminare da Windows: sul telefono è rimasto tutto. Puoi eliminarli dall'app Foto.");
            else if (r.DeleteFailed > 0) lines.AppendLine($"• {r.DeleteFailed.ToString("N0", It)} non eliminati, sono ancora sul telefono" +
                                                          (r.DeleteProblem is { } why ? ": " + why + "." : "."));
            if (r.Canceled) lines.AppendLine("Eliminazione annullata: quelli non ancora eliminati sono rimasti sul telefono.");
            var deleteTitle = r.Canceled ? "Eliminazione annullata"
                : r.DeleteRefused ? "L'iPhone non ha permesso di eliminare"
                : r.DeleteFailed > 0 ? "Fatto, con qualche problema"
                : "Eliminati";
            ShowResult(deleteTitle, lines.ToString().TrimEnd(), problems: r.DeleteRefused || r.DeleteFailed > 0, folder: null);
            return;
        }

        foreach (var f in r.Failed.Take(8)) lines.AppendLine($"✕ {f.Item.BaseName}: {f.Reason}");
        if (r.Failed.Count > 8) lines.AppendLine($"✕ … e altri {r.Failed.Count - 8} (dettagli nel registro).");
        if (r.ConversionWarnings.Count > 0) lines.AppendLine($"• {r.ConversionWarnings.Count} foto non convertite in JPG: copiato l'originale.");
        if (r.Canceled) lines.AppendLine("Trasferimento annullato: quello già copiato resta sul PC, dal telefono non è stato tolto niente.");
        if (r.AbortReason is not null) lines.AppendLine("Trasferimento interrotto: " + r.AbortReason + ".");

        var title = r.Canceled ? "Trasferimento annullato"
            : r.AbortReason is not null ? "Trasferimento interrotto"
            : r.Failed.Count > 0 ? "Fatto, con qualche problema"
            : r.Mode == TransferMode.Move && r.DeleteRefused ? "Copiato, ma non tolto dal telefono"
            : "Fatto";
        var folder = r.Folders.Count == 1 ? r.Folders.First() : Settings.DestinationRoot;
        ShowResult(title, lines.ToString().TrimEnd(), problems: r.Failed.Count > 0 || r.AbortReason is not null || r.DeleteRefused, folder);
    }

    private void ShowResult(string title, string text, bool problems, string? folder)
    {
        if (problems && ReportProblem($"esito: {title}"))
            text += "\n\nIl rapporto è stato inviato in automatico per l'assistenza.";
        ResultTitle = title;
        ResultText = text;
        ResultHasProblems = problems;
        _resultFolder = folder;
        ResultOpen = true;
    }

    [RelayCommand]
    private void OpenResultFolder()
    {
        ResultOpen = false;
        Shell.Open(_resultFolder ?? Settings.DestinationRoot);
    }

    // ================================================================== impostazioni

    [RelayCommand]
    private void OpenSettings()
    {
        RefreshDiagnostics();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    public bool ByYearMonth { get => Settings.ByYearMonth; set { Settings.ByYearMonth = value; SettingChanged(); } }
    public bool SkipAlreadyCopied { get => Settings.SkipAlreadyCopied; set { Settings.SkipAlreadyCopied = value; SettingChanged(); } }
    public bool ConvertHeic { get => Settings.ConvertHeicToJpeg; set { Settings.ConvertHeicToJpeg = value; SettingChanged(); } }
    public bool KeepHeicOriginal { get => Settings.KeepHeicOriginal; set { Settings.KeepHeicOriginal = value; SettingChanged(); } }
    public bool IncludeEdited { get => Settings.IncludeEdited; set { Settings.IncludeEdited = value; SettingChanged(); } }
    public bool IncludeSidecars { get => Settings.IncludeSidecars; set { Settings.IncludeSidecars = value; SettingChanged(); } }
    public bool ConfirmMove { get => Settings.ConfirmMove; set { Settings.ConfirmMove = value; SettingChanged(); } }
    public bool KeepAwakeSetting { get => Settings.KeepAwake; set { Settings.KeepAwake = value; SettingChanged(); } }
    public bool AutoUpdate { get => Settings.AutoUpdate; set { Settings.AutoUpdate = value; SettingChanged(); } }

    private void SettingChanged()
    {
        SaveSettings();
        OnPropertyChanged(string.Empty);
        foreach (var t in _tiles.Values) t.IsOnPc = IsOnPc(t.Item);
        UpdateCounts();
        UpdateSelection();
    }

    private void SaveSettings()
    {
        try { Settings.Save(AppPaths.Settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log.Error("Salvataggio impostazioni", e); }
    }

    [ObservableProperty] private string _sidebarSummary = "";

    private void UpdateSidebarSummary()
    {
        if (Device is null) { SidebarSummary = ""; return; }
        var (count, last) = _registry.Summary(Device.Key);
        SidebarSummary = count == 0 ? "Nessuna copia ancora fatta da questo iPhone"
            : $"{count.ToString("N0", It)} file copiati finora" +
              (last is { } l ? $"\nUltima copia: {l.ToLocalTime().ToString("d MMMM, HH:mm", It)}" : "");
    }

    // ================================================================== diagnostica

    [ObservableProperty] private string _diagnostics = "";

    public string VersionText => $"Versione {UpdateService.Current}";

    private void RefreshDiagnostics()
    {
        var sb = new StringBuilder();
        void Line(string label, string value) => sb.AppendLine($"{label,-26} {value}");
        Line("Versione", UpdateService.Current.ToString());
        Line("Windows", Environment.OSVersion.VersionString);
        Line("Driver Apple", DriverInstalled ? "trovato" : "NON trovato");
        Line("iPhone", Device is null ? "nessuno" : $"{Device.DisplayName} · {Device.Model} · iOS {Device.Firmware}");
        Line("Stato", DeviceStatus);
        if (Device is not null)
        {
            Line("Elementi / file", $"{_items.Count} / {_files.Count}");
            if (EnumerationSeconds > 0)
                Line("Lettura dell'elenco", $"{EnumerationSeconds:0.0} s ({(_files.Count / Math.Max(0.1, EnumerationSeconds)):0} file/s)");
            if (_thumbs is not null)
                Line("Miniature", $"{_thumbs.Fetched} dal telefono, {_thumbs.MissingCount} senza miniatura, {_thumbs.Errors} errori");
            var videos = _items.Where(i => i.Kind == MediaKind.Video).ToList();
            var canDelete = _files.Count(f => f.CanDelete);
            Line("Cancellabili (WPD)", $"{canDelete} di {_files.Count}");
            Line("Cancellazione provata", Settings.DeleteWorkedDevices.Contains(Device.Key) ? "funziona"
                : Settings.DeleteRefusedDevices.Contains(Device.Key) ? "RIFIUTATA dall'iPhone" : "non ancora");
        }
        if (_browse is WorkerConnection w) Line("Riavvii del collegamento", w.Restarts.ToString(CultureInfo.InvariantCulture));
        foreach (var n in _openNotes) Line("Telefono", n);
        Line("Cartella dati", AppPaths.Root);
        Diagnostics = sb.ToString().TrimEnd();
    }

    [RelayCommand]
    private void SaveReport()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"Ponte iPhone - rapporto {DateTime.Now:yyyy-MM-dd HHmm}.txt");
        try
        {
            File.WriteAllText(path, BuildReport());
            Shell.ShowInFolder(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowBanner("Rapporto non salvato: " + e.Message, error: true);
        }
    }

    /// <summary>Diagnostica e ultime 400 righe del registro di oggi: lo stesso testo del rapporto salvato o inviato.</summary>
    private string BuildReport()
    {
        RefreshDiagnostics();
        var sb = new StringBuilder(Diagnostics).AppendLine().AppendLine();
        try
        {
            if (File.Exists(_log.TodayPath))
            {
                sb.AppendLine("--- Registro di oggi (ultime 400 righe) ---");
                foreach (var l in File.ReadAllLines(_log.TodayPath).TakeLast(400)) sb.AppendLine(l);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            sb.AppendLine("Registro non leggibile: " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>Invia il rapporto in automatico (non nella dimostrazione). True se è partito.</summary>
    public bool ReportProblem(string reason)
    {
        if (_demo) return false;
        _log.Warn("Problema da segnalare: " + reason);
        return _reporter.Report(reason, BuildReport);
    }

    [RelayCommand]
    private void OpenLogFolder() => Shell.Open(_log.Folder);

    [RelayCommand]
    private void InstallDriver() => AppleDriver.OpenStore();

    // ================================================================== aggiornamenti e installazione

    [ObservableProperty] private string? _updateText;
    [ObservableProperty] private bool _updateBusy;
    [ObservableProperty] private string _updateStatus = "";

    public bool CanSelfInstall => !_demo && !SelfInstall.IsInstalled;
    [ObservableProperty] private bool _installBannerDismissed;

    public bool ShowInstallBanner => CanSelfInstall && !InstallBannerDismissed;

    partial void OnInstallBannerDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowInstallBanner));

    private async Task CheckUpdatesAsync(bool automatic)
    {
        if (automatic && !Settings.AutoUpdate) return;
        try
        {
            if (automatic) await Task.Delay(TimeSpan.FromSeconds(8));
            UpdateStatus = "Controllo…";
            _release = await _updates.CheckAsync(CancellationToken.None);
            UpdateText = null; // niente pulsanti da premere: si installa da sola alla chiusura
            if (_release is not null)
            {
                _preparedUpdate = await _updates.PrepareAsync(_release, CancellationToken.None);
                UpdateStatus = $"La {_release.Version} si installa da sola quando chiudi l'app";
            }
            else
            {
                UpdateStatus = $"Versione aggiornata ({UpdateService.Current})";
            }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            UpdateStatus = "Controllo non riuscito: " + e.Message;
            if (!automatic) _log.Warn("Controllo aggiornamenti: " + e.Message);
        }
    }

    [RelayCommand]
    private Task CheckUpdatesNowAsync() => CheckUpdatesAsync(automatic: false);

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_release is null || IsTransferring) return;
        UpdateBusy = true;
        try
        {
            if (await _updates.InstallAsync(_release, CancellationToken.None)) Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            _log.Error("Aggiornamento", e);
            ShowBanner("Aggiornamento non riuscito: " + e.Message, error: true);
        }
        finally
        {
            UpdateBusy = false;
        }
    }

    [RelayCommand]
    private void SelfInstallNow()
    {
        try
        {
            if (SelfInstall.Install(desktopShortcut: true)) Application.Current.Shutdown();
        }
        catch (Exception e)
        {
            _log.Error("Installazione sul PC", e);
            ShowBanner("Installazione non riuscita: " + e.Message, error: true);
        }
    }

    [RelayCommand]
    private void DismissInstallBanner() => InstallBannerDismissed = true;

    [RelayCommand]
    private void DismissBanner() => BannerText = null;

    private void ShowBanner(string? text, bool error = false)
    {
        BannerText = text;
        BannerIsError = error;
    }

    // ================================================================== chiusura

    public bool ConfirmClose()
    {
        if (!IsTransferring) return true;
        var answer = MessageBox.Show("È in corso un trasferimento. Interromperlo e chiudere?\n\nQuello già copiato resta sul PC; dal telefono non verrà tolto altro.",
            "Ponte iPhone", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        _transferCts?.Cancel();
        return true;
    }

    private string? _preparedUpdate;

    public async Task ShutdownAsync()
    {
        // Prima di tutto (la finestra si sta chiudendo): l'installazione aspetta che questo processo finisca.
        if (_preparedUpdate is not null && _release is not null && !_demo)
        {
            try { _updates.Launch(_preparedUpdate, _release, silent: true); }
            catch (Exception e) { _log.Error("Aggiornamento alla chiusura", e); }
        }
        _poll.Stop();
        _thumbs?.Dispose();
        if (_browse is not null) await _browse.DisposeAsync();
        _registry.Dispose();
    }

    // ================================================================== schermate della CI

    internal void DemoSelect(int count)
    {
        foreach (var t in _visibleOrder.Take(count)) t.IsSelected = true;
        if (_visibleOrder.Count > 2) Focus(_visibleOrder[2]);
        UpdateSelection();
    }

    internal void DemoShowTransfer()
    {
        IsTransferring = true;
        TransferTitle = "Sposto 124 elementi in " + Settings.DestinationRoot;
        OnTransferStatus(new TransferStatus(TransferPhase.Copying, 72, 124, 2_576_980_377, 4_402_341_478, 37_748_736,
            TimeSpan.FromSeconds(48), "IMG_4821.MOV", Path.Combine("2026", "10-Ottobre"), 69, 3, 0, 1,
            null));
    }

    internal void DemoEndTransfer() => IsTransferring = false;

    internal void DemoShowNoDevice()
    {
        CloseLibrary();
        Screen = AppScreen.NoDevice;
    }
}
