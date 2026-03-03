using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using LMP.Features.Player;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace LMP.Features.Shell;

public partial class MainWindow : Window
{
    #region Fields

    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;

    private CancellationTokenSource? _cleanupCts;

    private volatile bool _isMinimized;
    private DateTime _lastCleanupTime = DateTime.MinValue;
    private const int MinCleanupIntervalMs = 30_000;

    private TrayIcon? _trayIcon;
    private bool _forceClose;

    private NativeMenuItem? _playPauseItem;
    private NativeMenuItem? _nextItem;
    private NativeMenuItem? _prevItem;
    private NativeMenuItem? _repeatItem;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _queueItem;
    private NativeMenuItem? _cleanMemItem;
    private NativeMenuItem? _exitItem;

    private PlayerControlService? _playerControl;
    
    /// <summary>
    /// Подписки на PlayerControlService (работают независимо от suspend).
    /// </summary>
    private CompositeDisposable? _traySubscriptions;

    #endregion

    #region Constructor & Init

    public MainWindow()
    {
        InitializeComponent();

        PropertyChanged += MainWindow_PropertyChanged;
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _minimizeButton = this.FindControl<Button>("MinimizeButton");
        _maximizeButton = this.FindControl<Button>("MaximizeButton");
        _closeButton = this.FindControl<Button>("CloseButton");
        _dragArea = this.FindControl<Border>("DragArea");

        _minimizeButton?.Click += (_, _) => HandleMinimizeClick();
        _maximizeButton?.Click += (_, _) => ToggleMaximize();
        _closeButton?.Click += (_, _) => Close();

        var titleBar = this.FindControl<Grid>("TitleBar");
        titleBar?.PointerPressed += OnTitleBarPointerPressed;
        _dragArea?.DoubleTapped += (_, _) => ToggleMaximize();

        SetupTrayIcon();
    }

    #endregion

    #region Minimize Handling

    private void HandleMinimizeClick()
    {
        try
        {
            var library = Program.Services.GetRequiredService<LibraryService>();
            if (library.Settings.MinimizeToTray)
            {
                MinimizeToTray();
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[Window] MinimizeToTray check failed: {ex.Message}");
        }

        WindowState = WindowState.Minimized;
    }

    #endregion

    #region Tray Icon

    private void SetupTrayIcon()
    {
        try
        {
            _playerControl = Program.Services.GetRequiredService<PlayerControlService>();
            _traySubscriptions = new CompositeDisposable();

            var icons = TrayIcon.GetIcons(Application.Current!);
            if (icons == null || icons.Count == 0)
            {
                Log.Warn("[Tray] No TrayIcon defined in App.axaml TrayIcon.Icons");
                return;
            }

            _trayIcon = icons[0];
            LoadTrayIconImage();

            var L = LocalizationService.Instance;

            var menu = new NativeMenu();

            _showItem = new NativeMenuItem($"●  {L["Tray_Show"] ?? "Show"}");
            _showItem.Click += (_, _) => RestoreFromTray();
            menu.Add(_showItem);

            menu.Add(new NativeMenuItemSeparator());

            _playPauseItem = new NativeMenuItem($"►  {L["Tray_Play"] ?? "Play"}");
            _playPauseItem.IsEnabled = false;
            _playPauseItem.Click += (_, _) => _ = _playerControl?.PlayPauseAsync();
            menu.Add(_playPauseItem);

            _nextItem = new NativeMenuItem($"»  {L["Tray_Next"] ?? "Next"}");
            _nextItem.IsEnabled = false;
            _nextItem.Click += (_, _) => _ = _playerControl?.NextAsync();
            menu.Add(_nextItem);

            _prevItem = new NativeMenuItem($"«  {L["Tray_Previous"] ?? "Previous"}");
            _prevItem.IsEnabled = false;
            _prevItem.Click += (_, _) => _ = _playerControl?.PreviousAsync();
            menu.Add(_prevItem);

            _repeatItem = new NativeMenuItem($"↻  {L["Tray_Repeat"] ?? "Repeat"}");
            _repeatItem.IsEnabled = false;
            _repeatItem.Click += (_, _) => _playerControl?.ToggleRepeat();
            menu.Add(_repeatItem);

            menu.Add(new NativeMenuItemSeparator());

            _queueItem = new NativeMenuItem($"≡  {L["Tray_Queue"] ?? "Queue"}");
            _queueItem.Click += (_, _) => OnTrayGoToQueue();
            menu.Add(_queueItem);

            _cleanMemItem = new NativeMenuItem($"⟳  {L["Tray_ClearMemory"] ?? "Clear Memory"}");
            _cleanMemItem.Click += (_, _) => OnTrayClearMemory();
            menu.Add(_cleanMemItem);

            menu.Add(new NativeMenuItemSeparator());

            _exitItem = new NativeMenuItem($"×  {L["Tray_Exit"] ?? "Exit"}");
            _exitItem.Click += (_, _) =>
            {
                _forceClose = true;
                Close();
            };
            menu.Add(_exitItem);

            _trayIcon.Menu = menu;
            _trayIcon.Clicked += (_, _) => RestoreFromTray();

            LocalizationService.Instance.LanguageChanged += OnTrayLanguageChanged;

            // ═══ РЕАКТИВНЫЕ ПОДПИСКИ (работают ВСЕГДА, даже в tray) ═══
            SubscribeToPlayerControl();

            Log.Info("[Tray] Tray icon configured successfully");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Tray] Failed to setup tray icon: {ex.Message}");
            _trayIcon = null;
        }
    }

