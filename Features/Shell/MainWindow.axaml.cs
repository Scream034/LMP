using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using LMP.Core.Helpers;
using LMP.Core.Models;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace LMP.Features.Shell;

/// <summary>
/// Главное окно приложения. Управляет жизненным циклом, состоянием окна (Tray/Minimize),
/// и системными ресурсами в зависимости от активности пользователя.
/// 
/// <para><b>Tray:</b> Иконка ВСЕГДА видна в системном трее (и на Windows, и на Linux/macOS).
/// Скрывается только при полном закрытии приложения.</para>
/// 
/// <para><b>Tooltip формат:</b> <c>{AppName}: {TrackTitle} ({Volume}{VolumeEmoji})</c>
/// через <see cref="TrayTooltipHelper"/> (DRY).</para>
/// </summary>
public partial class MainWindow : Window
{
    #region Fields

    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;

    private CancellationTokenSource? _cleanupCts;
    private CancellationTokenSource? _deactivateCts;

    /// <summary>Окно свернуто в системный трей (максимальная экономия ресурсов).</summary>
    private volatile bool _isInTray;

    /// <summary>Окно свернуто в панель задач.</summary>
    private volatile bool _isMinimized;

    /// <summary>Окно потеряло фокус (находится на фоне или перекрыто).</summary>
    private volatile bool _isDeactivated;

    private DateTime _lastCleanupTime = DateTime.MinValue;
    private const int MinCleanupIntervalMs = 30_000;

    /// <summary>Задержка перед переходом в режим Soft Suspend при потере фокуса.</summary>
    private const int DeactivateSuspendDelayMs = 500;

    /// <summary>Задержка перед восстановлением tooltip после временного показа громкости (мс).</summary>
    private const int TooltipRestoreDelayMs = 3000;

    /// <summary>
    /// Минимальный интервал между toggle-ами окна из трея (мс).
    /// На Windows debounce выполняется в <see cref="TrayManager.TryInvokeToggle"/>.
    /// На non-Windows — в <see cref="ToggleTrayWindow"/>.
    /// </summary>
    public const int ToggleCooldownMs = 1000;

    private bool _forceClose;

    private PlayerControlService? _playerControl;
    private LibraryService? _library;

    /// <summary>Подписки на PlayerControlService (работают всегда, даже в трее).</summary>
    private CompositeDisposable? _traySubscriptions;

#if WINDOWS
    /// <summary>Нативный менеджер трея (lazy mouse hook для скролла громкости).</summary>
    private TrayManager? _trayManager;

    /// <summary>
    /// Таймер восстановления tooltip после показа громкости (debounce).
    /// Заменяет CancellationTokenSource + Task.Delay — без TaskCanceledException спама.
    /// </summary>
    private Avalonia.Threading.DispatcherTimer? _tooltipRestoreTimer;
#else
    /// <summary>Стандартный менеджер трея от Avalonia для Linux/macOS.</summary>
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _playPauseItem;
    private NativeMenuItem? _nextItem;
    private NativeMenuItem? _prevItem;
    private NativeMenuItem? _repeatItem;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _queueItem;
    private NativeMenuItem? _cleanMemItem;
    private NativeMenuItem? _exitItem;
#endif

    private readonly Canvas? _copyHintCanvas;
    private readonly Border? _copyHintOverlay;
    private readonly TextBlock? _copyHintText;
    private readonly PathIcon? _copyHintIcon;
    private CancellationTokenSource? _copyHintCts;

    private const int CopyHintDurationMs = 1800;
    private const int CopyHintFadeDurationMs = 150;

    #endregion

    #region Constructor & Init

