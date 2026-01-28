using System.Collections.Concurrent;
using LMP.Core.Models;

namespace LMP.Core.Services;

/// <summary>
/// Реализация Identity Map с поддержкой автоматической сборки мусора для неиспользуемых треков.
/// Использует WeakReference для временных данных (поиск) и жесткие ссылки для библиотеки.
/// </summary>
public sealed class TrackRegistry
{
    // Основное хранилище: ID -> Слабая ссылка на объект
    private readonly ConcurrentDictionary<string, WeakReference<TrackInfo>> _map = new();

    // Хранилище "важных" треков: ID -> Жесткая ссылка.
    // Это предотвращает удаление GC треков, которые есть в библиотеке, но сейчас не отображаются на экране.
    private readonly ConcurrentDictionary<string, TrackInfo> _pinnedTracks = new();

    private readonly Lock _cleanupLock = new();

    /// <summary>
    /// Получает существующий или регистрирует новый трек.
    /// </summary>
    public TrackInfo RegisterOrUpdate(TrackInfo incoming)
    {
        if (string.IsNullOrEmpty(incoming.Id)) return incoming;

        // 1. Пытаемся получить живой объект из слабой ссылки
        if (_map.TryGetValue(incoming.Id, out var weakRef))
        {
            if (weakRef.TryGetTarget(out var existing))
            {
                // Объект жив — обновляем метаданные и возвращаем его
                existing.UpdateMetadata(incoming);
                
                // Проверяем, нужно ли его "закрепить" (если он стал важным)
                UpdatePinStatus(existing);
                
                return existing;
            }
            else
            {
                // Объект умер (собран GC), удаляем мертвую ссылку
                _map.TryRemove(incoming.Id, out _);
            }
        }

        // 2. Объект не найден или умер — регистрируем incoming как новый оригинал
        var newRef = new WeakReference<TrackInfo>(incoming);
        _map.AddOrUpdate(incoming.Id, newRef, (_, _) => newRef);
        
        // Проверяем статус закрепления сразу при создании
        UpdatePinStatus(incoming);

        return incoming;
    }

    /// <summary>
    /// Пытается найти живой трек.
    /// </summary>
    public TrackInfo? TryGet(string id)
    {
        if (_map.TryGetValue(id, out var weakRef) && weakRef.TryGetTarget(out var track))
        {
            return track;
        }
        return null;
    }

    /// <summary>
    /// Управляет "закреплением" трека в памяти.
    /// Если трек в библиотеке/лайкнут/скачан — держим жесткую ссылку.
    /// Если нет — отпускаем (удаляем из _pinnedTracks), оставляя только слабую в _map.
    /// </summary>
    public void UpdatePinStatus(TrackInfo track)
    {
        bool shouldBePinned = track.IsLiked || 
                              track.IsDownloaded || 
                              track.IsDisliked || 
                              track.InPlaylists.Count > 0;

        if (shouldBePinned)
        {
            _pinnedTracks.TryAdd(track.Id, track);
        }
        else
        {
            _pinnedTracks.TryRemove(track.Id, out _);
        }
    }

    /// <summary>
    /// Загрузка из базы при старте. Все эти треки сразу становятся Pinned.
    /// </summary>
    public void Hydrate(IEnumerable<TrackInfo> tracks)
    {
        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.Id)) continue;

            // Добавляем в общую карту (Weak)
            _map[track.Id] = new WeakReference<TrackInfo>(track);
            
            // И закрепляем, так как это данные из библиотеки
            UpdatePinStatus(track);
        }
    }

    /// <summary>
    /// Возвращает все "Закрепленные" треки (для сохранения библиотеки).
    /// Мы не сохраняем мусор из поиска.
    /// </summary>
    public IEnumerable<TrackInfo> GetPinnedTracks() => _pinnedTracks.Values;

    /// <summary>
    /// Метод для периодической очистки словаря от мертвых ссылок (Optional).
    /// Можно вызывать раз в N минут или при навигации.
    /// </summary>
    public void CleanupDeadReferences()
    {
        // Запускаем в фоне, чтобы не блочить UI
        Task.Run(() =>
        {
            lock (_cleanupLock)
            {
                var keysToRemove = new List<string>();
                foreach (var kvp in _map)
                {
                    if (!kvp.Value.TryGetTarget(out _))
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                foreach (var key in keysToRemove)
                {
                    _map.TryRemove(key, out _);
                }
            }
        });
    }

    public void Clear()
    {
        _pinnedTracks.Clear();
        _map.Clear();
    }
}