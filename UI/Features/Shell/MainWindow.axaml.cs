using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI;

namespace LMP.UI.Features.Shell;

/// <summary>
/// Главное окно приложения.
///
/// <para><b>Lifecycle:</b> управляет видимостью окна (Normal / Minimized / Tray),
/// уровнем приостановки (<see cref="SuspendLevel"/>) и системными ресурсами.</para>
///
/// <para><b>Suspend архитектура (три уровня):</b></para>
/// <list type="bullet">
///   <item><b>None</b> — окно активно, все подписки работают</item>
///   <item><b>Soft</b> — окно свёрнуто в taskbar или потеряло фокус (500мс debounce)</item>
///   <item><b>Hard</b> — окно свёрнуто в tray, максимальная экономия ресурсов</item>
/// </list>
///
/// <para><b>Tray:</b> иконка ВСЕГДА видна в системном трее.
/// На Windows — нативный <see cref="TrayManager"/> с перехватом WM_MOUSEWHEEL.
/// На других платформах — стандартный Avalonia <see cref="TrayIcon"/>.</para>
///
/// </summary>
public partial class MainWindow : Window
{
    #region Constants

    /// <summary>Задержка перед Soft Suspend при потере фокуса (мс).</summary>
    private const int DeactivateSuspendDelayMs = 500;

    /// <summary>Минимальный интервал между GC-очистками (мс).</summary>
    private const int MinCleanupIntervalMs = 30_000;

    /// <summary>Задержка восстановления tooltip после показа громкости (мс).</summary>
    private const int TooltipRestoreDelayMs = 3000;

    /// <summary>
    /// Минимальный интервал между toggle-ами окна из трея (мс).
    /// На Windows debounce в <see cref="TrayManager.TryInvokeToggle"/>.
    /// На non-Windows — в <see cref="ToggleTrayWindow"/>.
    /// </summary>
    public const int ToggleCooldownMs = 1000;

    /// <summary>Длительность отображения Copy Hint (мс), считывается напрямую из синглтона.</summary>
    private static int CopyHintDurationMs => CopyHintService.Instance.DisplayDurationMs;

    /// <summary>Длительность fade-out Copy Hint (мс).</summary>
    private const int CopyHintFadeDurationMs = 150;

    #endregion

    #region Fields — Window Chrome

    private Button? _minimizeButton;
    private Button? _maximizeButton;
    private Button? _closeButton;
    private Border? _dragArea;
    private Grid? _rootGrid;

    #endregion

    #region Fields — Window State

    /// <summary>Окно свёрнуто в системный трей (Hard Suspend).</summary>
    private volatile bool _isInTray;

    /// <summary>Окно свёрнуто в панель задач (Soft Suspend).</summary>
    private volatile bool _isMinimized;

    /// <summary>Окно потеряло фокус (Soft Suspend после debounce).</summary>
    private volatile bool _isDeactivated;

    /// <summary>
    /// Guard: окно восстанавливается из трея.
    ///
    /// <para>Предотвращает re-suspend от Deactivated race condition на Windows:
    /// <c>Show()</c> + <c>Activate()</c> может вызвать Deactivated до того
    /// как окно получит foreground focus. Без guard'а это приводило к
    /// resume → 500мс → re-suspend.</para>
    ///
    /// <para>Сбрасывается в <see cref="OnWindowActivated"/> (нормальный путь)
    /// или через fallback таймер 1.5с (если фокус не пришёл).</para>
    /// </summary>
    private volatile bool _isRestoringFromTray;

    /// <summary>Принудительное закрытие (минуя диалог подтверждения).</summary>
    private bool _forceClose;

    #endregion

    #region Fields — Services

    private PlayerControlService? _playerControl;
    private LibraryService? _library;

    #endregion

    #region Fields — Suspend Timers

    private CancellationTokenSource? _cleanupCts;
    private CancellationTokenSource? _deactivateCts;
    private DateTime _lastCleanupTime = DateTime.MinValue;

    #endregion

