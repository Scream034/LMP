using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using System.Linq;

namespace LMP.UI.Features.Library;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Сохраняет акцентную обводку карточки плейлиста при открытии контекстного меню.
    /// Вызывается после полного открытия меню, когда логические связи дерева уже выстроены.
    /// </summary>
    private void OnPlaylistContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            var target = menu.GetLogicalAncestors()
                .OfType<Control>()
                .FirstOrDefault(c => c.Classes.Contains("playlist-card-inner"));

            target?.Classes.Add("menu-open");
        }
    }

    /// <summary>
    /// Снимает акцентную обводку после закрытия контекстного меню.
    /// </summary>
    private void OnPlaylistContextMenuClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu)
        {
            var target = menu.GetLogicalAncestors()
                .OfType<Control>()
                .FirstOrDefault(c => c.Classes.Contains("playlist-card-inner"));

            target?.Classes.Remove("menu-open");
        }
    }
}