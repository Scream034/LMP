using Avalonia.Controls;
using ReactiveUI;
using ReactiveUI.Avalonia;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace LMP.Features.Settings;

public partial class SettingsView : ReactiveUserControl<SettingsViewModel>
{
    /// <summary>
    /// Ширина ниже которой текст скрывается — остаются только иконки.
    /// 46px = иконка 18px + отступы 14px*2.
    /// </summary>
    private const double CollapsedThreshold = 120.0;

    /// <summary>Начальная ширина sidebar.</summary>
    private const double DefaultWidth = 240.0;

    /// <summary>
    /// Минимум = только иконки. Не даём утащить левее.
    /// </summary>
    private const double MinWidthSidebar = 46.0;

    /// <summary>Максимум — не даём растянуть больше половины типичного окна.</summary>
    private const double MaxWidthSidebar = 320.0;

    public SettingsView()
    {
        InitializeComponent();

        this.WhenActivated(disposables =>
        {
            if (ViewModel is not SettingsViewModel vm) return;

            // Задаём начальную ширину и ограничения для GridSplitter
            var col = LayoutGrid.ColumnDefinitions[0];
            col.Width = new GridLength(DefaultWidth, GridUnitType.Pixel);
            col.MinWidth = MinWidthSidebar;
            col.MaxWidth = MaxWidthSidebar;

            // Сброс скролла при смене страницы
            vm.WhenAnyValue(x => x.SelectedSidebarItem)
              .Skip(1)
              .Subscribe(_ => ContentScrollViewer.ScrollToHome())
              .DisposeWith(disposables);

            // Следим за реальной шириной Col0 → переключаем collapsed/expanded
            LayoutGrid.LayoutUpdated += OnLayoutUpdated;
            disposables.Add(Disposable.Create(() =>
                LayoutGrid.LayoutUpdated -= OnLayoutUpdated));

            void OnLayoutUpdated(object? sender, EventArgs e)
            {
                var actualWidth = LayoutGrid.ColumnDefinitions[0].ActualWidth;

                // Жёсткий зажим — на случай если GridSplitter всё же вышел за границы
                if (actualWidth < MinWidthSidebar)
                    LayoutGrid.ColumnDefinitions[0].Width =
                        new GridLength(MinWidthSidebar, GridUnitType.Pixel);
                else if (actualWidth > MaxWidthSidebar)
                    LayoutGrid.ColumnDefinitions[0].Width =
                        new GridLength(MaxWidthSidebar, GridUnitType.Pixel);

                // Автоматическое переключение текст ↔ только иконки
                var shouldExpand = actualWidth >= CollapsedThreshold;
                if (vm.IsSidebarExpanded != shouldExpand)
                    vm.IsSidebarExpanded = shouldExpand;
            }
        });
    }
}