    #region Fields — Tray

    /// <summary>Подписки на PlayerControlService (живут всё время жизни окна).</summary>
    private CompositeDisposable? _traySubscriptions;

#if WINDOWS
    /// <summary>Нативный менеджер трея (lazy mouse hook для скролла громкости).</summary>
    private TrayManager? _trayManager;

    /// <summary>Таймер восстановления tooltip после показа громкости (debounce).</summary>
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

    /// <summary>Timestamp последнего toggle (non-Windows debounce).</summary>
    private long _lastToggleTime;
#endif

    #endregion

    #region Fields — Copy Hint

    private readonly Canvas? _copyHintCanvas;
    private readonly Border? _copyHintOverlay;
    private readonly TextBlock? _copyHintText;
    private readonly PathIcon? _copyHintIcon;
    private CancellationTokenSource? _copyHintCts;

    #endregion

    #region Constructor & Initialization

    public MainWindow()
    {
        InitializeComponent();

        _copyHintCanvas = this.FindControl<Canvas>("CopyHintCanvas");
        _copyHintOverlay = this.FindControl<Border>("CopyHintOverlay");
        _copyHintText = this.FindControl<TextBlock>("CopyHintText");
        _copyHintIcon = this.FindControl<PathIcon>("CopyHintIcon");
        _rootGrid = this.FindControl<Grid>("RootGrid");

        if (_rootGrid != null)
            MousePositionHelper.Attach(_rootGrid);

        PropertyChanged += OnWindowPropertyChanged;
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
        _rootGrid = this.FindControl<Grid>("RootGrid");

        _minimizeButton?.Click += (_, _) => HandleMinimizeClick();
        _maximizeButton?.Click += (_, _) => ToggleMaximize();
        _closeButton?.Click += (_, _) => Close();

        var titleBar = this.FindControl<Grid>("TitleBar");
        titleBar?.PointerPressed += OnTitleBarPointerPressed;
        _dragArea?.DoubleTapped += (_, _) => ToggleMaximize();

        try { _library = AppEntry.Services.GetRequiredService<LibraryService>(); }
        catch (Exception ex) { Log.Warn($"[Window] LibraryService not available: {ex.Message}"); }

        SetupTrayIcon();
    }

    #endregion

    #region Suspend Management

    /// <summary>
    /// Определяет текущий уровень приостановки на основе состояния окна.
    /// </summary>
    private SuspendLevel DetermineSuspendLevel()
    {
        if (_isInTray) return SuspendLevel.Hard;
        if (_isMinimized || _isDeactivated) return SuspendLevel.Soft;
        return SuspendLevel.None;
    }

