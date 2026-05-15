using Avalonia.Controls;
using Avalonia.Interactivity;

namespace LMP.Features.Notifications;

public partial class NotificationPanel : UserControl
{
    public NotificationPanel()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // Expander.ExpandingEvent всплывает от любого Expander внутри панели.
        // BringIntoView() просит родительский ScrollViewer прокрутить элемент в видимую область.
        AddHandler(
            Expander.ExpandingEvent,
            OnExpanderExpanding,
            RoutingStrategies.Bubble,
            handledEventsToo: false);
    }

    /// <summary>
    /// При раскрытии Expander прокручивает его в область видимости ScrollViewer.
    /// Исправляет UX-проблему: детали уведомления уходили за нижний край панели.
    /// </summary>
    private static void OnExpanderExpanding(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Expander expander)
            expander.BringIntoView();
    }
}