    /// <summary>
    /// Подписывается на реактивные потоки PlayerControlService.
    /// Эти подписки работают НЕЗАВИСИМО от suspend состояния окна.
    /// </summary>
    private void SubscribeToPlayerControl()
    {
        if (_playerControl == null || _traySubscriptions == null) return;

        // IsPlaying → обновляем текст Play/Pause
        _playerControl.IsPlayingObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(isPlaying => UpdatePlayPauseMenuText(isPlaying))
            .DisposeWith(_traySubscriptions);

        // CurrentTrack → обновляем tooltip и enabled состояние
        _playerControl.CurrentTrackObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track =>
            {
                bool hasTrack = track != null;

                if (_trayIcon != null)
                {
                    _trayIcon.ToolTipText = track != null
                        ? $"{track.Title} — {track.Author}"
                        : "Lite Music Player";
                }

                UpdatePlaybackItemsEnabled(hasTrack);
            })
            .DisposeWith(_traySubscriptions);

        // RepeatMode → обновляем текст Repeat
        _playerControl.RepeatModeObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateRepeatMenuText())
            .DisposeWith(_traySubscriptions);

        Log.Debug("[Tray] Subscribed to PlayerControlService observables");
    }

    private void LoadTrayIconImage()
    {
        if (_trayIcon == null) return;

        string[] iconPaths =
        [
            "avares://LMP/Assets/icon.ico",
            "avares://LMP/Assets/icon.png",
            "avares://LMP/Assets/logo.png",
            "avares://LMP/Assets/logo.ico"
        ];

        foreach (var path in iconPaths)
        {
            try
            {
                var uri = new Uri(path);
                if (!Avalonia.Platform.AssetLoader.Exists(uri)) continue;

                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                _trayIcon.Icon = new WindowIcon(stream);
                Log.Debug($"[Tray] Icon loaded from: {path}");
                return;
            }
            catch (Exception ex)
            {
                Log.Debug($"[Tray] Failed to load icon from {path}: {ex.Message}");
            }
        }

        if (Icon != null)
        {
            _trayIcon.Icon = Icon;
            Log.Debug("[Tray] Using window icon as fallback");
        }
    }

    #endregion

    #region Tray Menu Actions

    private void OnTrayGoToQueue()
    {
        RestoreFromTray();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.NavigateCommand.Execute("Queue").Subscribe();
            }
        });
    }

    private static void OnTrayClearMemory()
    {
        Log.Info("[Tray] Manual memory cleanup requested");
        PerformCleanup(aggressive: true);
    }

    #endregion

    #region Tray State Updates

    private void UpdatePlaybackItemsEnabled(bool enabled)
    {
        if (_playPauseItem != null) _playPauseItem.IsEnabled = enabled;
        if (_nextItem != null) _nextItem.IsEnabled = enabled;
        if (_prevItem != null) _prevItem.IsEnabled = enabled;
        if (_repeatItem != null) _repeatItem.IsEnabled = enabled;
    }

    private void UpdatePlayPauseMenuText(bool isPlaying)
    {
        if (_playPauseItem == null) return;
        var L = LocalizationService.Instance;
        
        _playPauseItem.Header = isPlaying
            ? $"‖  {L["Tray_Pause"] ?? "Pause"}"
            : $"►  {L["Tray_Play"] ?? "Play"}";
    }

    private void UpdateRepeatMenuText()
    {
        if (_repeatItem == null || _playerControl == null) return;

        var L = LocalizationService.Instance;
        var repeatMode = _playerControl.RepeatMode;

        _repeatItem.Header = repeatMode switch
        {
            RepeatMode.None => $"↻  {L["Tray_Repeat"] ?? "Repeat"}",
            RepeatMode.All => $"↻• {L["Tray_RepeatAll"] ?? "All"}",
            RepeatMode.One => $"↺• {L["Tray_RepeatOne"] ?? "One"}",
            _ => $"↻  {L["Tray_Repeat"] ?? "Repeat"}"
        };
    }

    private void OnTrayLanguageChanged(object? sender, string e)
    {
        var L = LocalizationService.Instance;

        if (_showItem != null) _showItem.Header = $"●  {L["Tray_Show"] ?? "Show"}";
        if (_nextItem != null) _nextItem.Header = $"»  {L["Tray_Next"] ?? "Next"}";
        if (_prevItem != null) _prevItem.Header = $"«  {L["Tray_Previous"] ?? "Previous"}";
        if (_queueItem != null) _queueItem.Header = $"≡  {L["Tray_Queue"] ?? "Queue"}";
        if (_cleanMemItem != null) _cleanMemItem.Header = $"⟳  {L["Tray_ClearMemory"] ?? "Clear Memory"}";
        if (_exitItem != null) _exitItem.Header = $"×  {L["Tray_Exit"] ?? "Exit"}";

        if (_playerControl != null)
        {
            UpdatePlayPauseMenuText(_playerControl.IsPlaying);
            UpdateRepeatMenuText();
        }

        if (_trayIcon != null && _playerControl != null)
        {
            var track = _playerControl.CurrentTrack;
            _trayIcon.ToolTipText = track != null
                ? $"{track.Title} — {track.Author}"
                : "Lite Music Player";
        }
    }

    #endregion

    #region Tray Show/Hide

    private void MinimizeToTray()
    {
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = true;
        }

        Hide();

        _isMinimized = true;
        ViewModelBase.BroadcastSuspend();
        MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(30));
        ScheduleCleanup(TimeSpan.FromSeconds(5));

        Log.Info("[Window] Minimized to tray");
    }

    /// <summary>
    /// Восстанавливает окно из трея.
    /// Вызывает ForceSync для синхронизации состояния PlayerBar с текущим состоянием воспроизведения.
    /// </summary>
    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();

        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
        }

        if (_isMinimized)
        {
            _isMinimized = false;
            CancelCleanup();
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(5));
            
            // ═══ КРИТИЧНО: Принудительная синхронизация перед Resume ═══
            _playerControl?.ForceSync();
            
            ViewModelBase.BroadcastResume();
        }

        Log.Info("[Window] Restored from tray");
    }

    private void DisposeTrayIcon()
    {
        LocalizationService.Instance.LanguageChanged -= OnTrayLanguageChanged;
        
        _traySubscriptions?.Dispose();
        _traySubscriptions = null;
        
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
        }
        
        _trayIcon = null;
        _playerControl = null;
    }

    #endregion

    #region Close Handling

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            if (_trayIcon != null)
            {
                _trayIcon.IsVisible = false;
            }

            base.OnClosing(e);
            return;
        }

        var library = Program.Services.GetRequiredService<LibraryService>();
        var closeAction = library.Settings.CloseAction;

        switch (closeAction)
        {
            case CloseAction.MinimizeToTray:
                e.Cancel = true;
                MinimizeToTray();
                break;

            case CloseAction.Ask:
                e.Cancel = true;
                await HandleAskCloseAsync();
                break;

            case CloseAction.Exit:
            default:
                if (_trayIcon != null)
                {
                    _trayIcon.IsVisible = false;
                }
                base.OnClosing(e);
                break;
        }
    }

    private async Task HandleAskCloseAsync()
    {
        var dialog = Program.Services.GetRequiredService<DialogService>();

        var result = await dialog.ShowCloseActionDialogAsync();

        if (result == null) return;

        if (result.IsChecked)
        {
            var library = Program.Services.GetRequiredService<LibraryService>();
            library.UpdateSettings(s => s.CloseAction = result.Value);
        }

        if (result.Value == CloseAction.MinimizeToTray)
        {
            MinimizeToTray();
        }
        else
        {
            _forceClose = true;
            Close();
        }
    }

    #endregion

    #region Window State & Maximize Fix

    private void MainWindow_PropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;

        var state = (WindowState)e.NewValue!;
        HandleWindowStateChanged(state);
    }

    private void HandleWindowStateChanged(WindowState state)
    {
        UpdateMaximizePadding(state);

        var maximizeIcon = this.FindControl<Border>("MaximizeIcon");
        var restoreIcon = this.FindControl<Grid>("RestoreIcon");
        if (maximizeIcon != null) maximizeIcon.IsVisible = state != WindowState.Maximized;
        if (restoreIcon != null) restoreIcon.IsVisible = state == WindowState.Maximized;

        if (state == WindowState.Minimized)
        {
            try
            {
                var library = Program.Services.GetRequiredService<LibraryService>();
                if (library.Settings.MinimizeToTray)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        WindowState = WindowState.Normal;
                        MinimizeToTray();
                    });
                    return;
                }
            }
            catch { }

            _isMinimized = true;
            ViewModelBase.BroadcastSuspend();
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(30));
            ScheduleCleanup(TimeSpan.FromSeconds(2));
        }
        else if (_isMinimized)
        {
            _isMinimized = false;
            CancelCleanup();
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(5));
            
            // ═══ Синхронизация при разворачивании ═══
            _playerControl?.ForceSync();
            
            ViewModelBase.BroadcastResume();
        }
    }

    private void UpdateMaximizePadding(WindowState state)
    {
        var rootGrid = this.Content as Avalonia.Controls.Grid;
        if (rootGrid == null) return;

        if (state == WindowState.Maximized)
        {
            var screen = Screens.ScreenFromWindow(this);
            if (screen != null)
            {
                var scaling = screen.Scaling;
                var bounds = screen.Bounds;
                var workArea = screen.WorkingArea;

                double left = (workArea.X - bounds.X) / scaling;
                double top = (workArea.Y - bounds.Y) / scaling;
                double right = (bounds.Right - workArea.Right) / scaling;
                double bottom = (bounds.Bottom - workArea.Bottom) / scaling;

                rootGrid.Margin = new Thickness(
                    Math.Max(0, left),
                    Math.Max(0, top),
                    Math.Max(0, right),
                    Math.Max(0, bottom));
            }
        }
        else
        {
            rootGrid.Margin = new Thickness(0);
        }
    }

    #endregion

    #region Window Deactivation & Cleanup

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        ScheduleCleanup(TimeSpan.FromMinutes(2));
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        CancelCleanup();
    }

    private void ScheduleCleanup(TimeSpan delay)
    {
        CancelCleanup();
        _cleanupCts = new CancellationTokenSource();
        var token = _cleanupCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, token);
                if (token.IsCancellationRequested) return;

                var now = DateTime.UtcNow;
                if ((now - _lastCleanupTime).TotalMilliseconds < MinCleanupIntervalMs)
                    return;

                _lastCleanupTime = now;
                PerformCleanup(aggressive: false);
            }
            catch (OperationCanceledException) { }
        });
    }

    private static void PerformCleanup(bool aggressive = false)
    {
        _ = Task.Run(() =>
        {
            try
            {
                bool isPlaying;
                try
                {
                    var playerControl = Program.Services.GetRequiredService<PlayerControlService>();
                    isPlaying = playerControl.IsPlaying || playerControl.IsLoading;
                }
                catch
                {
                    isPlaying = false;
                }

                try
                {
                    var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
                    imageCache.ClearMemoryCache();

                    var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();
                    vmFactory.CleanupCache();

                    var registry = Program.Services.GetRequiredService<TrackRegistry>();
                    registry.CleanupDeadReferences();
                }
                catch (Exception ex)
                {
                    Log.Warn($"[Memory] Cache cleanup error: {ex.Message}");
                }

                if (isPlaying)
                {
                    GC.Collect(1, GCCollectionMode.Optimized, blocking: false);
                    Log.Info("[Memory] Soft cleanup (playback active)");
                }
                else if (aggressive)
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    MemoryHelpers.TrimWorkingSet();
                    Log.Info($"[Memory] Aggressive cleanup: {GC.GetTotalMemory(false) / 1024 / 1024} MB");
                }
                else
                {
                    GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
                    Log.Info($"[Memory] Normal cleanup: {GC.GetTotalMemory(false) / 1024 / 1024} MB");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[Memory] Cleanup error: {ex.Message}");
            }
        });
    }

    private void CancelCleanup()
    {
        if (_cleanupCts != null)
        {
            _cleanupCts.Cancel();
            _cleanupCts.Dispose();
            _cleanupCts = null;
        }
    }

    #endregion

    #region Title Bar Drag & Maximize

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;

        if (e.Source is Avalonia.Visual visual)
        {
            var parent = visual;
            while (parent != null)
            {
                if (parent is Button) return;
                parent = parent.GetVisualParent();
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    #endregion

    #region Dispose

    protected override void OnClosed(EventArgs e)
    {
        CancelCleanup();
        DisposeTrayIcon();
        PropertyChanged -= MainWindow_PropertyChanged;
        Deactivated -= OnWindowDeactivated;
        Activated -= OnWindowActivated;
        base.OnClosed(e);
    }

    #endregion
}