    /// <summary>
    /// Применяет текущий уровень suspend ко всем VM через <see cref="ViewModelBase.BroadcastSuspendLevel"/>.
    /// Планирует GC-очистку для Hard/Soft режимов.
    /// </summary>
    private void ApplySuspendLevel()
    {
        var level = DetermineSuspendLevel();
        bool forceOptimize = level == SuspendLevel.Hard || ShouldOptimizeWhenInactive();

        ViewModelBase.BroadcastSuspendLevel(level, forceOptimize);

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

    private bool ShouldOptimizeWhenInactive()
    {
        try { return _library?.Settings.OptimizeWhenInactive ?? true; }
        catch { return true; }
    }

    #endregion

    #region Window State (Minimize / Maximize)

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty)
            HandleWindowStateChanged((WindowState)e.NewValue!);
    }

    /// <summary>
    /// Обрабатывает смену состояния окна.
    /// MinimizeToTray: если настройка включена, перехватывает Minimized → прячет в трей.
    /// </summary>
    private void HandleWindowStateChanged(WindowState state)
    {
        UpdateMaximizeRestoreIcons(state);

        if (state == WindowState.Minimized)
        {
            if (ShouldMinimizeToTray())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    WindowState = WindowState.Normal;
                    MinimizeToTray();
                });
                return;
            }

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

    private void HandleMinimizeClick()
    {
        if (ShouldMinimizeToTray())
            MinimizeToTray();
        else
            WindowState = WindowState.Minimized;
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>
    /// Обновляет иконки Maximize/Restore в title bar.
    /// </summary>
    private void UpdateMaximizeRestoreIcons(WindowState state)
    {
        var maximizeIcon = this.FindControl<Border>("MaximizeIcon");
        var restoreIcon = this.FindControl<Grid>("RestoreIcon");

        maximizeIcon?.IsVisible = state != WindowState.Maximized;
        restoreIcon?.IsVisible = state == WindowState.Maximized;
    }

    /// <summary>
    /// Проверяет пользовательскую настройку MinimizeToTray.
    /// </summary>
    private bool ShouldMinimizeToTray()
    {
        try { return _library?.Settings.MinimizeToTray == true; }
        catch { return false; }
    }

    #endregion

    #region Window Focus (Activate / Deactivate)

    /// <summary>
    /// Потеря фокуса. Запускает debounce-таймер на <see cref="DeactivateSuspendDelayMs"/>.
    ///
    /// <remarks><see cref="_isRestoringFromTray"/> guard блокирует deactivation
    /// во время restore из трея — на Windows <c>Show()</c> + <c>Activate()</c>
    /// часто вызывают Deactivated до получения foreground focus.</remarks>
    /// </summary>
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_isInTray || _isMinimized || _isRestoringFromTray) return;

        CancelDeactivateSuspend();
        _deactivateCts = new CancellationTokenSource();
        var token = _deactivateCts.Token;

        _ = Task.Delay(DeactivateSuspendDelayMs, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!IsActive && !_isInTray && !_isMinimized && !_isRestoringFromTray)
                {
                    _isDeactivated = true;
                    ApplySuspendLevel();
                    Log.Debug("[Window] Deactivated → Soft Suspend");
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Возвращение фокуса. Отменяет pending suspend, снимает mouse hook,
    /// сбрасывает <see cref="_isRestoringFromTray"/> guard.
    /// </summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        CancelDeactivateSuspend();

        _isRestoringFromTray = false;

        if (_isInTray) return;

#if WINDOWS
        _trayManager?.ForceUninstallHook();
#endif

        if (_isDeactivated)
        {
            _isDeactivated = false;
            ApplySuspendLevel();
            Log.Debug("[Window] Activated → Suspend lifted");
        }
    }

    private void CancelDeactivateSuspend()
    {
        if (_deactivateCts is null) return;

        _deactivateCts.Cancel();
        _deactivateCts.Dispose();
        _deactivateCts = null;
    }

    #endregion

    #region Tray — Setup

    /// <summary>
    /// Инициализирует иконку трея. На Windows — нативный TrayManager,
    /// на других платформах — Avalonia TrayIcon. Иконка показывается сразу.
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            _playerControl = AppEntry.Services.GetRequiredService<PlayerControlService>();
            _traySubscriptions = [];

#if WINDOWS
            SetupWindowsTray();
#else
            SetupAvaloniaTray();
#endif

            SubscribeToPlayerControl();
            Log.Info("[Tray] Configured");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Tray] Setup failed: {ex.Message}");
        }
    }

