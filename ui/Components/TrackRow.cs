using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;

namespace MyLiteMusicPlayer.UI.Components;

public class TrackRow
{
    private readonly LibraryService _library;
    private readonly DownloadService _downloads;
    private readonly Action<TrackInfo> _onPlay;
    private readonly Action<TrackInfo> _onEnqueue;
    private readonly Action<TrackInfo> _onStartRadio;

    private const float RowHeight = 56f;
    private const float IconSize = 44f;
    private const float ButtonSize = 28f;

    public TrackRow(
        LibraryService library,
        DownloadService downloads,
        Action<TrackInfo> onPlay,
        Action<TrackInfo> onEnqueue,
        Action<TrackInfo> onStartRadio)
    {
        _library = library;
        _downloads = downloads;
        _onPlay = onPlay;
        _onEnqueue = onEnqueue;
        _onStartRadio = onStartRadio;
    }

    public void Render(TrackInfo track, int index, TrackInfo? currentlyPlaying, string? currentPlaylistId = null)
    {
        bool isPlaying = currentlyPlaying?.Id == track.Id;

        ImGui.PushID($"track_{track.Id}_{index}");

        if (isPlaying)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.15f, 0.35f, 0.15f, 0.4f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.1f, 0.1f, 0.12f, 0.5f));
        }

        float availWidth = ImGui.GetContentRegionAvail().X;

        ImGui.BeginChild($"row_{index}", new Vector2(availWidth, RowHeight), ImGuiChildFlags.None);

        var drawList = ImGui.GetWindowDrawList();
        Vector2 rowStart = ImGui.GetCursorScreenPos();

        // Hover эффект на всю строку
        ImGui.SetCursorPos(Vector2.Zero);
        ImGui.InvisibleButton($"row_bg_{index}", new Vector2(availWidth, RowHeight));
        bool isRowHovered = ImGui.IsItemHovered();

        if (isRowHovered)
        {
            drawList.AddRectFilled(rowStart, rowStart + new Vector2(availWidth, RowHeight),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.05f)));
        }

        // Двойной клик для воспроизведения
        if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _onPlay(track);
        }

        // 1. Обложка/Иконка
        float iconPadding = (RowHeight - IconSize) / 2;
        ImGui.SetCursorPos(new Vector2(10, iconPadding));

        Vector2 iconPos = ImGui.GetCursorScreenPos();

        // Фон обложки
        drawList.AddRectFilled(iconPos, iconPos + new Vector2(IconSize, IconSize),
            ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.25f, 1f)), 6f);

        // Индикатор воспроизведения
        if (isPlaying)
        {
            Vector2 center = iconPos + new Vector2(IconSize / 2, IconSize / 2);
            // Анимированные полоски эквалайзера
            float time = (float)ImGui.GetTime();
            for (int i = 0; i < 3; i++)
            {
                float h = 8f + 8f * (float)Math.Sin(time * 4 + i * 1.5f);
                float x = center.X - 10 + i * 10;
                drawList.AddRectFilled(
                    new Vector2(x, center.Y + 10 - h),
                    new Vector2(x + 6, center.Y + 10),
                    ImGui.GetColorU32(new Vector4(0.3f, 0.9f, 0.4f, 1f)), 2f);
            }
        }
        else
        {
            // Иконка музыки
            Vector2 center = iconPos + new Vector2(IconSize / 2, IconSize / 2);
            drawList.AddText(center - new Vector2(8, 8),
                ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 1f)), "♪");
        }

        // Play overlay on hover
        if (isRowHovered && !isPlaying)
        {
            drawList.AddRectFilled(iconPos, iconPos + new Vector2(IconSize, IconSize),
                ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.6f)), 6f);

            Vector2 center = iconPos + new Vector2(IconSize / 2, IconSize / 2);
            drawList.AddTriangleFilled(
                center + new Vector2(-6, -8),
                center + new Vector2(-6, 8),
                center + new Vector2(8, 0),
                ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)));

            ImGui.SetCursorPos(new Vector2(10, iconPadding));
            if (ImGui.InvisibleButton("play_icon", new Vector2(IconSize, IconSize)))
            {
                _onPlay(track);
            }
        }

        // 2. Название и исполнитель
        float textX = 10 + IconSize + 12;
        float textWidth = availWidth - textX - 180; // Оставляем место для кнопок

        ImGui.SetCursorPos(new Vector2(textX, 8));

        // Название
        if (isPlaying)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.95f, 0.5f, 1f));
        }

        string title = TruncateText(track.Title, (int)(textWidth / 7));
        ImGui.Text(title);

        if (isPlaying)
        {
            ImGui.PopStyleColor();
        }

        // Исполнитель и длительность
        ImGui.SetCursorPos(new Vector2(textX, 28));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1f));
        string subtitle = $"{TruncateText(track.Author, 25)} • {FormatDuration(track.Duration)}";
        ImGui.Text(subtitle);
        ImGui.PopStyleColor();

        // 3. Кнопки действий (справа)
        float buttonsX = availWidth - 170;
        float buttonsY = (RowHeight - ButtonSize) / 2;

        ImGui.SetCursorPos(new Vector2(buttonsX, buttonsY));

        // Лайк
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 14f);

        if (track.IsLiked)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.3f, 0.1f, 0.1f, 0.5f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        }

        if (ImGui.Button(track.IsLiked ? "♥##like" : "♡##like", new Vector2(ButtonSize, ButtonSize)))
        {
            _library.ToggleLike(track);
        }

        ImGui.PopStyleColor(2);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(track.IsLiked ? "Убрать из любимого" : "Добавить в любимое");
        }

        ImGui.SameLine(0, 4);

        // Дизлайк
        if (track.IsDisliked)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
        }
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));

        if (ImGui.Button(track.IsDisliked ? "👎##dis" : "👍##dis", new Vector2(ButtonSize, ButtonSize)))
        {
            _library.ToggleDislike(track);
        }

        ImGui.PopStyleColor(2);

        ImGui.SameLine(0, 4);

        // Меню (три точки)
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));

        if (ImGui.Button("⋮##menu", new Vector2(ButtonSize, ButtonSize)))
        {
            ImGui.OpenPopup($"track_menu_{track.Id}_{index}");
        }

        ImGui.PopStyleColor(2);

        RenderTrackMenu(track, index);

        ImGui.SameLine(0, 4);

        // Кнопка добавления
        RenderAddButton(track, currentPlaylistId);

        ImGui.PopStyleVar(); // FrameRounding

        ImGui.EndChild();
        ImGui.PopStyleColor(); // ChildBg

        ImGui.PopID();

        ImGui.Spacing();
    }

    private void RenderAddButton(TrackInfo track, string? currentPlaylistId)
    {
        // Проверяем, есть ли трек в каких-то плейлистах (кроме liked и текущего)
        var otherPlaylists = track.InPlaylists
            .Where(p => p != "liked" && p != currentPlaylistId)
            .ToList();

        bool isInCurrentPlaylist = currentPlaylistId != null &&
                                    _library.IsTrackInPlaylist(track.Id, currentPlaylistId);

        if (isInCurrentPlaylist)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.9f, 0.4f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.1f, 0.2f, 0.1f, 0.5f));

            if (ImGui.Button("✓##added", new Vector2(ButtonSize, ButtonSize)))
            {
                ImGui.OpenPopup($"add_popup_{track.Id}");
            }

            ImGui.PopStyleColor(2);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));

            if (ImGui.Button("+##add", new Vector2(ButtonSize, ButtonSize)))
            {
                ImGui.OpenPopup($"add_popup_{track.Id}");
            }

            ImGui.PopStyleColor(2);
        }

        if (ImGui.IsItemHovered())
        {
            if (otherPlaylists.Count > 0)
            {
                var names = otherPlaylists
                    .Select(id => _library.GetPlaylist(id)?.Name ?? id)
                    .Take(3);
                ImGui.SetTooltip($"В плейлистах: {string.Join(", ", names)}");
            }
            else
            {
                ImGui.SetTooltip("Добавить в плейлист");
            }
        }

        RenderPlaylistPopup(track);
    }

    private void RenderTrackMenu(TrackInfo track, int index)
    {
        if (ImGui.BeginPopup($"track_menu_{track.Id}_{index}"))
        {
            if (ImGui.MenuItem("▶ Воспроизвести"))
            {
                _onPlay(track);
            }

            if (ImGui.MenuItem("+ Добавить в очередь"))
            {
                _onEnqueue(track);
            }

            ImGui.Separator();

            if (ImGui.MenuItem("📻 Включить радиостанцию"))
            {
                _onStartRadio(track);
            }

            ImGui.Separator();

            // Скачивание
            if (track.IsDownloaded)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.8f, 0.4f, 1f));
                ImGui.Text("✓ Скачано");
                ImGui.PopStyleColor();

                if (!string.IsNullOrEmpty(track.LocalPath) && ImGui.MenuItem("📂 Показать в папке"))
                {
                    try
                    {
                        System.Diagnostics.Process.Start("explorer.exe",
                            $"/select,\"{track.LocalPath}\"");
                    }
                    catch { }
                }
            }
            else if (_downloads.IsDownloading(track.Id))
            {
                float progress = _downloads.GetProgress(track.Id);
                ImGui.ProgressBar(progress, new Vector2(120, 0), $"{progress * 100:0}%");

                if (ImGui.MenuItem("✕ Отменить"))
                {
                    _downloads.CancelDownload(track.Id);
                }
            }
            else
            {
                if (ImGui.MenuItem("⬇ Скачать"))
                {
                    _downloads.StartDownload(track);
                }
            }

            ImGui.Separator();

            // Копирование ссылки
            if (ImGui.MenuItem("🔗 Копировать ссылку"))
            {
                ImGui.SetClipboardText(track.Url);
            }

            ImGui.EndPopup();
        }
    }

    private void RenderPlaylistPopup(TrackInfo track)
    {
        if (ImGui.BeginPopup($"add_popup_{track.Id}"))
        {
            ImGui.Text("Добавить в плейлист:");
            ImGui.Separator();

            foreach (var playlist in _library.GetAllPlaylists())
            {
                if (playlist.Id == "liked") continue;

                bool isInPlaylist = track.InPlaylists.Contains(playlist.Id);

                if (isInPlaylist)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.9f, 0.4f, 1f));
                }

                if (ImGui.MenuItem($"{(isInPlaylist ? "✓ " : "")}{playlist.Name}"))
                {
                    if (isInPlaylist)
                        _library.RemoveTrackFromPlaylist(track, playlist.Id);
                    else
                        _library.AddTrackToPlaylist(track, playlist.Id);
                }

                if (isInPlaylist)
                {
                    ImGui.PopStyleColor();
                }
            }

            ImGui.Separator();

            if (ImGui.MenuItem("+ Создать новый плейлист"))
            {
                // Быстрое создание с добавлением
                var newPlaylist = _library.CreatePlaylist($"Плейлист {DateTime.Now:HH:mm}");
                _library.AddTrackToPlaylist(track, newPlaylist.Id);
            }

            ImGui.EndPopup();
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return duration.ToString(@"h\:mm\:ss");
        return duration.ToString(@"m\:ss");
    }
}