    public MainWindow()
    {
        InitializeComponent();

        _copyHintCanvas = this.FindControl<Canvas>("CopyHintCanvas");
        _copyHintOverlay = this.FindControl<Border>("CopyHintOverlay");
        _copyHintText = this.FindControl<TextBlock>("CopyHintText");
        _copyHintIcon = this.FindControl<PathIcon>("CopyHintIcon");

        // Трекинг позиции курсора для позиционирования toast
        var rootGrid = this.FindControl<Grid>("RootGrid") ?? Content as Grid;
        if (rootGrid != null)
            MousePositionHelper.Attach(rootGrid);

        PropertyChanged += MainWindow_PropertyChanged;
        Deactivated += OnWindowDeactivated;
        Activated += OnWindowActivated;

        CopyHintService.Instance.HintRequested += OnCopyHintRequested;
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

        try
        {
            _library = Program.Services.GetRequiredService<LibraryService>();
        }
        catch (Exception ex)
        {
            Log.Warn($"[Window] LibraryService not available: {ex.Message}");
        }

        SetupTrayIcon();
    }

    #endregion

    #region Suspend Level Management

    /// <summary>
    /// Определяет текущий уровень приостановки (Suspend) на основе состояния окна.
    /// </summary>
    private SuspendLevel DetermineSuspendLevel()
    {
        if (_isInTray) return SuspendLevel.Hard;
        if (_isMinimized) return SuspendLevel.Soft;
        if (_isDeactivated) return SuspendLevel.Soft;
        return SuspendLevel.None;
    }