#if WINDOWS

    /// <summary>
    /// Создаёт нативный Windows TrayManager с mouse hook для скролла громкости.
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
            onVolumeChanged: vol =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() => HandleTrayVolumeChanged(vol)),
            isWindowVisible: () => IsVisible && !_isInTray && WindowState != WindowState.Minimized);

        _trayManager.SetIcon(LoadWindowsIcon());
        _trayManager.Show();
        _trayManager.UpdateTooltipFromPlayerState();

        Log.Debug("[Tray] Windows TrayManager created");
    }

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr LoadImage(
        IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    private static partial IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;

    /// <summary>
    /// Загружает иконку из avares:// ресурсов для Shell_NotifyIcon.
    /// Fallback: IDI_APPLICATION (стандартная системная иконка).
    /// </summary>
    private static IntPtr LoadWindowsIcon()
    {
        string[] candidates = ["avares://LMP/Assets/app.ico", "avares://LMP/Assets/icon.ico"];

        foreach (var path in candidates)
        {
            try
            {
                var uri = new Uri(path);
                if (!Avalonia.Platform.AssetLoader.Exists(uri)) continue;

                string tempPath = Path.Combine(Path.GetTempPath(), "lmp_tray_icon.ico");

                using (var source = Avalonia.Platform.AssetLoader.Open(uri))
                using (var fs = File.Create(tempPath))
                    source.CopyTo(fs);

                IntPtr hIcon = LoadImage(IntPtr.Zero, tempPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE);

                try { File.Delete(tempPath); } catch { /* best effort */ }

                if (hIcon != IntPtr.Zero) return hIcon;
            }
            catch { continue; }
        }

        return LoadIconW(IntPtr.Zero, 32512); // IDI_APPLICATION
    }

#else

    /// <summary>
    /// Настраивает Avalonia TrayIcon для Linux/macOS с контекстным меню.
    /// </summary>
    private void SetupAvaloniaTray()
    {
        var icons = TrayIcon.GetIcons(Application.Current!);
        if (icons is not { Count: > 0 })
        {
            Log.Warn("[Tray] No TrayIcon defined in App.axaml");
            return;
        }

        _trayIcon = icons[0];
        LoadTrayIconImage();
        _trayIcon.IsVisible = true;
        _trayIcon.Clicked += (_, _) =>
        {
            ToggleTrayWindow();
            UpdateShowHideItemText();
        };

        BuildAvaloniaTrayMenu();
        UpdateTrayTooltip();

        LocalizationService.Instance.LanguageChanged += OnTrayLanguageChanged;
    }

    /// <summary>
    /// Собирает нативное контекстное меню трея (non-Windows).
    /// </summary>
    private void BuildAvaloniaTrayMenu()
    {
        if (_trayIcon is null) return;

        var L = LocalizationService.Instance;
        var menu = new NativeMenu();

        _showItem = new NativeMenuItem(FormatShowHideText());
        _showItem.Click += (_, _) => { ToggleTrayWindow(); UpdateShowHideItemText(); };
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
        _exitItem.Click += (_, _) => { _forceClose = true; Close(); };
        menu.Add(_exitItem);

        _trayIcon.Menu = menu;
    }

    /// <summary>
    /// Загружает иконку для Avalonia TrayIcon из ресурсов приложения.
    /// </summary>
    private void LoadTrayIconImage()
    {
        if (_trayIcon is null) return;

        string[] candidates =
        [
            "avares://LMP/Assets/app.ico",
            "avares://LMP/Assets/icon.ico",
            "avares://LMP/Assets/icon.png",
            "avares://LMP/Assets/logo.png",
            "avares://LMP/Assets/logo.ico"
        ];

        foreach (var path in candidates)
        {
            try
            {
                var uri = new Uri(path);
                if (!Avalonia.Platform.AssetLoader.Exists(uri)) continue;

                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                _trayIcon.Icon = new WindowIcon(stream);
                return;
            }
            catch { /* try next */ }
        }

        if (Icon != null) _trayIcon.Icon = Icon;
    }

#endif

    #endregion

    #region Tray — Show / Restore

    /// <summary>
    /// Toggle видимости окна по ЛКМ на иконке трея.
    /// Debounce: на Windows — в <see cref="TrayManager.TryInvokeToggle"/>,
    /// на non-Windows — здесь через <see cref="_lastToggleTime"/>.
    /// </summary>
    private void ToggleTrayWindow()
    {
#if !WINDOWS
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastToggleTime) < ToggleCooldownMs)
        {
            Log.Debug("[Window] Toggle throttled");
            return;
        }
        Volatile.Write(ref _lastToggleTime, now);
