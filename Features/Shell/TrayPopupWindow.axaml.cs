using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LMP.Core.Models;
using LMP.Core.Services;
using Material.Icons;
using Material.Icons.Avalonia;

namespace LMP.Features.Shell;

/// <summary>
/// Кастомное всплывающее окно контекстного меню трея.
/// 
/// <para><b>Поведение:</b></para>
/// <list type="bullet">
///   <item>Автоматически закрывается при потере фокуса (light-dismiss) и по Escape</item>
///   <item>Позиционируется рядом с курсором, не выходя за границы экрана</item>
///   <item>Секция громкости поддерживает скролл колесиком (PointerWheelChanged)</item>
///   <item>Текст и иконка кнопки Show/Hide меняются в зависимости от видимости главного окна</item>
///   <item>Все тексты локализованы и обновляются при каждом показе</item>
/// </list>
/// </summary>
public partial class TrayPopupWindow : Window
{
    #region Fields

    private PlayerControlService? _playerControl;
    private Action? _onShowWindow;
    private Action? _onExit;
    private Action? _onOpenQueue;
    private Action? _onCleanMemory;
    private Action<int>? _onVolumeChanged;

    /// <summary>
    /// Последнее известное состояние видимости главного окна.
    /// Обновляется через <see cref="UpdateState(bool)"/> перед каждым показом popup.
    /// Используется для корректного текста/иконки кнопки Show/Hide.
    /// </summary>
    private bool _lastKnownWindowVisible;

    // ═══ UI контролы ═══
    private Border? _trackInfoSection;
    private TextBlock? _trackTitleText;
    private TextBlock? _trackAuthorText;
    private Button? _showButton;
    private TextBlock? _showText;
    private MaterialIcon? _showIcon;
    private Button? _playPauseButton;
    private MaterialIcon? _playPauseIcon;
    private TextBlock? _playPauseText;
    private Button? _nextButton;
    private TextBlock? _nextText;
    private Button? _prevButton;
    private TextBlock? _prevText;
    private Button? _repeatButton;
    private MaterialIcon? _repeatIcon;
    private TextBlock? _repeatText;
    private Border? _volumeSection;
    private MaterialIcon? _volumeIcon;
    private TextBlock? _volumeText;
    private Button? _queueButton;
    private TextBlock? _queueText;
    private Button? _cleanMemButton;
    private TextBlock? _cleanMemText;
    private Button? _exitButton;
    private TextBlock? _exitText;

    /// <summary>Шаг изменения громкости при скролле в popup.</summary>
    private const int VolumeStep = 2;

    /// <summary>Примерная ширина popup для расчёта позиции (DIP).</summary>
    private const int EstimatedWidth = 260;

    /// <summary>Примерная высота popup для расчёта позиции (DIP).</summary>
    private const int EstimatedHeight = 400;

    #endregion

    #region Constructor & Init

    /// <summary>
    /// Публичный безпараметрический конструктор — необходим для Avalonia XAML loader (AVLN3001).
    /// Не используется напрямую — вызывайте <see cref="Initialize"/> после создания.
    /// </summary>
    public TrayPopupWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Инициализирует popup с зависимостями и подписками.
    /// Вызывается один раз после создания через безпараметрический конструктор.
    /// </summary>
    /// <param name="playerControl">Сервис управления плеером</param>
    /// <param name="onShowWindow">Toggle show/hide главного окна</param>
    /// <param name="onExit">Закрыть приложение</param>
    /// <param name="onOpenQueue">Открыть очередь</param>
    /// <param name="onCleanMemory">Очистить память</param>
    /// <param name="onVolumeChanged">Callback при изменении громкости скроллом</param>
    public void Initialize(
        PlayerControlService playerControl,
        Action? onShowWindow,
        Action? onExit,
        Action? onOpenQueue,
        Action? onCleanMemory,
        Action<int>? onVolumeChanged = null)
    {
        _playerControl = playerControl;
        _onShowWindow = onShowWindow;
        _onExit = onExit;
        _onOpenQueue = onOpenQueue;
        _onCleanMemory = onCleanMemory;
        _onVolumeChanged = onVolumeChanged;

        ResolveControls();
        BindClickHandlers();

        // ═══ Скролл громкости на volume section ═══
        _volumeSection?.PointerWheelChanged += OnVolumeScroll;

        // Light-dismiss: закрыть popup при потере фокуса
        Deactivated += (_, _) => HidePopup();
    }

