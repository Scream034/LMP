namespace LMP.Core.Helpers.Extensions;

/// <summary>
/// Предоставляет методы расширения для работы с сетевыми адресами <see cref="Uri"/>.
/// </summary>
internal static class UriExtensions
{
    extension(Uri uri)
    {
        /// <summary>
        /// Возвращает базовый домен URI (схему и хост) без выделения избыточной памяти.
        /// Использует встроенный оптимизированный метод <see cref="Uri.GetLeftPart(UriPartial)"/>.
        /// </summary>
        /// <returns>Строка, содержащая схему и хост (например, "https://rr1---sn-u5gp-hhas.googlevideo.com").</returns>
        public string Domain => uri.GetLeftPart(UriPartial.Authority);
    }
}