#endif

        if (_isInTray || !IsVisible || WindowState == WindowState.Minimized)
            RestoreFromTray();
        else
            MinimizeToTray();
    }

    /// <summary>
    /// Сворачивает окно в трей (Hard Suspend).
    /// Иконка уже видна — просто прячем окно.
    /// </summary>
    private void MinimizeToTray()
    {
        Hide();

        _isInTray = true;
        _isMinimized = false;
        _isDeactivated = false;
        CancelDeactivateSuspend();
        ApplySuspendLevel();

#if !WINDOWS
        UpdateShowHideItemText();
#endif

        Log.Info("[Window] Minimized to tray");
    }

    /// <summary>
    /// Восстанавливает окно из трея.
    ///
    /// <para><b>Порядок операций критичен:</b></para>
    /// <list type="number">
    ///   <item><see cref="_isRestoringFromTray"/> guard — блокирует Deactivated race</item>
    ///   <item><see cref="CancelDeactivateSuspend"/> — отменяет pending таймер</item>
    ///   <item><c>Show()</c>/<c>Activate()</c> — могут вызвать Deactivated, но guard блокирует</item>
    ///   <item>Флаги сбрасываются ПОСЛЕ Show — исключает "щель" между состояниями</item>
    ///   <item><see cref="ApplySuspendLevel"/> безусловно — гарантирует resume</item>
    ///   <item>Guard сбрасывается в <see cref="OnWindowActivated"/> или fallback 1.5с</item>
    /// </list>
    /// </summary>
    private void RestoreFromTray()
    {
        // 1. Guard ДО Show — блокирует Deactivated race на Windows
        _isRestoringFromTray = true;
        CancelDeactivateSuspend();

        // 2. Показываем (может вызвать Deactivated — guard блокирует)
        Show();
        WindowState = WindowState.Normal;
        Activate();

        // 3. Флаги ПОСЛЕ Show
        _isInTray = false;
        _isMinimized = false;
        _isDeactivated = false;

        // 4. Безусловный resume
        _playerControl?.ForceSync();
        ApplySuspendLevel();

        // 5. Fallback: если Activated не сработал за 1.5с — снимаем guard
        _ = ClearRestoreGuardFallbackAsync();

#if !WINDOWS
        UpdateShowHideItemText();
#endif

        Log.Info("[Window] Restored from tray");
    }

    /// <summary>
    /// Fallback сброс restore guard. Нормальный путь: guard сбрасывается
    /// в <see cref="OnWindowActivated"/> (~50–200мс). Fallback нужен если
    /// Windows не дал foreground focus (редкий случай).
    /// </summary>
    private async Task ClearRestoreGuardFallbackAsync()
    {
        await Task.Delay(1500);

        if (_isRestoringFromTray)
        {
            _isRestoringFromTray = false;
            Log.Debug("[Window] Restore guard cleared by fallback");
        }
    }

    #endregion

    #region Tray — Player Subscriptions

    /// <summary>
    /// Подписки на PlayerControlService для обновления трея.
    /// Живут всё время жизни окна (включая tray-режим).
    /// </summary>
    private void SubscribeToPlayerControl()
    {
        if (_playerControl is null || _traySubscriptions is null) return;

#if WINDOWS
        _playerControl.CurrentTrackObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => _trayManager?.UpdateTooltipFromPlayerState())
            .DisposeWith(_traySubscriptions);

        _playerControl.IsPlayingObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => _trayManager?.UpdateTooltipFromPlayerState())
            .DisposeWith(_traySubscriptions);
#else
        _playerControl.IsPlayingObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(UpdatePlayPauseMenuText)
            .DisposeWith(_traySubscriptions);

        _playerControl.CurrentTrackObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(track =>
            {
                UpdatePlaybackItemsEnabled(track != null);
                UpdateTrayTooltip();
            })
            .DisposeWith(_traySubscriptions);

        _playerControl.RepeatModeObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateRepeatMenuText())
            .DisposeWith(_traySubscriptions);

        _playerControl.VolumeObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => UpdateTrayTooltip())
            .DisposeWith(_traySubscriptions);
