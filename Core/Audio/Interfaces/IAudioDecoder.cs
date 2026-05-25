namespace LMP.Core.Audio.Interfaces;

/// <summary>
/// Декодер сжатого аудио в PCM.
/// </summary>
public interface IAudioDecoder : IDisposable
{
    /// <summary>
    /// Декодирует сжатый фрейм в PCM float32.
    /// </summary>
    /// <param name="encodedData">Сжатые данные (один фрейм)</param>
    /// <param name="outputBuffer">Выходной буфер (float32, interleaved)</param>
    /// <returns>Количество семплов на канал</returns>
    int Decode(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer);

    /// <summary>
    /// Декодирует со сбросом состояния (после seek).
    /// </summary>
    /// <remarks>
    /// <para>Сохранён как совместимый high-level API для прямого использования декодера.</para>
    /// <para>В основном playback pipeline предпочтителен одноразовый вызов
    /// <see cref="FlushState"/> перед декодированием warm-up skip-фреймов и
    /// последующие вызовы <see cref="Decode"/>, чтобы не выполнять специальный
    /// seek-reset path на горячем участке первого пропускаемого фрейма.</para>
    /// </remarks>
    int DecodeWithReset(ReadOnlySpan<byte> encodedData, Span<float> outputBuffer);

    /// <summary>
    /// Выполняет лёгкий сброс внутреннего состояния декодера без полного пересоздания.
    /// </summary>
    /// <remarks>
    /// <para>Метод используется при seek для одноразовой очистки decoder state
    /// перед декодированием и отбрасыванием warm-up skip-фреймов.</para>
    /// <para>Реализация зависит от кодека: AAC делает реальный soft flush внутренних
    /// буферов, Opus может быть intentional no-op, если библиотека декодирования
    /// не предоставляет безопасного промежуточного flush без hard reset.</para>
    /// </remarks>
    void FlushState();

    /// <summary>Частота дискретизации</summary>
    int SampleRate { get; }

    /// <summary>Количество каналов</summary>
    int Channels { get; }

    /// <summary>Максимальный размер выходного буфера в семплах на канал</summary>
    int MaxFrameSize { get; }

    /// <summary>Тип кодека</summary>
    AudioCodec Codec { get; }

    /// <summary>Инициализирован ли декодер</summary>
    bool IsInitialized { get; }
}