    /// <summary>
    /// Проверяет настройку пользователя на предмет необходимости оптимизации при неактивности.
    /// </summary>
    private bool ShouldOptimizeWhenInactive()
    {
        try
        {
            return _library?.Settings.OptimizeWhenInactive ?? true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Применяет текущий уровень suspend. Управляет частотой мониторинга памяти,
    /// очисткой мусора (GC) и оповещает ViewModelBase о необходимости отключить тяжелые UI-компоненты.
    /// </summary>
    private void ApplySuspendLevel()
    {
        var level = DetermineSuspendLevel();
        bool forceOptimize = level == SuspendLevel.Hard || ShouldOptimizeWhenInactive();

        ViewModelBase.BroadcastSuspendLevel(level, forceOptimize);

        var monitoringInterval = level switch
        {
            SuspendLevel.Hard => TimeSpan.FromSeconds(30),
            SuspendLevel.Soft when forceOptimize => TimeSpan.FromSeconds(15),
            _ => TimeSpan.FromSeconds(5)
        };
        MemoryDiagnostics.Instance.SetMonitoringInterval(monitoringInterval);

        if (level != SuspendLevel.None && forceOptimize)
        {
            var cleanupDelay = level == SuspendLevel.Hard
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromMinutes(2);
            ScheduleCleanup(cleanupDelay);
        }
        else
        {
            CancelCleanup();
        }
    }

    #endregion

    #region Window State & Maximize Fix

    private void MainWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
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
        maximizeIcon?.IsVisible = state != WindowState.Maximized;
        restoreIcon?.IsVisible = state == WindowState.Maximized;

        if (state == WindowState.Minimized)
        {
            try
            {
                if (_library?.Settings.MinimizeToTray == true)
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
            _isDeactivated = false;
            ApplySuspendLevel();
        }
        else if (_isMinimized)
        {
            _isMinimized = false;
            ApplySuspendLevel();
        }
    }

    private void UpdateMaximizePadding(WindowState state)
    {
        if (this.Content is not Grid rootGrid) return;

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

    private void HandleMinimizeClick()
    {
        try
        {
            if (_library?.Settings.MinimizeToTray == true)
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

    #region Window Deactivation & Activation

    /// <summary>
    /// Вызывается при потере окном фокуса.
    /// Использует таймер для предотвращения ложных срабатываний.
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isInTray || _isMinimized) return;

        CancelDeactivateSuspend();
        _deactivateCts = new CancellationTokenSource();
        var token = _deactivateCts.Token;

        _ = Task.Delay(DeactivateSuspendDelayMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!IsActive && !_isInTray && !_isMinimized)
                {
                    _isDeactivated = true;
                    ApplySuspendLevel();
                    Log.Debug("[Window] App deactivated, suspend level applied.");
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Вызывается при возвращении фокуса окну.
    /// Отменяет запланированный suspend, восстанавливает нормальную работу UI,
    /// и немедленно снимает mouse hook (пользователь теперь взаимодействует с окном).
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        CancelDeactivateSuspend();

        if (_isInTray) return;

        // ═══ Немедленно снимаем hook — пользователь в окне, скролл на трее невозможен ═══
#if WINDOWS
        _trayManager?.ForceUninstallHook();
#endif

        if (_isDeactivated)
        {
            _isDeactivated = false;
            ApplySuspendLevel();
            Log.Debug("[Window] App activated, suspend level lifted.");
        }
    }

    private void CancelDeactivateSuspend()
    {
        if (_deactivateCts != null)
        {
            _deactivateCts.Cancel();
            _deactivateCts.Dispose();
            _deactivateCts = null;
        }
    }

    #endregion

    #region Tray Icon Setup

    /// <summary>
    /// Настраивает иконку системного трея.
    /// На Windows — нативный TrayManager с перехватом WM_MOUSEWHEEL.
    /// На других платформах — стандартный Avalonia TrayIcon.
    /// Иконка показывается ВСЕГДА и не скрывается при восстановлении окна.
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            _playerControl = Program.Services.GetRequiredService<PlayerControlService>();
            _traySubscriptions = [];

#if WINDOWS
            SetupWindowsTray();
#else
            SetupAvaloniaTray();
#endif

            SubscribeToPlayerControl();
            Log.Info("[Tray] Tray icon configured successfully");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Tray] Failed to setup tray icon: {ex.Message}");
        }
    }

#if WINDOWS

    /// <summary>
    /// Настраивает нативный Windows TrayManager.
    /// Иконка показывается СРАЗУ (не только при MinimizeToTray).
    /// ЛКМ по иконке — toggle show/hide главного окна.
    /// </summary>
    private void SetupWindowsTray()
    {
        _trayManager = new TrayManager(
            _playerControl!,
            onToggleWindow: () => Avalonia.Threading.Dispatcher.UIThread.Post(ToggleTrayWindow),
            onExit: () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _forceClose = true;
                Close();
            }),
            onOpenQueue: () => Avalonia.Threading.Dispatcher.UIThread.Post(OnTrayGoToQueue),
            onVolumeChanged: newVolume =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleTrayVolumeChanged(newVolume)),
            isWindowVisible: () => IsVisible && !_isInTray && WindowState != WindowState.Minimized);

        var iconHandle = LoadWindowsIcon();
        _trayManager.SetIcon(iconHandle);
        _trayManager.Show();
        _trayManager.UpdateTooltipFromPlayerState();

        Log.Debug("[Tray] Windows TrayManager created, icon always visible");
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    /// <summary>
    /// Загружает иконку из ресурсов приложения для Win32 Shell_NotifyIcon.
    /// Пробует avares:// пути, при неудаче возвращает IDI_APPLICATION.
    /// </summary>
    private static IntPtr LoadWindowsIcon()
    {
        try
        {
            string[] iconPaths = ["avares://LMP/Assets/app.ico", "avares://LMP/Assets/icon.ico"];

            foreach (var path in iconPaths)
            {
                try
                {
                    var uri = new Uri(path);
                    if (!Avalonia.Platform.AssetLoader.Exists(uri)) continue;

                    using var stream = Avalonia.Platform.AssetLoader.Open(uri);

                    string tempPath = Path.Combine(Path.GetTempPath(), "lmp_tray_icon.ico");
                    using (var fs = File.Create(tempPath))
                    {
                        stream.CopyTo(fs);
                    }

                    IntPtr hIcon = LoadImage(IntPtr.Zero, tempPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);

                    try { File.Delete(tempPath); } catch { }

                    if (hIcon != IntPtr.Zero)
                        return hIcon;
                }
                catch
                {
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[Tray] LoadWindowsIcon failed: {ex.Message}");
        }

        return LoadIconW(IntPtr.Zero, 32512);
    }

#else

    /// <summary>
    /// Настраивает Avalonia TrayIcon для Linux/macOS.
    /// Иконка показывается СРАЗУ (не только при MinimizeToTray).
    /// </summary>
    private void SetupAvaloniaTray()
    {
        var icons = TrayIcon.GetIcons(Application.Current!);
        if (icons == null || icons.Count == 0)
        {
            Log.Warn("[Tray] No TrayIcon defined in App.axaml TrayIcon.Icons");
            return;
        }

        _trayIcon = icons[0];
        LoadTrayIconImage();

        // ═══ ВСЕГДА показываем иконку ═══
        _trayIcon.IsVisible = true;

        var L = LocalizationService.Instance;
        var menu = new NativeMenu();

        _showItem = new NativeMenuItem(FormatShowHideText());
        _showItem.Click += (_, _) =>
        {
            ToggleTrayWindow();
            UpdateShowHideItemText();
        };
        menu.Add(_showItem);

        menu.Add(new NativeMenuItemSeparator());

        _playPauseItem = new NativeMenuItem($"►  {L["Tray_Play"] ?? "Play"}") { IsEnabled = false };
        _playPauseItem.Click += (_, _) => _ = _playerControl?.PlayPauseAsync();
        menu.Add(_playPauseItem);

        _nextItem = new NativeMenuItem($"»  {L["Tray_Next"] ?? "Next"}") { IsEnabled = false };
        _nextItem.Click += (_, _) => _ = _playerControl?.NextAsync();
        menu.Add(_nextItem);

        _prevItem = new NativeMenuItem($"«  {L["Tray_Previous"] ?? "Previous"}") { IsEnabled = false };
        _prevItem.Click += (_, _) => _ = _playerControl?.PreviousAsync();
        menu.Add(_prevItem);

        _repeatItem = new NativeMenuItem($"↻  {L["Tray_Repeat"] ?? "Repeat"}") { IsEnabled = false };
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

        _trayIcon.Clicked += (_, _) =>
        {
            ToggleTrayWindow();
            UpdateShowHideItemText();
        };

        _trayIcon.ToolTipText = FormatTrayTooltip(_playerControl?.CurrentTrack);

        LocalizationService.Instance.LanguageChanged += OnTrayLanguageChanged;
    }

    /// <summary>
    /// Загружает иконку для Avalonia TrayIcon из ресурсов приложения.
    /// </summary>
    private void LoadTrayIconImage()
    {
        if (_trayIcon == null) return;

        string[] iconPaths =
        [
            "avares://LMP/Assets/app.ico",
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
                return;
            }
            catch { }
        }

        if (Icon != null)
        {
            _trayIcon.Icon = Icon;
        }
    }

    /// <summary>
    /// Формирует текст пункта Show/Hide в зависимости от текущей видимости окна.
    /// </summary>
    private string FormatShowHideText()
    {
        var L = LocalizationService.Instance;
        bool isVisible = IsVisible && !_isInTray && WindowState != WindowState.Minimized;

        return isVisible
            ? $"●  {L["Tray_Hide"] ?? "Hide"}"
            : $"●  {L["Tray_Show"] ?? "Show"}";
    }

    /// <summary>
    /// Обновляет текст пункта Show/Hide в меню трея после toggle видимости окна.
    /// </summary>
    private void UpdateShowHideItemText()
    {
        if (_showItem != null)
            _showItem.Header = FormatShowHideText();
    }

#endif

    /// <summary>
    /// Toggle видимости окна по ЛКМ на иконке трея.
    /// Если окно видимо и активно — сворачиваем в трей.
    /// Если окно скрыто (в трее) или свёрнуто — восстанавливаем.
    /// 
    /// <para><b>Debounce:</b> На Windows debounce реализован в
    /// <see cref="TrayManager.TryInvokeToggle"/> (WndProc уровень).
    /// На non-Windows — здесь через <see cref="_lastToggleTime"/>.</para>
    /// </summary>
    private void ToggleTrayWindow()
    {
#if !WINDOWS
        // Non-Windows debounce (на Windows — в TrayManager.TryInvokeToggle)
        long now = Environment.TickCount64;
        long elapsed = now - Volatile.Read(ref _lastToggleTime);

        if (elapsed < ToggleCooldownMs)
        {
            Log.Debug($"[Window] Toggle throttled (elapsed={elapsed}ms)");
            return;
        }

        Volatile.Write(ref _lastToggleTime, now);
#endif

        if (_isInTray || !IsVisible || WindowState == WindowState.Minimized)
        {
            RestoreFromTray();
        }
        else
        {
            MinimizeToTray();
        }
    }

    #endregion

    #region Player Control Subscriptions

    /// <summary>
    /// Подписывается на изменения состояния плеера для обновления трея.
    /// Подписки живут всё время жизни окна (включая tray-режим).
    /// </summary>
    private void SubscribeToPlayerControl()
    {
        if (_playerControl == null || _traySubscriptions == null) return;

#if WINDOWS
        // Обновляем tooltip при смене трека
        _playerControl.CurrentTrackObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => _trayManager?.UpdateTooltipFromPlayerState())
            .DisposeWith(_traySubscriptions);

        // Обновляем tooltip при смене состояния воспроизведения (play/pause)
        _playerControl.IsPlayingObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => _trayManager?.UpdateTooltipFromPlayerState())
            .DisposeWith(_traySubscriptions);