#endif

        // Поддержка внешних запросов на resume (например из Volume popup при suspend)
        _playerControl.ResumeRequestObservable
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => HandleExternalResumeRequest())
            .DisposeWith(_traySubscriptions);
    }

    /// <summary>
    /// Обрабатывает внешний запрос на resume (например из PlayerBarView при hover на Volume).
    /// </summary>
    private void HandleExternalResumeRequest()
    {
        if (_isInTray)
        {
            RestoreFromTray();
            return;
        }

        if (ViewModelBase.CurrentSuspendLevel != SuspendLevel.None)
        {
            _isDeactivated = false;
            _isMinimized = false;
            ApplySuspendLevel();
        }
    }

    #endregion

    #region Tray — Menu Actions

    /// <summary>
    /// Восстанавливает окно и навигирует на страницу очереди.
    /// </summary>
    private void OnTrayGoToQueue()
    {
        RestoreFromTray();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is MainWindowViewModel vm)
                vm.NavigateCommand.Execute("Queue").Subscribe();
        });
    }

    private static void OnTrayClearMemory()
    {
        Log.Info("[Tray] Manual memory cleanup");
        MemoryCleanupHelper.PerformCleanup(aggressive: true);
    }

    /// <summary>
    /// Обрабатывает изменение громкости через скролл на иконке трея (Windows).
    /// Показывает tooltip с акцентом на громкости, debounce-восстанавливает стандартный.
    /// </summary>
    private void HandleTrayVolumeChanged(int newVolume)
    {
#if WINDOWS
        if (_trayManager is null) return;

        _trayManager.UpdateTooltipWithVolumeAccent();

        _tooltipRestoreTimer ??= CreateTooltipRestoreTimer();
        _tooltipRestoreTimer.Stop();
        _tooltipRestoreTimer.Start();
#endif
        Log.Debug($"[Tray] Volume: {newVolume}%");
    }

#if WINDOWS
    /// <summary>
    /// Создаёт DispatcherTimer для debounce-восстановления tooltip.
    /// Один экземпляр на весь lifecycle окна.
    /// </summary>
    private Avalonia.Threading.DispatcherTimer CreateTooltipRestoreTimer()
    {
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(TooltipRestoreDelayMs)
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _trayManager?.UpdateTooltipFromPlayerState();
        };

        return timer;
    }
#endif

    #endregion

    #region Tray — Platform Helpers (non-Windows)

