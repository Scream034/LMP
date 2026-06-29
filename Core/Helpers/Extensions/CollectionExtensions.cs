namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Предоставляет высокопроизводительные методы расширения для работы с коллекциями.
/// </summary>
internal static class CollectionExtensions
{
    extension<T>(IEnumerable<T?> source)
        where T : class
    {
        /// <summary>
        /// Фильтрует последовательность, отсекая все элементы со значением <c>null</c>.
        /// </summary>
        /// <returns>Последовательность, содержащая только элементы, отличные от null.</returns>
        public IEnumerable<T> WhereNotNull()
        {
            foreach (var i in source)
            {
                if (i is not null)
                    yield return i;
            }
        }
    }

    extension<T>(IEnumerable<T?> source)
        where T : struct
    {
        /// <summary>
        /// Фильтрует последовательность структур, извлекая только значимые элементы.
        /// </summary>
        /// <returns>Последовательность распакованных значимых значений.</returns>
        public IEnumerable<T> WhereNotNull()
        {
            foreach (var i in source)
            {
                if (i is not null)
                    yield return i.Value;
            }
        }
    }

    extension<T>(IEnumerable<T> source)
        where T : struct
    {
        /// <summary>
        /// Безопасно возвращает элемент по указанному индексу без создания аллокаций в куче.
        /// <para>Оптимизирован для быстрого приведения типов к <see cref="IReadOnlyList{T}"/>, <see cref="IList{T}"/>, 
        /// а также выполняет предварительную проверку границ на основе размеров коллекций.</para>
        /// </summary>
        /// <param name="index">Индекс запрашиваемого элемента.</param>
        /// <returns>Значение элемента или <c>null</c>, если индекс выходит за пределы коллекции.</returns>
        public T? ElementAtOrNull(int index)
        {
            if (index < 0) return null;

            // Каст 1: Прямой доступ через ReadOnlyList
            if (source is IReadOnlyList<T> readOnlyList)
                return index < readOnlyList.Count ? readOnlyList[index] : null;

            // Каст 2: Прямой доступ через стандартный IList
            if (source is IList<T> list)
                return index < list.Count ? list[index] : null;

            // Быстрая проверка границ для коллекций перед итерацией
            if (source is ICollection<T> collection && index >= collection.Count)
                return null;

            if (source is IReadOnlyCollection<T> readOnlyCollection && index >= readOnlyCollection.Count)
                return null;

            // Fallback: последовательный перебор без аллокаций
            using var enumerator = source.GetEnumerator();
            int currentIndex = 0;
            while (enumerator.MoveNext())
            {
                if (currentIndex == index)
                    return enumerator.Current;
                currentIndex++;
            }

            return null;
        }

        /// <summary>
        /// Возвращает первый элемент последовательности или <c>null</c>, если она пуста.
        /// </summary>
        /// <returns>Первый элемент или <c>null</c>.</returns>
        public T? FirstOrNull()
        {
            foreach (var i in source)
                return i;

            return null;
        }
    }
}