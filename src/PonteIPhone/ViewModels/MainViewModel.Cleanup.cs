using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PonteIPhone.Core.Device;
using PonteIPhone.Core.Model;
using PonteIPhone.Core.Transfer;
using PonteIPhone.Services;

namespace PonteIPhone.ViewModels;

/// <summary>
/// Pagina "Fai spazio" (richiesta del 17/10): cosa si può togliere dall'iPhone senza perdere niente.
/// Già sul PC e doppioni si tolgono con un pulsante; video grandi e schermate si mostrano nella griglia e sceglie l'utente.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Da quanto un video conta come "grande".</summary>
    private const long BigVideoBytes = 100L * 1024 * 1024;

    [ObservableProperty] private bool _isCleanupOpen;

    partial void OnIsCleanupOpenChanged(bool value)
    {
        if (value)
        {
            IsSettingsOpen = false;
            IsStorageOpen = false;
        }
        RaiseScreen();
    }

    [ObservableProperty] private string _cleanupTotal = "";
    [ObservableProperty] private string _cleanupAfter = "";
    [ObservableProperty] private string _cleanupBadge = "";

    [ObservableProperty] private string _cleanOnPcText = "";
    [ObservableProperty] private string _cleanOnPcSize = "";
    [ObservableProperty] private bool _hasCleanOnPc;

    [ObservableProperty] private string _cleanVideosText = "";
    [ObservableProperty] private string _cleanVideosSize = "";
    [ObservableProperty] private bool _hasCleanVideos;

    [ObservableProperty] private string _cleanScreensText = "";
    [ObservableProperty] private string _cleanScreensSize = "";
    [ObservableProperty] private bool _hasCleanScreens;

    [ObservableProperty] private string _dupText = "Controllo il contenuto dei file che potrebbero essere uguali: ci vuole qualche minuto.";
    [ObservableProperty] private string _dupSize = "";
    [ObservableProperty] private bool _dupChecking;
    [ObservableProperty] private bool _dupChecked;
    [ObservableProperty] private bool _hasDups;
    [ObservableProperty] private double _dupProgress;
    [ObservableProperty] private bool _dupKeepNewest;

    public ObservableCollection<CleanupSlice> CleanupSlices { get; } = [];
    [ObservableProperty] private string _cleanupAppsText = "";

    private List<DuplicateGroup> _dupGroups = [];
    private string? _dupGroupsFor;
    /// <summary>SHA-256 già calcolati in questa sessione (chiave del file → impronta).</summary>
    private readonly Dictionary<string, string> _hashes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _dupCts;

    public bool ShowCleanupStart => !DupChecking && !DupChecked;

    partial void OnDupCheckingChanged(bool value) => OnPropertyChanged(nameof(ShowCleanupStart));
    partial void OnDupCheckedChanged(bool value) => OnPropertyChanged(nameof(ShowCleanupStart));

    partial void OnDupKeepNewestChanged(bool value)
    {
        if (_dupGroups.Count == 0) return;
        // Stessi gruppi, cambia solo chi resta.
        _dupGroups = DuplicateFinder.Confirm(_dupGroups.Select(g => g.Copies.Prepend(g.Keep).ToList()),
            f => _hashes.TryGetValue(f.Key, out var h) ? h : null, value);
        ApplyDupRoles();
        if (Filter == LibraryFilter.Duplicates) BuildRows();
        RecomputeCleanup();
    }

    [RelayCommand]
    private void OpenCleanup()
    {
        if (!HasDevice) return;
        IsCleanupOpen = true;
        RecomputeCleanup();
    }

    private IEnumerable<TileViewModel> OnPcTiles => _tiles.Values.Where(t => t.IsOnPc);
    private IEnumerable<TileViewModel> BigVideoTiles => _tiles.Values.Where(t => t.Item.Kind == MediaKind.Video && t.Item.TotalSize >= BigVideoBytes);
    private IEnumerable<TileViewModel> ScreenTiles => _tiles.Values.Where(t => t.Item.Kind == MediaKind.Screenshot);
    private IEnumerable<TileViewModel> DupCopyTiles => _tiles.Values.Where(t => t.DupRole == DupRole.Copy);

    /// <summary>Rifà i conti della pagina (dopo un elenco, una copia, un'eliminazione).</summary>
    private void RecomputeCleanup()
    {
        string N(int n) => n.ToString("N0", It);
        var onPc = OnPcTiles.ToList();
        var videos = BigVideoTiles.ToList();
        var screens = ScreenTiles.ToList();
        // Un elemento conta una volta sola anche se sta in due voci (un video grande già sul PC).
        var counted = new HashSet<string>(StringComparer.Ordinal);
        long Sum(IEnumerable<TileViewModel> tiles) => tiles.Where(t => counted.Add(t.Item.Id)).Sum(t => t.Item.TotalSize);
        long sOnPc = Sum(onPc), sDups = Sum(DupCopyTiles), sVideos = Sum(videos), sScreens = Sum(screens);

        HasCleanOnPc = onPc.Count > 0;
        CleanOnPcSize = TransferEngine.Format(onPc.Sum(t => t.Item.TotalSize));
        CleanOnPcText = onPc.Count == 0 ? "Niente: quando copi o sposti, qui compaiono le foto già al sicuro sul PC."
            : $"{N(onPc.Count)} foto e video con il bollino ✓ PC";

        HasCleanVideos = videos.Count > 0;
        CleanVideosSize = TransferEngine.Format(videos.Sum(t => t.Item.TotalSize));
        CleanVideosText = videos.Count == 0 ? "Nessun video sopra i 100 MB." : $"{N(videos.Count)} video sopra i 100 MB";

        HasCleanScreens = screens.Count > 0;
        CleanScreensSize = TransferEngine.Format(screens.Sum(t => t.Item.TotalSize));
        CleanScreensText = screens.Count == 0 ? "Nessuna schermata." : $"{N(screens.Count)} schermate";

        int copies = _dupGroups.Sum(g => g.Copies.Count);
        HasDups = copies > 0;
        DupSize = DupChecked ? TransferEngine.Format(_dupGroups.Sum(g => g.Saving)) : "";
        if (DupChecked)
            DupText = copies == 0 ? "Nessun doppione: ogni foto e video c'è una volta sola."
                : $"{N(_dupGroups.Count)} gruppi · {N(copies)} copie in più";

        long total = sOnPc + sDups + sVideos + sScreens;
        CleanupTotal = TransferEngine.Format(total);
        CleanupBadge = total > 0 ? TransferEngine.Format(total) : "";
        CleanupAfter = Storage is { } st && st.Capacity > 0
            ? $"Lo spazio libero passerebbe da {TransferEngine.Format(st.Free)} a {TransferEngine.Format(st.Free + total)}"
            : "";

        CleanupSlices.Clear();
        void Slice(string name, long bytes, Brush fill) { if (bytes > 0) CleanupSlices.Add(new CleanupSlice(name, bytes, fill)); }
        Slice("Già sul PC", sOnPc, CleanupSlice.Green);
        Slice("Doppioni", sDups, CleanupSlice.Amber);
        Slice("Video grandi", sVideos, CleanupSlice.Purple);
        Slice("Schermate", sScreens, CleanupSlice.Blue);

        CleanupAppsText = Breakdown is { Apps.Count: > 0 } b
            ? "Le più pesanti: " + string.Join(", ", b.Apps.OrderByDescending(a => a.Total).Take(3).Select(a => $"{a.Name} ({TransferEngine.Format(a.Total)})")) + "."
            : "Le app più pesanti sono nella pagina Memoria.";
    }

    /// <summary>"Guarda": la griglia mostra solo quella voce, così si sceglie a mano.</summary>
    [RelayCommand]
    private void CleanupShow(string which)
    {
        if (!Enum.TryParse<LibraryFilter>(which, out var f)) return;
        IsCleanupOpen = false;
        if (Filter == f) BuildRows();
        else Filter = f;
        ClearSelection();
    }

    /// <summary>Le foto già sul PC: si mostrano, si selezionano tutte e si chiede conferma (la conferma ricontrolla il disco).</summary>
    [RelayCommand]
    private void CleanupDeleteOnPc()
    {
        CleanupShow(nameof(LibraryFilter.OnPc));
        foreach (var t in _visibleOrder) t.IsSelected = true;
        UpdateSelection();
        if (DeleteCommand.CanExecute(null)) DeleteCommand.Execute(null);
    }

    /// <summary>I doppioni: si mostrano a gruppi con le copie già selezionate (quella che resta no).</summary>
    [RelayCommand]
    private void CleanupReviewDups()
    {
        CleanupShow(nameof(LibraryFilter.Duplicates));
        foreach (var t in _visibleOrder) t.IsSelected = t.DupRole == DupRole.Copy;
        UpdateSelection();
    }

    [RelayCommand]
    private void CleanupDeleteDups()
    {
        CleanupReviewDups();
        if (DeleteCommand.CanExecute(null)) DeleteCommand.Execute(null);
    }

    /// <summary>
    /// Legge (scaricandoli in una cartella temporanea) solo i file che potrebbero essere uguali e ne calcola lo SHA-256.
    /// Su un collegamento a parte, come la memoria: le miniature non si fermano.
    /// </summary>
    [RelayCommand]
    private async Task CheckDuplicatesAsync()
    {
        if (Device is not { } dev || DupChecking) return;
        var candidates = DuplicateFinder.Candidates(_items);
        var files = DuplicateFinder.FilesToHash(candidates).Where(f => !_hashes.ContainsKey(f.Key))
            .GroupBy(f => f.Key).Select(g => g.First()).ToList();
        DupChecking = true;
        DupProgress = 0;
        DupText = files.Count == 0 ? "Confronto…" : $"Leggo {files.Count.ToString("N0", It)} file che potrebbero essere uguali…";
        _dupCts?.Dispose();
        _dupCts = new CancellationTokenSource();
        var ct = _dupCts.Token;
        var temp = Path.Combine(Path.GetTempPath(), "PonteIPhone-doppioni");
        IDeviceConnection? conn = null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        int failed = 0;
        try
        {
            if (files.Count > 0)
            {
                Directory.CreateDirectory(temp);
                conn = _demo ? _browse! : new WorkerConnection(new ProcessWorkerLauncher(Environment.ProcessPath!), log: _log.Info);
                var opened = await conn.OpenAsync(dev.Id, ct);
                if (!opened.Ready) throw new DeviceException("L'iPhone è bloccato: sbloccalo e riprova", DeviceErrors.NotReady);
                long totalBytes = Math.Max(1, files.Sum(f => f.Size)), doneBytes = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = files[i];
                    var path = Path.Combine(temp, $"{i}.part");
                    try
                    {
                        var r = await conn.DownloadAsync(f.ObjectId, path, null, ct);
                        if (r.Bytes == f.Size) _hashes[f.Key] = r.Sha256;
                        else failed++;
                    }
                    catch (DeviceException e) when (e.Kind != DeviceErrorKind.Gone)
                    {
                        failed++; // quel file resta fuori: nel dubbio non è un doppione
                    }
                    finally
                    {
                        try { File.Delete(path); } catch (IOException) { }
                    }
                    doneBytes += f.Size;
                    DupProgress = (double)doneBytes / totalBytes;
                    DupText = $"Letti {(i + 1).ToString("N0", It)} di {files.Count.ToString("N0", It)} file…";
                }
            }
            _dupGroups = DuplicateFinder.Confirm(candidates, f => _hashes.TryGetValue(f.Key, out var h) ? h : null, DupKeepNewest);
            _dupGroupsFor = dev.Key;
            DupChecked = true;
            ApplyDupRoles();
            _log.Info($"Doppioni: {candidates.Count} gruppi candidati, {files.Count} file letti in {clock.Elapsed.TotalSeconds:0} s" +
                      $"{(failed > 0 ? $" ({failed} non letti)" : "")}; {_dupGroups.Count} gruppi confermati, {_dupGroups.Sum(g => g.Copies.Count)} copie");
        }
        catch (OperationCanceledException)
        {
            DupText = "Controllo interrotto.";
        }
        catch (DeviceException e)
        {
            _log.Warn("Doppioni: " + e.Message);
            DupText = e.Kind == DeviceErrorKind.Gone ? "L'iPhone si è scollegato: ricollegalo e riprova."
                : e.Code == DeviceErrors.NotReady ? "L'iPhone è bloccato: sbloccalo e riprova."
                : "Il telefono non ha risposto: riprova.";
        }
        finally
        {
            if (conn is not null && !_demo) await conn.DisposeAsync();
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
            DupChecking = false;
            RecomputeCleanup();
        }
    }

    [RelayCommand]
    private void CancelDuplicates() => _dupCts?.Cancel();

    /// <summary>Segna sulle miniature chi resta e chi è una copia (solo per il telefono su cui si è controllato).</summary>
    private void ApplyDupRoles()
    {
        foreach (var t in _tiles.Values) t.DupRole = DupRole.None;
        if (_dupGroupsFor != Device?.Key) return;
        foreach (var g in _dupGroups)
        {
            if (_tiles.TryGetValue(g.Keep.Id, out var keep)) keep.DupRole = DupRole.Keep;
            foreach (var c in g.Copies)
                if (_tiles.TryGetValue(c.Id, out var copy)) copy.DupRole = DupRole.Copy;
        }
    }

    /// <summary>Dopo un nuovo elenco i gruppi valgono solo per gli elementi ancora sul telefono, con gli stessi file.</summary>
    private void RefreshDupsAfterList()
    {
        if (_dupGroups.Count == 0) { ApplyDupRoles(); return; }
        var present = _items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var still = _dupGroups.Select(g => g.Copies.Prepend(g.Keep).Where(i => present.ContainsKey(i.Id)).Select(i => present[i.Id]).ToList())
            .Where(l => l.Count > 1);
        _dupGroups = DuplicateFinder.Confirm(still, f => _hashes.TryGetValue(f.Key, out var h) ? h : null, DupKeepNewest);
        ApplyDupRoles();
    }

    /// <summary>Gruppo di doppioni di una miniatura, per l'intestazione nella griglia.</summary>
    private DuplicateGroup? DupGroupOf(TileViewModel t) =>
        _dupGroups.FirstOrDefault(g => g.Keep.Id == t.Item.Id || g.Copies.Any(c => c.Id == t.Item.Id));

    /// <summary>Una copia di un doppione è al sicuro se quella che resta non è tra quelle da eliminare.</summary>
    private bool IsSafeDuplicateCopy(TileViewModel t, HashSet<string> selectedIds) =>
        t.DupRole == DupRole.Copy && DupGroupOf(t) is { } g && !selectedIds.Contains(g.Keep.Id);
}

/// <summary>Una voce della barra di Fai spazio.</summary>
public sealed class CleanupSlice(string name, long bytes, Brush fill)
{
    public string Name => name;
    public double Weight => bytes;
    public Brush Fill => fill;
    public string Tooltip => $"{name}: {TransferEngine.Format(bytes)}";

    public static readonly Brush Green = Make(0x4C, 0xC3, 0x8A);
    public static readonly Brush Amber = Make(0xE0, 0xA4, 0x4A);
    public static readonly Brush Purple = Make(0x9B, 0x51, 0xE0);
    public static readonly Brush Blue = Make(0x6A, 0xA7, 0xE8);

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
