using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LMP.Core.Helpers;

/// <summary>
/// ObservableCollection с поддержкой батчевых обновлений.
/// При вызове <see cref="ReplaceAll"/> генерирует ОДИН CollectionChanged(Reset)
/// вместо N отдельных Add/Remove, что критично для производительности UI
/// при фильтрации больших списков (500+ элементов).
/// </summary>
/// <typeparam name="T">Тип элементов коллекции</typeparam>
public class BatchObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    /// <summary>
    /// Атомарно заменяет всё содержимое коллекции на новые элементы.
    /// Генерирует ровно один CollectionChanged(Reset) после замены.
    /// </summary>
    /// <param name="newItems">Новые элементы для отображения</param>
    public void ReplaceAll(IList<T> newItems)
    {
        ArgumentNullException.ThrowIfNull(newItems);

        _suppressNotifications = true;

        try
        {
            Items.Clear();
            foreach (var item in newItems)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        // Один Reset-эвент вместо Clear + N×Add
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }

    /// <summary>
    /// Добавляет диапазон элементов с одним Reset-уведомлением.
    /// </summary>
    /// <param name="items">Элементы для добавления</param>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _suppressNotifications = true;

        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnCollectionChanged(e);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
        {
            base.OnPropertyChanged(e);
        }
    }
}