    /// <summary>
    /// Получает ссылки на все именованные контролы из XAML-дерева.
    /// Вызывается один раз из <see cref="Initialize"/>.
    /// </summary>
    private void ResolveControls()
    {
        _trackInfoSection = this.FindControl<Border>("TrackInfoSection");
        _trackTitleText = this.FindControl<TextBlock>("TrackTitleText");
        _trackAuthorText = this.FindControl<TextBlock>("TrackAuthorText");
        _showButton = this.FindControl<Button>("ShowButton");
        _showText = this.FindControl<TextBlock>("ShowText");
        _showIcon = this.FindControl<MaterialIcon>("ShowIcon");
        _playPauseButton = this.FindControl<Button>("PlayPauseButton");
        _playPauseIcon = this.FindControl<MaterialIcon>("PlayPauseIcon");
        _playPauseText = this.FindControl<TextBlock>("PlayPauseText");
        _nextButton = this.FindControl<Button>("NextButton");
        _nextText = this.FindControl<TextBlock>("NextText");
        _prevButton = this.FindControl<Button>("PrevButton");
        _prevText = this.FindControl<TextBlock>("PrevText");
        _repeatButton = this.FindControl<Button>("RepeatButton");
        _repeatIcon = this.FindControl<MaterialIcon>("RepeatIcon");
        _repeatText = this.FindControl<TextBlock>("RepeatText");
        _volumeSection = this.FindControl<Border>("VolumeSection");
        _volumeIcon = this.FindControl<MaterialIcon>("VolumeIcon");
        _volumeText = this.FindControl<TextBlock>("VolumeText");
        _queueButton = this.FindControl<Button>("QueueButton");
        _queueText = this.FindControl<TextBlock>("QueueText");
        _cleanMemButton = this.FindControl<Button>("CleanMemButton");
        _cleanMemText = this.FindControl<TextBlock>("CleanMemText");
        _exitButton = this.FindControl<Button>("ExitButton");
        _exitText = this.FindControl<TextBlock>("ExitText");
    }

    /// <summary>
    /// Привязывает обработчики кликов ко всем кнопкам меню.
    /// Вызывается один раз из <see cref="Initialize"/>.
    /// </summary>
    private void BindClickHandlers()
    {
        _showButton?.Click += OnShowClick;
        _playPauseButton?.Click += OnPlayPauseClick;
        _nextButton?.Click += OnNextClick;
        _prevButton?.Click += OnPrevClick;
        _repeatButton?.Click += OnRepeatClick;
        _queueButton?.Click += OnQueueClick;
        _cleanMemButton?.Click += OnCleanMemClick;
        _exitButton?.Click += OnExitClick;
    }

    #endregion

    #region State Update

