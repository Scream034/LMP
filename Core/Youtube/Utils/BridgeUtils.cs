using System.Runtime.CompilerServices;
using System.Text.Json;
using LMP.Core.Youtube.Utils.Extensions;

namespace LMP.Core.Youtube.Utils;

/// <summary>
/// Служебный класс для общих операций парсинга ответов YouTube API.
/// Содержит оптимизированные методы для извлечения токенов продолжения.
/// </summary>
internal static class BridgeUtils
{
    /// <summary>
    /// Пытается найти токен продолжения (Continuation Token) в различных структурах JSON-ответа.
    /// Поддерживает форматы:
    /// 1. commandExecutorCommand (вложенные команды, используется в "Понравившиеся").
    /// 2. continuationCommand (прямой токен).
    /// 3. nextContinuationData (старый формат).
    /// </summary>
    /// <param name="renderer">JSON-элемент, представляющий continuationItemRenderer или аналогичный объект.</param>
    /// <returns>Строка токена или null, если токен не найден.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string? ExtractContinuationToken(JsonElement renderer)
    {
        // 1. Проверяем наличие endpoint
        if (renderer.TryGetProperty("continuationEndpoint", out var endpoint))
        {
            // Вариант А: Прямой путь (continuationCommand -> token)
            if (endpoint.TryGetProperty("continuationCommand", out var directCommand) &&
                directCommand.TryGetProperty("token", out var directToken))
            {
                return directToken.GetStringOrNull();
            }

            // Вариант Б: Вложенный путь (commandExecutorCommand -> commands -> continuationCommand)
            // Это актуально для системных плейлистов типа "Понравившиеся" (Liked Videos).
            if (endpoint.TryGetProperty("commandExecutorCommand", out var executor) &&
                executor.TryGetProperty("commands", out var commands) &&
                commands.ValueKind == JsonValueKind.Array)
            {
                foreach (var cmd in commands.EnumerateArray())
                {
                    if (cmd.TryGetProperty("continuationCommand", out var nestedCommand) &&
                        nestedCommand.TryGetProperty("token", out var nestedToken))
                    {
                        return nestedToken.GetStringOrNull();
                    }
                }
            }
        }

        // 2. Проверяем старый формат (nextContinuationData -> continuation)
        if (renderer.TryGetProperty("nextContinuationData", out var nextData) &&
            nextData.TryGetProperty("continuation", out var oldToken))
        {
            return oldToken.GetStringOrNull();
        }

        return null;
    }

    /// <summary>
    /// Ищет элемент continuationItemRenderer в конце списка содержимого.
    /// </summary>
    /// <param name="contents">JSON-массив содержимого (contents/items).</param>
    /// <returns>Токен продолжения, если найден последний элемент-рендерер.</returns>
    public static string? FindTokenInContents(JsonElement contents)
    {
        if (contents.ValueKind != JsonValueKind.Array || contents.GetArrayLength() == 0)
            return null;

        // Берем последний элемент, так как токен пагинации всегда находится в конце списка
        var lastItem = contents.EnumerateArray().LastOrDefault();
        
        if (lastItem.ValueKind == JsonValueKind.Undefined)
            return null;

        if (lastItem.TryGetProperty("continuationItemRenderer", out var renderer))
        {
            return ExtractContinuationToken(renderer);
        }

        return null;
    }
}