#else
        _playerControl.IsPlayingObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(UpdatePlayPauseMenuText)
            .DisposeWith(_traySubscriptions);

        _playerControl.CurrentTrackObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(track =>
            {
                bool hasTrack = track != null;
                if (_trayIcon != null)
                    _trayIcon.ToolTipText = FormatTrayTooltip(track);
                UpdatePlaybackItemsEnabled(hasTrack);
            })
            .DisposeWith(_traySubscriptions);

        _playerControl.RepeatModeObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => UpdateRepeatMenuText())
            .DisposeWith(_traySubscriptions);

        _playerControl.VolumeObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (_trayIcon != null)
                    _trayIcon.ToolTipText = FormatTrayTooltip(_playerControl.CurrentTrack);
            })
            .DisposeWith(_traySubscriptions);
#endif

        // Поддержка внешних запросов на разворачивание
        _playerControl.ResumeRequestObservable
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ =>
            {
                if (_isInTray)
                {
                    RestoreFromTray();
                }
                else if (ViewModelBase.CurrentSuspendLevel != SuspendLevel.None)
                {
                    _isDeactivated = false;
                    _isMinimized = false;
                    ApplySuspendLevel();
                }
            })
            .DisposeWith(_traySubscriptions);
    }

    #endregion

    #region Tray Menu Actions

    /// <summary>
    /// Восстанавливает окно из трея и переходит на страницу очереди.
    /// </summary>
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

    /// <summary>
    /// Обработчик кнопки "Clear Memory" из трей-меню.
    /// </summary>
    private static void OnTrayClearMemory()
    {
        Log.Info("[Tray] Manual memory cleanup requested");
        MemoryCleanupHelper.PerformCleanup(aggressive: true);
    }

    /// <summary>
    /// Обрабатывает изменение громкости через скролл колесиком над иконкой трея.
    /// Показывает tooltip с акцентом на громкости, затем восстанавливает стандартный
    /// через <see cref="TooltipRestoreDelayMs"/> после последнего scroll event.
    /// 
    /// <para><b>Debounce:</b> DispatcherTimer перезапускается при каждом вызове.
    /// Это устраняет спам TaskCanceledException который был с CTS + Task.Delay.</para>
    /// </summary>
    private void HandleTrayVolumeChanged(int newVolume)
    {
#if WINDOWS
        if (_trayManager == null) return;

        _trayManager.UpdateTooltipWithVolumeAccent();

        // ═══ Debounce: перезапускаем таймер восстановления ═══
        if (_tooltipRestoreTimer == null)
        {
            _tooltipRestoreTimer = new Avalonia.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TooltipRestoreDelayMs)
            };
            _tooltipRestoreTimer.Tick += (_, _) =>
            {
                _tooltipRestoreTimer.Stop();
                _trayManager?.UpdateTooltipFromPlayerState();
            };
        }

        _tooltipRestoreTimer.Stop();
        _tooltipRestoreTimer.Start();
