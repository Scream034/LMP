using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using LMP.Core.Services;
using LMP.Core.ViewModels;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Notification = LMP.Core.Models.Notification;

namespace LMP.Features.Notifications;

public sealed class NotificationPanelViewModel : ViewModelBase
{
    private readonly NotificationService _notificationService;

    /// <summary>
    /// Размер одной пачки при инкрементальной загрузке.
    /// Первая пачка появляется мгновенно, остальные — после yield.
    /// Значение подобрано так чтобы первый экран заполнялся без задержки.
    /// </summary>
    private const int BatchSize = 8;

    /// <summary>
    /// Монотонный счётчик открытий панели. Защищает от перезаписи
    /// состояния устаревшим async-flow при быстром повторном открытии.
    /// </summary>
    private int _openGeneration;

    private ObservableCollection<Notification> Notifications
        => _notificationService.Notifications;

    /// <summary>
    /// Коллекция привязанная к ListBox.
    /// Заполняется инкрементально пачками по <see cref="BatchSize"/> элементов,
    /// чтобы не блокировать UI-поток разовым добавлением всех элементов.
    /// </summary>
    public ObservableCollection<Notification> DisplayedNotifications { get; } = [];

    public bool HasNotifications => Notifications.Count > 0;

    [Reactive] public bool IsLoading { get; private set; }

    public ReactiveCommand<Unit, Unit> ClearAllCommand { get; }
    public ReactiveCommand<string?, Unit> CopyErrorCommand { get; }

    public NotificationPanelViewModel(NotificationService notificationService)
    {
        _notificationService = notificationService;

        Notifications.CollectionChanged += OnSourceCollectionChanged;

        ClearAllCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _notificationService.ClearAll();
            DisplayedNotifications.Clear();
        }));

        CopyErrorCommand = CreateCommand(ReactiveCommand.Create<string?>(details =>
        {
            if (!string.IsNullOrEmpty(details))
                CopyToClipboard(details, "Error details");
        }));
    }

    /// <summary>
    /// Синхронизирует <see cref="DisplayedNotifications"/> при изменении источника.
    /// Работает только после завершения загрузки (<see cref="IsLoading"/> = false),
    /// чтобы не конкурировать с инкрементальным batch-flow.
    /// </summary>
    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(HasNotifications));

        if (IsLoading) return;

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (Notification n in e.NewItems)
                    DisplayedNotifications.Insert(0, n);
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (Notification n in e.OldItems)
                    DisplayedNotifications.Remove(n);
                break;

            case NotifyCollectionChangedAction.Reset:
                DisplayedNotifications.Clear();
                break;
        }
    }

    /// <summary>
    /// Вызывается при каждом открытии панели.
    ///
    /// <para>Инкрементальная загрузка пачками по <see cref="BatchSize"/> элементов:</para>
    /// <list type="number">
    ///   <item>Первая пачка добавляется синхронно — панель открывается уже с контентом.</item>
    ///   <item>После каждой следующей пачки —
    ///         <c>await InvokeAsync(Background)</c> отдаёт управление render-циклу,
    ///         UI остаётся отзывчивым между пачками.</item>
    ///   <item><see cref="IsLoading"/> = false после последней пачки.</item>
    /// </list>
    ///
    /// <para>Generation-guard защищает от конкурентных вызовов при быстром
    /// закрытии и повторном открытии панели.</para>
    /// </summary>
    public async Task OnPanelOpenedAsync()
    {
        var generation = ++_openGeneration;

        DisplayedNotifications.Clear();
        IsLoading = true;

        var snapshot = Notifications.ToList();

        if (snapshot.Count == 0)
        {
            IsLoading = false;
            return;
        }

        // Первая пачка — синхронно, без yield.
        // Панель открывается уже с видимым контентом, а не пустой.
        var firstBatch = Math.Min(BatchSize, snapshot.Count);
        for (var i = 0; i < firstBatch; i++)
            DisplayedNotifications.Add(snapshot[i]);

        // Остальные пачки — с yield между каждой,
        // чтобы Avalonia успевала рендерить между добавлениями.
        for (var i = firstBatch; i < snapshot.Count; i += BatchSize)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            if (_openGeneration != generation) return;

            var end = Math.Min(i + BatchSize, snapshot.Count);
            for (var j = i; j < end; j++)
                DisplayedNotifications.Add(snapshot[j]);
        }

        if (_openGeneration == generation)
            IsLoading = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Notifications.CollectionChanged -= OnSourceCollectionChanged;

        base.Dispose(disposing);
    }

    private static void CopyToClipboard(string text, string description)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime { MainWindow: { } win })
            {
                win.Clipboard?.SetTextAsync(text);
                Log.Info($"[Notification] {description} copied to clipboard");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[Notification] Failed to copy: {ex.Message}");
        }
    }
}