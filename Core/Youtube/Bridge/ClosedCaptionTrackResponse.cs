using System.Xml.Linq;
using LMP.Core.Helpers.Extensions;

namespace LMP.Core.Youtube.Bridge;

/// <summary>
/// Представляет XML-манифест субтитров (Closed Captions) YouTube.
/// </summary>
internal sealed class ClosedCaptionTrackResponse(XElement content)
{
    /// <summary>
    /// Список фрагментов субтитров в манифесте.
    /// </summary>
    public IEnumerable<CaptionData> Captions =>
        content.Descendants("p").Select(x => new CaptionData(x));

    /// <summary>
    /// Представляет один фрагмент текста субтитров.
    /// </summary>
    public sealed class CaptionData(XElement content)
    {
        /// <summary>
        /// Текст фрагмента субтитров.
        /// </summary>
        public string? Text => (string?)content;

        /// <summary>
        /// Смещение начала воспроизведения фрагмента во времени.
        /// </summary>
        public TimeSpan? Offset =>
            ((double?)content.Attribute("t"))?.Pipe(TimeSpan.FromMilliseconds);

        /// <summary>
        /// Длительность отображения фрагмента субтитров.
        /// </summary>
        public TimeSpan? Duration =>
            ((double?)content.Attribute("d"))?.Pipe(TimeSpan.FromMilliseconds);

        /// <summary>
        /// Детализированные пословесные части (слова/буквы) внутри фрагмента субтитров.
        /// </summary>
        public IReadOnlyList<PartData> Parts
        {
            get
            {
                var result = new List<PartData>();
                foreach (var x in content.Elements("s"))
                    result.Add(new PartData(x));
                return result;
            }
        }
    }

    /// <summary>
    /// Детализированная часть слова внутри фрагмента субтитров.
    /// </summary>
    public sealed class PartData(XElement content)
    {
        /// <summary>
        /// Текст части субтитра.
        /// </summary>
        public string? Text => (string?)content;

        /// <summary>
        /// Временное смещение части фрагмента.
        /// </summary>
        public TimeSpan? Offset =>
            ((double?)content.Attribute("t"))?.Pipe(TimeSpan.FromMilliseconds)
            ?? ((double?)content.Attribute("ac"))?.Pipe(TimeSpan.FromMilliseconds)
            ?? TimeSpan.Zero;
    }

    /// <summary>
    /// Создает экземпляр ClosedCaptionTrackResponse из сырого XML-текста.
    /// </summary>
    public static ClosedCaptionTrackResponse Parse(string raw) => new(Xml.Parse(raw));
}