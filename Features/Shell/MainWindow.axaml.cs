using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;

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

    /// <summary>
    /// Ссылка на TrayIcon, определённый в App.axaml через TrayIcon.Icons.
    /// Avalonia управляет его lifecycle — мы только переключаем IsVisible и Menu.
    /// </summary>
    private TrayIcon? _trayIcon;

    /// <summary>
    /// True = пользователь выбрал "Выход" из трея или подтвердил закрытие.
    /// Предотвращает рекурсивный перехват OnClosing.
    /// </summary>
    private bool _forceClose;

    // Ссылки на пункты меню для обновления текста и состояния
    private NativeMenuItem? _playPauseItem;
    private NativeMenuItem? _nextItem;
    private NativeMenuItem? _prevItem;
    private NativeMenuItem? _repeatItem;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _queueItem;
    private NativeMenuItem? _cleanMemItem;
    private NativeMenuItem? _exitItem;

    /// <summary>
    /// Кэш состояния HasTrack для обновления enabled пунктов меню трея.
    /// </summary>
    private bool _lastHasTrack;

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

    /// <summary>
    /// Обрабатывает нажатие кнопки минимизации.
    /// Если MinimizeToTray=true — сворачивает в трей вместо панели задач.
    /// </summary>
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

    /// <summary>
    /// Находит TrayIcon из App.axaml TrayIcon.Icons и настраивает контекстное меню.
    /// 
    /// <para><b>Почему через App.axaml:</b></para>
    /// <para>TrayIcon созданный через <c>new TrayIcon()</c> в code-behind ненадёжно
    /// подхватывает иконку на всех платформах. TrayIcon.Icons в XAML — рекомендованный
    /// подход Avalonia, который корректно работает с WindowIcon на Windows, Linux и macOS.</para>
    /// 
    /// <para><b>Меню:</b></para>
    /// <list type="bullet">
    ///   <item>📌 Show — восстановить окно</item>
    ///   <item>─────────</item>
    ///   <item>▶ Play / ⏸ Pause — управление воспроизведением (disabled если нет трека)</item>
    ///   <item>⏭ Next (disabled если нет трека)</item>
    ///   <item>⏮ Previous (disabled если нет трека)</item>
    ///   <item>🔁 Repeat — циклическое переключение (disabled если нет трека)</item>
    ///   <item>─────────</item>
    ///   <item>📋 Queue — перейти к очереди</item>
    ///   <item>🧹 Clear Memory — очистить кэши + GC</item>
    ///   <item>─────────</item>
    ///   <item>❌ Exit — выход</item>
    /// </list>
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            // Находим TrayIcon определённый в App.axaml
            var icons = TrayIcon.GetIcons(Application.Current!);
            if (icons == null || icons.Count == 0)
            {
                Log.Warn("[Tray] No TrayIcon defined in App.axaml TrayIcon.Icons");
                return;
            }

            _trayIcon = icons[0];

            // Загружаем иконку программно (App.axaml не может использовать avares для Icon)
            LoadTrayIconImage();

            var L = LocalizationService.Instance;

            // ═══ Контекстное меню ═══
            var menu = new NativeMenu();

            _showItem = new NativeMenuItem($"📌  {L["Tray_Show"] ?? "Show"}");
            _showItem.Click += (_, _) => RestoreFromTray();
            menu.Add(_showItem);

            menu.Add(new NativeMenuItemSeparator());

            // Управление воспроизведением — по умолчанию disabled
            _playPauseItem = new NativeMenuItem($"▶  {L["Tray_Play"] ?? "Play"}");
            _playPauseItem.IsEnabled = false;
            _playPauseItem.Click += (_, _) => OnTrayPlayPause();
            menu.Add(_playPauseItem);

            _nextItem = new NativeMenuItem($"⏭  {L["Tray_Next"] ?? "Next"}");
            _nextItem.IsEnabled = false;
            _nextItem.Click += (_, _) => OnTrayNext();
            menu.Add(_nextItem);

            _prevItem = new NativeMenuItem($"⏮  {L["Tray_Previous"] ?? "Previous"}");
            _prevItem.IsEnabled = false;
            _prevItem.Click += (_, _) => OnTrayPrevious();
            menu.Add(_prevItem);

            _repeatItem = new NativeMenuItem($"🔁  {L["Tray_Repeat"] ?? "Repeat"}");
            _repeatItem.IsEnabled = false;
            _repeatItem.Click += (_, _) => OnTrayRepeat();
            menu.Add(_repeatItem);

            menu.Add(new NativeMenuItemSeparator());

            _queueItem = new NativeMenuItem($"📋  {L["Tray_Queue"] ?? "Queue"}");
            _queueItem.Click += (_, _) => OnTrayGoToQueue();
            menu.Add(_queueItem);

            _cleanMemItem = new NativeMenuItem($"🧹  {L["Tray_ClearMemory"] ?? "Clear Memory"}");
            _cleanMemItem.Click += (_, _) => OnTrayClearMemory();
            menu.Add(_cleanMemItem);

            menu.Add(new NativeMenuItemSeparator());

            _exitItem = new NativeMenuItem($"❌  {L["Tray_Exit"] ?? "Exit"}");
            _exitItem.Click += (_, _) =>
            {
                _forceClose = true;
                Close();
            };
            menu.Add(_exitItem);

            _trayIcon.Menu = menu;

            // Клик по иконке — восстановить окно
            _trayIcon.Clicked += (_, _) => RestoreFromTray();

            // Подписка на смену языка
            LocalizationService.Instance.LanguageChanged += OnTrayLanguageChanged;

            // Подписка на состояние воспроизведения
            SubscribeToPlaybackState();

            Log.Info("[Tray] Tray icon configured successfully");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Tray] Failed to setup tray icon: {ex.Message}");
            _trayIcon = null;
        }
    }

    /// <summary>
    /// Загружает иконку для TrayIcon из ресурсов приложения.
    /// Пробует несколько форматов в порядке приоритета.
    /// </summary>
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

        // Fallback: использовать иконку самого окна
        if (Icon != null)
        {
            _trayIcon.Icon = Icon;
            Log.Debug("[Tray] Using window icon as fallback");
        }
        else
        {
            Log.Warn("[Tray] No icon loaded — tray will use system default");
        }
    }

    #endregion

    #region Tray Menu Actions

    private void OnTrayPlayPause()
    {
        try
        {
            var audio = Program.Services.GetRequiredService<AudioEngine>();
            _ = audio.SetPlaybackStateAsync(!audio.IsPlaying);
        }
        catch (Exception ex)
        {
            Log.Error($"[Tray] PlayPause error: {ex.Message}");
        }
    }

    private void OnTrayNext()
    {
        try
        {
            var audio = Program.Services.GetRequiredService<AudioEngine>();
            _ = audio.PlayNextAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[Tray] Next error: {ex.Message}");
        }
    }

    private void OnTrayPrevious()
    {
        try
        {
            var audio = Program.Services.GetRequiredService<AudioEngine>();
            _ = audio.PlayPreviousAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"[Tray] Previous error: {ex.Message}");
        }
    }

    private void OnTrayRepeat()
    {
        try
        {
            var audio = Program.Services.GetRequiredService<AudioEngine>();
            audio.RepeatMode = audio.RepeatMode switch
            {
                RepeatMode.None => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                RepeatMode.One => RepeatMode.None,
                _ => RepeatMode.None
            };
            UpdateRepeatMenuText();
        }
        catch (Exception ex)
        {
            Log.Error($"[Tray] Repeat error: {ex.Message}");
        }
    }

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
        PerformCleanup();
    }

    #endregion

    #region Tray State Updates

    /// <summary>
    /// Подписывается на AudioEngine для обновления:
    /// - Play/Pause текст
    /// - Tooltip (название трека)
    /// - Enabled состояние пунктов управления (HasTrack)
    /// </summary>
    private void SubscribeToPlaybackState()
    {
        try
        {
            var audio = Program.Services.GetRequiredService<AudioEngine>();

            // Playback state → Play/Pause текст
            audio.OnPlaybackStateChanged += (isPlaying, _) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdatePlayPauseMenuText(isPlaying));
            };

            // Track changed → tooltip + enabled
            audio.OnTrackChanged += track =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    bool hasTrack = track != null;

                    if (_trayIcon != null)
                    {
                        _trayIcon.ToolTipText = track != null
                            ? $"{track.Title} — {track.Author}"
                            : "Lite Music Player";
                    }

                    // Обновляем enabled состояние пунктов управления
                    if (hasTrack != _lastHasTrack)
                    {
                        _lastHasTrack = hasTrack;
                        UpdatePlaybackItemsEnabled(hasTrack);
                    }
                });
            };
        }
        catch (Exception ex)
        {
            Log.Debug($"[Tray] Could not subscribe to playback state: {ex.Message}");
        }
    }

    /// <summary>
    /// Включает/выключает пункты меню управления воспроизведением.
    /// </summary>
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
            ? $"⏸  {L["Tray_Pause"] ?? "Pause"}"
            : $"▶  {L["Tray_Play"] ?? "Play"}";
    }

    private void UpdateRepeatMenuText()
    {
        if (_repeatItem == null) return;

        try
        {
            var audio = Program.Services.GetRequiredService<AudioEngine>();
            var L = LocalizationService.Instance;

            _repeatItem.Header = audio.RepeatMode switch
            {
                RepeatMode.None => $"🔁  {L["Tray_Repeat"] ?? "Repeat"}",
                RepeatMode.All => $"🔁  ✓ {L["Tray_RepeatAll"] ?? "Repeat All"}",
                RepeatMode.One => $"🔂  ✓ {L["Tray_RepeatOne"] ?? "Repeat One"}",
                _ => $"🔁  {L["Tray_Repeat"] ?? "Repeat"}"
            };
        }
        catch { }
    }

    private void OnTrayLanguageChanged(object? sender, string e)
    {
        var L = LocalizationService.Instance;

        if (_showItem != null) _showItem.Header = $"📌  {L["Tray_Show"] ?? "Show"}";
        if (_nextItem != null) _nextItem.Header = $"⏭  {L["Tray_Next"] ?? "Next"}";
        if (_prevItem != null) _prevItem.Header = $"⏮  {L["Tray_Previous"] ?? "Previous"}";
        if (_queueItem != null) _queueItem.Header = $"📋  {L["Tray_Queue"] ?? "Queue"}";
        if (_cleanMemItem != null) _cleanMemItem.Header = $"🧹  {L["Tray_ClearMemory"] ?? "Clear Memory"}";
        if (_exitItem != null) _exitItem.Header = $"❌  {L["Tray_Exit"] ?? "Exit"}";

        // Play/Pause и Repeat зависят от состояния
        try
        {
            var audio = Program.Services.GetRequiredService<AudioEngine>();
            UpdatePlayPauseMenuText(audio.IsPlaying);
            UpdateRepeatMenuText();
        }
        catch { }

        // Обновляем tooltip
        if (_trayIcon != null)
        {
            try
            {
                var audio = Program.Services.GetRequiredService<AudioEngine>();
                _trayIcon.ToolTipText = audio.CurrentTrack != null
                    ? $"{audio.CurrentTrack.Title} — {audio.CurrentTrack.Author}"
                    : "Lite Music Player";
            }
            catch
            {
                _trayIcon.ToolTipText = "Lite Music Player";
            }
        }
    }

    #endregion

    #region Tray Show/Hide

    /// <summary>
    /// Скрывает окно и показывает иконку в трее.
    /// </summary>
    private void MinimizeToTray()
    {
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = true;
        }

        Hide();

        // Broadcast suspend для экономии ресурсов
        _isMinimized = true;
        ViewModelBase.BroadcastSuspend();
        MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(30));
        ScheduleCleanup(TimeSpan.FromSeconds(5));

        Log.Info("[Window] Minimized to tray");
    }

    /// <summary>
    /// Восстанавливает окно из трея.
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

        // Resume
        if (_isMinimized)
        {
            _isMinimized = false;
            CancelCleanup();
            MemoryDiagnostics.Instance.SetMonitoringInterval(TimeSpan.FromSeconds(5));
            ViewModelBase.BroadcastResume();
        }

        Log.Info("[Window] Restored from tray");
    }

    private void DisposeTrayIcon()
    {
        LocalizationService.Instance.LanguageChanged -= OnTrayLanguageChanged;

        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            // Не вызываем Dispose — TrayIcon управляется через App.axaml TrayIcons
            _trayIcon = null;
        }
    }

    #endregion

    #region Close Handling

    /// <summary>
    /// Перехватывает закрытие окна и применяет настройку CloseAction.
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            // При реальном закрытии — скрываем TrayIcon
            if (_trayIcon != null)
                _trayIcon.IsVisible = false;

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
                    _trayIcon.IsVisible = false;
                base.OnClosing(e);
                break;
        }
    }

    /// <summary>
    /// Показывает диалог "Что сделать при закрытии?" через универсальный ChoiceDialog.
    /// </summary>
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

    /// <summary>
    /// Обрабатывает смену состояния окна.
    /// 
    /// <para><b>FIX Maximize:</b> При SystemDecorations="BorderOnly" + ExtendClientAreaToDecorationsHint
    /// Avalonia не учитывает WorkingArea. Вручную выставляем Margin на корневой Grid.</para>
    /// 
    /// <para><b>Minimize to Tray:</b> Если MinimizeToTray=true, при сворачивании через
    /// системную кнопку (не нашу) или Alt+F9 — перехватываем и уходим в трей.</para>
    /// </summary>
    private void HandleWindowStateChanged(WindowState state)
    {
        // Maximize fix
        UpdateMaximizePadding(state);

        // Maximize/Restore icon toggle
        var maximizeIcon = this.FindControl<Border>("MaximizeIcon");
        var restoreIcon = this.FindControl<Grid>("RestoreIcon");
        if (maximizeIcon != null) maximizeIcon.IsVisible = state != WindowState.Maximized;
        if (restoreIcon != null) restoreIcon.IsVisible = state == WindowState.Maximized;

        // Minimize
        if (state == WindowState.Minimized)
        {
            // Проверяем MinimizeToTray для системного сворачивания
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
            ViewModelBase.BroadcastResume();
        }
    }

    /// <summary>
    /// При максимизации добавляет Margin к корневому Grid равный размеру
    /// области занятой панелью задач. Предотвращает перекрытие панели задач.
    /// </summary>
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
                PerformCleanup();
            }
            catch (OperationCanceledException) { }
        });
    }

    private static void PerformCleanup()
    {
        Log.Info("[Memory] Cleanup");

        _ = Task.Run(() =>
        {
            try
            {
                var imageCache = Program.Services.GetRequiredService<ImageCacheService>();
                imageCache.ClearMemoryCache();

                var vmFactory = Program.Services.GetRequiredService<TrackViewModelFactory>();
                vmFactory.CleanupCache();

                var registry = Program.Services.GetRequiredService<TrackRegistry>();
                registry.CleanupDeadReferences();

                System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                    System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

                MemoryHelpers.TrimWorkingSet();

                var afterMb = GC.GetTotalMemory(false) / 1024 / 1024;
                Log.Info($"[Memory] After cleanup: {afterMb} MB");
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