    /// <summary>
    /// Обновляет состояние всех элементов popup перед показом.
    /// 
    /// <para><b>Вызывается при каждом открытии popup</b> для актуализации:
    /// трек-инфо, play/pause, repeat, громкость, локализация, Show/Hide.</para>
    /// 
    /// <para><b>Show/Hide кнопка:</b> текст и иконка меняются в зависимости
    /// от <paramref name="isWindowVisible"/>:</para>
    /// <list type="bullet">
    ///   <item><c>true</c> → "Hide" + WindowMinimize icon</item>
    ///   <item><c>false</c> → "Show" + WindowRestore icon</item>
    /// </list>
    /// </summary>
    /// <param name="isWindowVisible">
    /// <c>true</c> — главное окно видимо (кнопка показывает "Hide / Свернуть").
    /// <c>false</c> — окно скрыто/в трее (кнопка показывает "Show / Показать").
    /// </param>
    public void UpdateState(bool isWindowVisible = false)
    {
        if (_playerControl == null) return;

        _lastKnownWindowVisible = isWindowVisible;

        var L = LocalizationService.Instance;
        var track = _playerControl.CurrentTrack;
        bool hasTrack = track != null;

        // ═══ Track info ═══
        UpdateTrackInfo(track, hasTrack);

        // ═══ Show / Hide — текст и иконка зависят от состояния окна ═══
        UpdateShowHideButton(isWindowVisible, L);

        // ═══ Play/Pause ═══
        UpdatePlayPauseButton(_playerControl.IsPlaying, L);

        // ═══ Next / Previous ═══
        SetText(_nextText, L["Tray_Next"] ?? "Next");
        SetText(_prevText, L["Tray_Previous"] ?? "Previous");

        // ═══ Repeat ═══
        UpdateRepeatButton(_playerControl.RepeatMode, L);

        // ═══ Volume ═══
        UpdateVolumeDisplay();

        if (_volumeSection != null)
            ToolTip.SetTip(_volumeSection, L["Tray_VolumeScrollHint"] ?? "Scroll ↕");

        // ═══ Enabled states (playback controls) ═══
        SetEnabled(_playPauseButton, hasTrack);
        SetEnabled(_nextButton, hasTrack);
        SetEnabled(_prevButton, hasTrack);
        SetEnabled(_repeatButton, hasTrack);

        // ═══ Bottom items ═══
        SetText(_queueText, L["Tray_Queue"] ?? "Queue");
        SetText(_cleanMemText, L["Tray_ClearMemory"] ?? "Clear Memory");
        SetText(_exitText, L["Tray_Exit"] ?? "Exit");
    }

    /// <summary>
    /// Обновляет секцию информации о треке.
    /// </summary>
    private void UpdateTrackInfo(TrackInfo? track, bool hasTrack)
    {
        _trackInfoSection?.IsVisible = hasTrack;

        if (hasTrack && track != null)
        {
            SetText(_trackTitleText, track.Title);
            SetText(_trackAuthorText, track.Author);
        }
    }

    /// <summary>
    /// Обновляет кнопку Show/Hide: текст и иконку в зависимости от видимости окна.
    /// </summary>
    private void UpdateShowHideButton(bool isWindowVisible, LocalizationService L)
    {
        if (isWindowVisible)
        {
            SetText(_showText, L["Tray_Hide"] ?? "Hide");
            SetIcon(_showIcon, MaterialIconKind.WindowMinimize);
        }
        else
        {
            SetText(_showText, L["Tray_Show"] ?? "Show");
            SetIcon(_showIcon, MaterialIconKind.WindowRestore);
        }
    }

    /// <summary>
    /// Обновляет кнопку Play/Pause: текст и иконку в зависимости от состояния воспроизведения.
    /// </summary>
    private void UpdatePlayPauseButton(bool isPlaying, LocalizationService L)
    {
        if (isPlaying)
        {
            SetText(_playPauseText, L["Tray_Pause"] ?? "Pause");
            SetIcon(_playPauseIcon, MaterialIconKind.Pause);
        }
        else
        {
            SetText(_playPauseText, L["Tray_Play"] ?? "Play");
            SetIcon(_playPauseIcon, MaterialIconKind.Play);
        }
    }

    /// <summary>
    /// Обновляет кнопку Repeat: текст и иконку в зависимости от режима повтора.
    /// </summary>
    private void UpdateRepeatButton(RepeatMode repeatMode, LocalizationService L)
    {
        if (_repeatText == null || _repeatIcon == null) return;

        (_repeatText.Text, _repeatIcon.Kind) = repeatMode switch
        {
            RepeatMode.All => (L["Tray_RepeatAll"] ?? "Repeat All", MaterialIconKind.Repeat),
            RepeatMode.One => (L["Tray_RepeatOne"] ?? "Repeat One", MaterialIconKind.RepeatOne),
            _ => (L["Tray_Repeat"] ?? "Repeat", MaterialIconKind.RepeatOff)
        };
    }

    /// <summary>
    /// Обновляет отображение громкости (иконка + текст).
    /// Читает напрямую из AudioEngine через <see cref="PlayerControlService.GetCurrentVolume"/>.
    /// </summary>
    private void UpdateVolumeDisplay()
    {
        if (_playerControl == null || _volumeText == null || _volumeIcon == null) return;

        var volume = _playerControl.GetCurrentVolume();
        _volumeText.Text = $"{volume}%";
        _volumeIcon.Kind = volume switch
        {
            0 => MaterialIconKind.VolumeMute,
            <= 33 => MaterialIconKind.VolumeLow,
            <= 66 => MaterialIconKind.VolumeMedium,
            _ => MaterialIconKind.VolumeHigh
        };
    }

