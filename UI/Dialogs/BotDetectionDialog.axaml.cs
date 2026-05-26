using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReactiveUI;
using System.Reactive;

namespace LMP.UI.Dialogs;

public partial class BotDetectionDialog : Window
{
    private static readonly LocalizationService L = LocalizationService.Instance;

    private readonly DispatcherTimer _timer;
    private readonly IDisposable? _closeSub;
    private DateTime _endTime;
    private TimeSpan _totalWaitTime;
    private bool _closedByTimer;

    #region Styled Properties

    public static readonly StyledProperty<string> DialogTitleProperty =
        AvaloniaProperty.Register<BotDetectionDialog, string>(nameof(DialogTitle), "");

    public static readonly StyledProperty<string> MessageProperty =
        AvaloniaProperty.Register<BotDetectionDialog, string>(nameof(Message), "");

    public static readonly StyledProperty<string> HintProperty =
        AvaloniaProperty.Register<BotDetectionDialog, string>(nameof(Hint), "");

    public static readonly StyledProperty<string> TimeRemainingProperty =
        AvaloniaProperty.Register<BotDetectionDialog, string>(nameof(TimeRemaining), "");

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<BotDetectionDialog, double>(nameof(Progress), 0);

    public static readonly StyledProperty<string> CloseButtonTextProperty =
        AvaloniaProperty.Register<BotDetectionDialog, string>(nameof(CloseButtonText), "OK");

    #endregion

    #region Properties

    public string DialogTitle
    {
        get => GetValue(DialogTitleProperty);
        set => SetValue(DialogTitleProperty, value);
    }

    public string Message
    {
        get => GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string TimeRemaining
    {
        get => GetValue(TimeRemainingProperty);
        set => SetValue(TimeRemainingProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public string CloseButtonText
    {
        get => GetValue(CloseButtonTextProperty);
        set => SetValue(CloseButtonTextProperty, value);
    }

    public ReactiveCommand<Unit, Unit> CloseCommand { get; }

    #endregion

    public BotDetectionDialog()
    {
        InitializeComponent();

        // Устанавливаем локализованные тексты по умолчанию
        DialogTitle = L["Dialog_BotDetection_Title"];
        Message = L["Dialog_BotDetection_Message"];
        Hint = L["Dialog_BotDetection_Hint"];
        CloseButtonText = L["Common_OK"];

        CloseCommand = ReactiveCommand.Create(() => { });
        _closeSub = CloseCommand.Subscribe(_ =>
        {
            if (IsLoaded)
            {
                Close(_closedByTimer);
            }
        });

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timer.Tick += OnTimerTick;

        DataContext = this;
    }

    /// <summary>
    /// Запускает обратный отсчёт.
    /// </summary>
    /// <param name="waitTime">Общее время ожидания.</param>
    public void StartCountdown(TimeSpan waitTime)
    {
        _totalWaitTime = waitTime;
        _endTime = DateTime.UtcNow + waitTime;
        _closedByTimer = false;

        UpdateDisplay();
        _timer.Start();
    }

    /// <summary>
    /// Обновляет отсчёт с учётом прошедшего времени (если диалог переоткрывается).
    /// </summary>
    /// <param name="remainingTime">Оставшееся время.</param>
    /// <param name="originalTotal">Изначальное полное время (для расчёта прогресса).</param>
    public void UpdateCountdown(TimeSpan remainingTime, TimeSpan originalTotal)
    {
        _totalWaitTime = originalTotal;
        _endTime = DateTime.UtcNow + remainingTime;
        _closedByTimer = false;

        UpdateDisplay();

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        var remaining = _endTime - DateTime.UtcNow;

        if (remaining <= TimeSpan.Zero)
        {
            _timer.Stop();
            _closedByTimer = true;
            Close(true);
            return;
        }

        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        var remaining = _endTime - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

        // Форматируем время
        if (remaining.TotalSeconds >= 60)
        {
            TimeRemaining = $"{remaining.Minutes:D1}:{remaining.Seconds:D2}";
        }
        else
        {
            TimeRemaining = $"{remaining.TotalSeconds:F0}s";
        }

        // Прогресс (0 = начало, 100 = конец)
        double elapsed = _totalWaitTime.TotalSeconds - remaining.TotalSeconds;
        Progress = _totalWaitTime.TotalSeconds > 0
            ? elapsed / _totalWaitTime.TotalSeconds * 100.0
            : 100;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _closeSub?.Dispose();
        base.OnClosed(e);
    }
}