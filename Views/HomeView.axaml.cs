using Avalonia.Controls;
using MyLiteMusicPlayer.ViewModels;
using System;
using System.Reactive; // Нужно для Unit
using System.Windows.Input; // Нужно для ICommand

namespace MyLiteMusicPlayer.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is ScrollViewer sv && DataContext is HomeViewModel vm)
        {
            // Проверяем, близко ли мы к концу прокрутки (например, 200 пикселей до конца)
            if (sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 200)
            {
                // ИСПРАВЛЕНО: Приводим к ICommand для синхронной проверки CanExecute
                if ((vm.LoadMoreCommand as ICommand).CanExecute(null))
                {
                    // ИСПРАВЛЕНО: Передаем Unit.Default и подписываемся
                    vm.LoadMoreCommand.Execute(Unit.Default).Subscribe();
                }
            }
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
    }
}