#endif
        Log.Debug($"[Tray] Volume changed via tray: {newVolume}%");
    }

    #endregion

    #region Tray Tooltip Formatting (non-Windows)

#if !WINDOWS
    /// <summary>
    /// Формирует tooltip трея для non-Windows платформ.
    /// Делегирует в <see cref="TrayTooltipHelper"/> (DRY).
    /// </summary>
    private string FormatTrayTooltip(TrackInfo? track)
    {
        var volume = _playerControl?.CurrentVolume ?? 0;
        return TrayTooltipHelper.Format(track, volume);
    }
#endif

    #endregion

    #region Tray State Updates (non-Windows)

#if !WINDOWS
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

        UpdateShowHideItemText();
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

        if (_trayIcon != null)
            _trayIcon.ToolTipText = FormatTrayTooltip(_playerControl?.CurrentTrack);
    }
#endif

    #endregion

    #region Tray Show / Restore

    /// <summary>
    /// Сворачивает окно в трей. Иконка уже видна (всегда показана),
    /// просто прячем окно и переходим в Hard Suspend.
    /// </summary>
    private void MinimizeToTray()
    {
        Hide();

        _isInTray = true;
        _isMinimized = false;
        _isDeactivated = false;
        ApplySuspendLevel();

#if !WINDOWS
        UpdateShowHideItemText();
#endif

        Log.Info("[Window] Minimized to tray");
    }

    /// <summary>
    /// Восстанавливает окно из трея.
    /// Иконка остаётся видимой (не скрываем — она всегда в трее).
    /// </summary>
    private void RestoreFromTray()
    {
        bool wasInTray = _isInTray;
        _isInTray = false;
        _isMinimized = false;
        _isDeactivated = false;

        Show();
        WindowState = WindowState.Normal;
        Activate();

        if (wasInTray)
        {
            _playerControl?.ForceSync();
            ApplySuspendLevel();
        }

#if !WINDOWS
        UpdateShowHideItemText();
#endif

        Log.Info("[Window] Restored from tray");
    }

    #endregion

    #region Close Handling

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }

        var closeAction = _library?.Settings.CloseAction ?? CloseAction.Exit;

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
            _library?.UpdateSettings(s => s.CloseAction = result.Value);
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

    #region Memory Management (Cleanup)

    /// <summary>
    /// Планирует отложенную очистку памяти через <see cref="MemoryCleanupHelper"/>.
    /// </summary>
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
                MemoryCleanupHelper.PerformCleanup(aggressive: false);
            }
            catch (OperationCanceledException) { }
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

    #region Drag & Move

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;

        if (e.Source is Visual visual)
        {
            var parent = visual;
            while (parent != null)
            {
                if (parent is Button) return;
                parent = parent.GetVisualParent();
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    #endregion

    #region Copy Hint

    /// <summary>
    /// Получает запрос от любого VM или контрола.
    /// Всегда гарантирует выполнение на UI-потоке.
    /// </summary>
    private void OnCopyHintRequested(string text, CopyHintKind kind, Point? cursorPosition)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => OnCopyHintRequested(text, kind, cursorPosition));
            return;
        }

        ShowCopyHint(text, kind, cursorPosition);
    }

    /// <summary>
    /// Показывает toast у позиции курсора или в нижнем центре окна как fallback.
    /// CTS отменяет предыдущий цикл при быстрых запросах.
    /// </summary>
    private async void ShowCopyHint(string text, CopyHintKind kind, Point? cursorPosition)
    {
        if (_copyHintOverlay is null || _copyHintText is null || _copyHintCanvas is null)
        {
            Log.Warn("[CopyHint] Controls not resolved, skipping");
            return;
        }

        _copyHintCts?.Cancel();
        _copyHintCts?.Dispose();
        var cts = new CancellationTokenSource();
        _copyHintCts = cts;

        try
        {
            ApplyCopyHintKind(kind);
            _copyHintText.Text = text;

            // Показываем для измерения размера
            _copyHintOverlay.IsVisible = true;
            _copyHintOverlay.Opacity = 0;

            // Один кадр — дать layout-движку измерить размер overlay
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => { }, Avalonia.Threading.DispatcherPriority.Render);

            PositionHint(cursorPosition);

            _copyHintOverlay.Opacity = 1;

            await Task.Delay(CopyHintDurationMs, cts.Token);

            _copyHintOverlay.Opacity = 0;
            await Task.Delay(CopyHintFadeDurationMs, cts.Token);

            _copyHintOverlay.IsVisible = false;
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Позиционирует toast над точкой курсора с коррекцией выхода за границы окна.
    /// Если сверху места недостаточно, перемещает подсказку под курсор.
    /// </summary>
    private void PositionHint(Point? cursorPosition)
    {
        if (_copyHintCanvas is null || _copyHintOverlay is null)
            return;

        double hintWidth = _copyHintOverlay.DesiredSize.Width;
        double hintHeight = _copyHintOverlay.DesiredSize.Height;
        double canvasWidth = _copyHintCanvas.Bounds.Width;
        double canvasHeight = _copyHintCanvas.Bounds.Height;

        if (cursorPosition is null)
        {
            // Fallback: Центр-низ
            double fallbackX = Math.Max(8, (canvasWidth - hintWidth) * 0.5);
            double fallbackY = Math.Max(8, canvasHeight - hintHeight - 120);
            Canvas.SetLeft(_copyHintOverlay, fallbackX);
            Canvas.SetTop(_copyHintOverlay, fallbackY);
            return;
        }

        double x = cursorPosition.Value.X - (hintWidth / 2); // Центрируем по горизонтали относительно курсора

        // Пытаемся разместить НАД курсором (с отступом 12px)
        double y = cursorPosition.Value.Y - hintHeight - 12;

        // Проверка выхода за верхнюю границу
        if (y < 8)
        {
            // Если места сверху нет, прыгаем ПОД курсор (+24px)
            y = cursorPosition.Value.Y + 24;
        }

        // Ограничиваем X, чтобы не вылезти за края окна
        x = Math.Clamp(x, 8, canvasWidth - hintWidth - 8);

        // Ограничиваем Y (на случай очень длинных списков)
        y = Math.Clamp(y, 8, canvasHeight - hintHeight - 8);

        Canvas.SetLeft(_copyHintOverlay, x);
        Canvas.SetTop(_copyHintOverlay, y);
    }

    /// <summary>
    /// Применяет иконку и цвет акцента по типу hint-а.
    /// Использует StaticResource из Icons.axaml — zero alloc после первого парсинга.
    /// </summary>
    private void ApplyCopyHintKind(CopyHintKind kind)
    {
        if (_copyHintIcon is null || _copyHintOverlay is null) return;

        var (iconKey, resourceKey) = kind switch
        {
            CopyHintKind.Warning => ("Icon.InformationOutline", "SystemWarnOrangeBrush"),
            CopyHintKind.Error => ("Icon.Close", "SystemErrorRedBrush"),
            _ => ("Icon.CheckCircle", "AccentBrush")
        };

        var themeVariant = Application.Current?.ActualThemeVariant;

        if (Application.Current?.Resources.TryGetResource(iconKey, themeVariant, out var geo) == true
            && geo is StreamGeometry geometry)
        {
            _copyHintIcon.Data = geometry;
        }

        if (Application.Current?.Resources.TryGetResource(resourceKey, themeVariant, out var res) == true
            && res is IBrush brush)
        {
            _copyHintIcon.Foreground = brush;
            _copyHintOverlay.BorderBrush = brush;
        }
    }

    #endregion

    #region Dispose

    protected override void OnClosed(EventArgs e)
    {
        CancelDeactivateSuspend();
        CancelCleanup();

#if WINDOWS
        _tooltipRestoreTimer?.Stop();
        _tooltipRestoreTimer = null;
        _trayManager?.Dispose();
#else
        if (_trayIcon != null)
            _trayIcon.IsVisible = false;
        LocalizationService.Instance.LanguageChanged -= OnTrayLanguageChanged;
#endif

        _traySubscriptions?.Dispose();

        PropertyChanged -= MainWindow_PropertyChanged;
        Deactivated -= OnWindowDeactivated;
        Activated -= OnWindowActivated;

        CopyHintService.Instance.HintRequested -= OnCopyHintRequested;
        _copyHintCts?.Cancel();
        _copyHintCts?.Dispose();

        if (_copyHintOverlay != null)
        {
            _copyHintOverlay.IsVisible = false;
            _copyHintOverlay.Opacity = 0;
        }

        base.OnClosed(e);
    }

    #endregion
}