#if !WINDOWS

    private string FormatShowHideText()
    {
        var L = LocalizationService.Instance;
        bool isVisible = IsVisible && !_isInTray && WindowState != WindowState.Minimized;

        return isVisible
            ? $"●  {L["Tray_Hide"] ?? "Hide"}"
            : $"●  {L["Tray_Show"] ?? "Show"}";
    }

    private void UpdateShowHideItemText()
    {
        if (_showItem != null) _showItem.Header = FormatShowHideText();
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null) return;
        int volume = _playerControl?.CurrentVolume ?? 0;
        _trayIcon.ToolTipText = TrayTooltipHelper.Format(_playerControl?.CurrentTrack, volume);
    }

    private void UpdatePlaybackItemsEnabled(bool enabled)
    {
        if (_playPauseItem != null) _playPauseItem.IsEnabled = enabled;
        if (_nextItem != null) _nextItem.IsEnabled = enabled;
        if (_prevItem != null) _prevItem.IsEnabled = enabled;
        if (_repeatItem != null) _repeatItem.IsEnabled = enabled;
    }

    private void UpdatePlayPauseMenuText(bool isPlaying)
    {
        if (_playPauseItem is null) return;
        var L = LocalizationService.Instance;

        _playPauseItem.Header = isPlaying
            ? $"‖  {L["Tray_Pause"] ?? "Pause"}"
            : $"►  {L["Tray_Play"] ?? "Play"}";
    }

    private void UpdateRepeatMenuText()
    {
        if (_repeatItem is null || _playerControl is null) return;
        var L = LocalizationService.Instance;

        _repeatItem.Header = _playerControl.RepeatMode switch
        {
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

        UpdateTrayTooltip();
    }

#endif

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
        var dialog = AppEntry.Services.GetRequiredService<DialogService>();
        var result = await dialog.ShowCloseActionDialogAsync();

        if (result is null) return;

        if (result.IsChecked)
            _library?.UpdateSettings(s => s.CloseAction = result.Value);

        if (result.Value == CloseAction.MinimizeToTray)
            MinimizeToTray();
        else
        {
            _forceClose = true;
            Close();
        }
    }

    #endregion

    #region Memory Cleanup

    /// <summary>
    /// Планирует отложенную очистку памяти. Предыдущий таймер отменяется.
    /// </summary>
    private void ScheduleCleanup(TimeSpan delay)
    {
        CancelCleanup();
        _cleanupCts = new CancellationTokenSource();
        var token = _cleanupCts.Token;

        _ = Task.Run(async () =>
        {
            // Используем безопасный метод ожидания без генерации исключений
            bool completedNormal = await DelayNoThrowAsync(delay, token);
            if (!completedNormal) return; // Была отмена, выходим

            var now = DateTime.UtcNow;
            if ((now - _lastCleanupTime).TotalMilliseconds < MinCleanupIntervalMs)
                return;

            _lastCleanupTime = now;
            MemoryCleanupHelper.PerformCleanup(aggressive: false);
        });
    }

    private void CancelCleanup()
    {
        if (_cleanupCts is null) return;

        _cleanupCts.Cancel();
        _cleanupCts.Dispose();
        _cleanupCts = null;
    }

    /// <summary>
    /// Выполняет асинхронное ожидание, которое завершается либо по истечении времени,
    /// либо при отмене токена, без выбрасывания исключений (zero-exception).
    /// </summary>
    /// <param name="delay">Время ожидания.</param>
    /// <param name="token">Токен отмены.</param>
    /// <returns>True — если ожидание завершилось штатно; False — если была запрошена отмена.</returns>
    private static async Task<bool> DelayNoThrowAsync(TimeSpan delay, CancellationToken token)
    {
        if (token.IsCancellationRequested)
            return false;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Регистрируем коллбек отмены, который переведет задачу в состояние завершения с результатом false
        using (token.Register(static state =>
               {
                   ((TaskCompletionSource<bool>)state!).TrySetResult(false);
               }, tcs))
        {
            // Запускаем нативный таймер без передачи токена, чтобы он гарантированно не выбрасывал исключений
            var delayTask = Task.Delay(delay, CancellationToken.None);

            // Ждем, что наступит раньше — таймер или отмена токена
            var completedTask = await Task.WhenAny(delayTask, tcs.Task).ConfigureAwait(false);

            // Если первым завершился таймер, возвращаем true
            return completedTask == delayTask;
        }
    }

    #endregion

    #region Title Bar & Drag

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Не перехватываем drag если клик по кнопке
        if (e.Source is Button) return;

        if (e.Source is Visual visual)
        {
            for (var parent = visual; parent != null; parent = parent.GetVisualParent())
            {
                if (parent is Button) return;
            }
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    #endregion

    #region Copy Hint Overlay

    /// <summary>
    /// Обработчик запроса от <see cref="CopyHintService"/>.
    /// Гарантирует выполнение на UI-потоке.
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
    /// Показывает toast у позиции курсора (или в нижнем центре как fallback).
    /// CTS отменяет предыдущий цикл при быстрых повторных запросах.
    /// </summary>
    private async void ShowCopyHint(string text, CopyHintKind kind, Point? cursorPosition)
    {
        if (_copyHintOverlay is null || _copyHintText is null || _copyHintCanvas is null)
            return;

        _copyHintCts?.Cancel();
        _copyHintCts?.Dispose();
        var cts = new CancellationTokenSource();
        _copyHintCts = cts;

        try
        {
            ApplyCopyHintStyle(kind);
            _copyHintText.Text = text;

            // Показываем невидимо для layout measurement
            _copyHintOverlay.IsVisible = true;
            _copyHintOverlay.Opacity = 0;

            // Ждём один render pass для корректного DesiredSize
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                () => { }, Avalonia.Threading.DispatcherPriority.Render);

            PositionCopyHint(cursorPosition);
            _copyHintOverlay.Opacity = 1;

            await Task.Delay(CopyHintDurationMs, cts.Token);

            _copyHintOverlay.Opacity = 0;
            await Task.Delay(CopyHintFadeDurationMs, cts.Token);

            _copyHintOverlay.IsVisible = false;
        }
        catch (OperationCanceledException) { /* new hint requested */ }
    }

    /// <summary>
    /// Позиционирует hint над курсором. Если сверху нет места — под курсором.
    /// </summary>
    private void PositionCopyHint(Point? cursorPosition)
    {
        if (_copyHintCanvas is null || _copyHintOverlay is null) return;

        double hintW = _copyHintOverlay.DesiredSize.Width;
        double hintH = _copyHintOverlay.DesiredSize.Height;
        double canvasW = _copyHintCanvas.Bounds.Width;
        double canvasH = _copyHintCanvas.Bounds.Height;

        const double margin = 8.0;

        if (cursorPosition is not { } cursor)
        {
            // Fallback: центр-низ (аккуратный докинг без гигантского отступа)
            Canvas.SetLeft(_copyHintOverlay, Math.Max(margin, (canvasW - hintW) * 0.5));
            Canvas.SetTop(_copyHintOverlay, Math.Max(margin, canvasH - hintH - 40));
            return;
        }

        double x = cursor.X - hintW / 2.0;
        double y = cursor.Y - hintH - 12; // над курсором

        if (y < margin)
            y = cursor.Y + 24; // под курсором

        x = Math.Clamp(x, margin, canvasW - hintW - margin);
        y = Math.Clamp(y, margin, canvasH - hintH - margin);

        Canvas.SetLeft(_copyHintOverlay, x);
        Canvas.SetTop(_copyHintOverlay, y);
    }

    /// <summary>
    /// Применяет иконку и цвет акцента по типу hint-а.
    /// </summary>
    private void ApplyCopyHintStyle(CopyHintKind kind)
    {
        if (_copyHintIcon is null || _copyHintOverlay is null) return;

        var (iconKey, brushKey) = kind switch
        {
            CopyHintKind.Warning => ("Icon.InformationOutline", "SystemWarnOrangeBrush"),
            CopyHintKind.Error => ("Icon.Close", "SystemErrorRedBrush"),
            _ => ("Icon.CheckCircle", "AccentBrush")
        };

        var theme = Application.Current?.ActualThemeVariant;

        if (Application.Current?.Resources.TryGetResource(iconKey, theme, out var geo) == true
            && geo is StreamGeometry geometry)
            _copyHintIcon.Data = geometry;

        if (Application.Current?.Resources.TryGetResource(brushKey, theme, out var res) == true
            && res is IBrush brush)
        {
            _copyHintIcon.Foreground = brush;
            _copyHintOverlay.BorderBrush = brush;
        }
    }

    #endregion

    #region Cleanup

    protected override void OnClosed(EventArgs e)
    {
        // Timers
        CancelDeactivateSuspend();
        CancelCleanup();

        // Tray
#if WINDOWS
        _tooltipRestoreTimer?.Stop();
        _tooltipRestoreTimer = null;
        _trayManager?.Dispose();
#else
        if (_trayIcon != null) _trayIcon.IsVisible = false;
        LocalizationService.Instance.LanguageChanged -= OnTrayLanguageChanged;
#endif

        _traySubscriptions?.Dispose();

        // Window events
        PropertyChanged -= OnWindowPropertyChanged;
        Deactivated -= OnWindowDeactivated;
        Activated -= OnWindowActivated;

        // Copy hint
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