    #endregion

    #region Show / Position

    /// <summary>
    /// Показывает popup рядом с курсором, корректируя позицию для рабочей области экрана.
    /// Использует сохранённое состояние <see cref="_lastKnownWindowVisible"/>.
    /// 
    /// <para><b>Позиционирование:</b> Если popup не помещается справа/снизу от курсора,
    /// он сдвигается влево/вверх. Гарантируется нахождение в пределах WorkingArea.</para>
    /// </summary>
    /// <param name="x">X-координата курсора в пикселях экрана</param>
    /// <param name="y">Y-координата курсора в пикселях экрана</param>
    public void ShowAt(int x, int y)
    {
        UpdateState(_lastKnownWindowVisible);

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen != null)
        {
            var workArea = screen.WorkingArea;
            var scaling = screen.Scaling;

            double dipX = x / scaling;
            double dipY = y / scaling;
            double workRight = workArea.Right / scaling;
            double workBottom = workArea.Bottom / scaling;
            double workLeft = workArea.X / scaling;
            double workTop = workArea.Y / scaling;

            // Корректируем позицию чтобы popup не выходил за границы экрана
            if (dipX + EstimatedWidth > workRight)
                dipX = workRight - EstimatedWidth;
            if (dipY + EstimatedHeight > workBottom)
                dipY -= EstimatedHeight;
            if (dipX < workLeft)
                dipX = workLeft;
            if (dipY < workTop)
                dipY = workTop;

            Position = new PixelPoint((int)(dipX * scaling), (int)(dipY * scaling));
        }
        else
        {
            Position = new PixelPoint(x, y);
        }

        Show();
        Activate();
    }

    /// <summary>Скрывает popup (light-dismiss).</summary>
    private void HidePopup() => Hide();

    #endregion

    #region Click Handlers

    private void OnShowClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onShowWindow?.Invoke();
    }

    private async void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_playerControl == null) return;
        await _playerControl.PlayPauseAsync();
        UpdateState(_lastKnownWindowVisible);
    }

    private async void OnNextClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        if (_playerControl != null) await _playerControl.NextAsync();
    }

    private async void OnPrevClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        if (_playerControl != null) await _playerControl.PreviousAsync();
    }

    private void OnRepeatClick(object? sender, RoutedEventArgs e)
    {
        _playerControl?.ToggleRepeat();
        UpdateState(_lastKnownWindowVisible);
    }

    private void OnQueueClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onOpenQueue?.Invoke();
    }

    private void OnCleanMemClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onCleanMemory?.Invoke();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        HidePopup();
        _onExit?.Invoke();
    }

    #endregion

    #region Volume Scroll

    /// <summary>
    /// Скролл колесиком на секции громкости — меняет громкость на ±<see cref="VolumeStep"/>.
    /// </summary>
    private void OnVolumeScroll(object? sender, PointerWheelEventArgs e)
    {
        if (_playerControl == null) return;

        int delta = e.Delta.Y > 0 ? VolumeStep : -VolumeStep;
        int newVolume = _playerControl.AdjustVolume(delta);

        UpdateVolumeDisplay();
        _onVolumeChanged?.Invoke(newVolume);

        e.Handled = true;
    }

    #endregion

    #region Keyboard

    /// <summary>
    /// Escape → закрыть popup.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HidePopup();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    #endregion

    #region Helpers — Null-safe Property Setters

    /// <summary>Null-safe установка текста TextBlock.</summary>
    private static void SetText(TextBlock? control, string? text)
    {
        control?.Text = text;
    }

    /// <summary>Null-safe установка Kind для MaterialIcon.</summary>
    private static void SetIcon(MaterialIcon? control, MaterialIconKind kind)
    {
        control?.Kind = kind;
    }

    /// <summary>Null-safe установка IsEnabled для Control.</summary>
    private static void SetEnabled(Control? control, bool enabled)
    {
        control?.IsEnabled = enabled;
    }

    #endregion
}