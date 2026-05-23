using System.Runtime.CompilerServices;

namespace LMP.Core.Audio.Helpers;

/// <summary>
/// Единый механизм проверки актуальности сессии.
/// Устраняет дупликацию паттерна <c>Interlocked.CompareExchange(ref _session, 0, 0)</c>
/// и различие операторов сравнения (<c>&lt;</c> vs <c>!=</c>) между AudioEngine и AudioPlayer.
/// </summary>
/// <remarks>
/// <para><b>Семантика:</b> Сессия «устарела» (stale) если её ID строго меньше текущего.
/// Оператор <c>&lt;</c> выбран вместо <c>!=</c> для корректной обработки wrap-around
/// при длительной работе (int.MaxValue → отрицательные значения).</para>
/// <para><b>Thread safety:</b> Все операции используют <see cref="Interlocked"/>
/// или <see cref="Volatile"/> — безопасны без дополнительных блокировок.</para>
/// </remarks>
public struct SessionGuard
{
    private int _currentSession;

    /// <summary>
    /// Текущий ID сессии (volatile read).
    /// </summary>
    public readonly int Current => Volatile.Read(ref Unsafe.AsRef(in _currentSession));

    /// <summary>
    /// Начинает новую сессию. Возвращает ID новой сессии.
    /// </summary>
    /// <returns>ID новой сессии (строго больше предыдущей).</returns>
    public int BeginNew() => Interlocked.Increment(ref _currentSession);

    /// <summary>
    /// Проверяет, устарела ли указанная сессия.
    /// </summary>
    /// <param name="sessionId">ID сессии для проверки.</param>
    /// <returns>true если <paramref name="sessionId"/> строго меньше текущей.</returns>
    public readonly bool IsStale(int sessionId) =>
        sessionId < Volatile.Read(ref Unsafe.AsRef(in _currentSession));

    /// <summary>
    /// Проверяет, устарела ли сессия или токен отменён.
    /// </summary>
    /// <param name="sessionId">ID сессии для проверки.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>true если сессия устарела или токен отменён.</returns>
    public readonly bool IsStaleOrCancelled(int sessionId, CancellationToken ct) =>
        ct.IsCancellationRequested || IsStale(sessionId);
}