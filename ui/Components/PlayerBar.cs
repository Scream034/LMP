using System.Numerics;
using ImGuiNET;
using MyLiteMusicPlayer.Models;
using MyLiteMusicPlayer.Services;

namespace MyLiteMusicPlayer.UI.Components;

public class PlayerBar
{
    private readonly AudioEngine _audio;
    private readonly LibraryService _library;
    
    private float _volume;
    private float _seekPosition;
    private bool _isSeeking;

    public PlayerBar(AudioEngine audio, LibraryService library)
    {
        _audio = audio;
        _library = library;
        
        _volume = library.Data.Volume;
        _audio.ShuffleEnabled = library.Data.ShuffleEnabled;
        _audio.RepeatMode = library.Data.RepeatMode;
        
        _audio.SetVolume(_volume);
    }

    public void Render()
    {
        float barHeight = 90;
        var windowSize = ImGui.GetIO().DisplaySize;
        
        ImGui.SetNextWindowPos(new Vector2(0, windowSize.Y - barHeight));
        ImGui.SetNextWindowSize(new Vector2(windowSize.X, barHeight));
        
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                 ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar |
                                 ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBringToFrontOnFocus;
        
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.06f, 0.06f, 0.08f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 10));
        
        if (ImGui.Begin("PlayerBar", flags))
        {
            var track = _audio.CurrentTrack;
            
            // Верхняя часть - прогресс-бар
            RenderProgressBar(track, windowSize.X - 40);
            
            ImGui.Spacing();
            
            // Нижняя часть - 3 колонки
            float columnWidth = (windowSize.X - 40) / 3;
            
            // Левая часть - информация о треке
            ImGui.BeginChild("TrackInfo", new Vector2(columnWidth, 50), ImGuiChildFlags.None);
            RenderTrackInfo(track);
            ImGui.EndChild();
            
            ImGui.SameLine();
            
            // Центр - управление воспроизведением
            ImGui.BeginChild("Controls", new Vector2(columnWidth, 50), ImGuiChildFlags.None);
            RenderPlaybackControls();
            ImGui.EndChild();
            
            ImGui.SameLine();
            
            // Правая часть - громкость и очередь
            ImGui.BeginChild("VolumeQueue", new Vector2(columnWidth, 50), ImGuiChildFlags.None);
            RenderVolumeAndQueue();
            ImGui.EndChild();
        }
        ImGui.End();
        
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    private void RenderProgressBar(TrackInfo? track, float width)
    {
        if (track == null)
        {
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, new Vector4(0.3f, 0.3f, 0.3f, 1f));
            ImGui.ProgressBar(0f, new Vector2(width, 4), "");
            ImGui.PopStyleColor();
            return;
        }
        
        var currentPos = _audio.CurrentPosition;
        var totalDur = _audio.TotalDuration;
        
        float progress = totalDur.TotalSeconds > 0 
            ? (float)(currentPos.TotalSeconds / totalDur.TotalSeconds) 
            : 0f;
        
        if (_isSeeking)
            progress = _seekPosition;
        
        // Время слева
        ImGui.TextDisabled(FormatTime(currentPos));
        ImGui.SameLine();
        
        // Слайдер прогресса
        float sliderWidth = width - 100;
        ImGui.SetNextItemWidth(sliderWidth);
        
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.2f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab, new Vector4(1f, 1f, 1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, new Vector4(0.4f, 0.9f, 0.4f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 2f);
        
        if (ImGui.SliderFloat("##progress", ref progress, 0f, 1f, ""))
        {
            _isSeeking = true;
            _seekPosition = progress;
        }
        
        // Применяем seek при отпускании мыши
        if (_isSeeking && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            var seekTime = TimeSpan.FromSeconds(_seekPosition * totalDur.TotalSeconds);
            _audio.Seek(seekTime);
            _isSeeking = false;
        }
        
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(3);
        
        // Время справа
        ImGui.SameLine();
        ImGui.TextDisabled(FormatTime(totalDur));
    }

    private void RenderTrackInfo(TrackInfo? track)
    {
        if (track == null)
        {
            ImGui.SetCursorPosY(15);
            ImGui.TextDisabled("Ничего не воспроизводится");
            return;
        }
        
        var drawList = ImGui.GetWindowDrawList();
        Vector2 startPos = ImGui.GetCursorScreenPos();
        
        // Обложка
        float iconSize = 45;
        drawList.AddRectFilled(startPos, startPos + new Vector2(iconSize, iconSize),
            ImGui.GetColorU32(new Vector4(0.2f, 0.2f, 0.25f, 1f)), 6f);
        
        // Иконка музыки
        Vector2 center = startPos + new Vector2(iconSize / 2, iconSize / 2);
        drawList.AddText(center - new Vector2(6, 8), 
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "♪");
        
        // Текст
        ImGui.SetCursorPos(new Vector2(iconSize + 12, 5));
        ImGui.Text(TruncateText(track.Title, 30));
        
        ImGui.SetCursorPos(new Vector2(iconSize + 12, 25));
        ImGui.TextDisabled(TruncateText(track.Author, 25));
        
        // Лайк кнопка рядом с информацией
        ImGui.SameLine();
        ImGui.SetCursorPosY(10);
        
        if (track.IsLiked)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.3f, 0.3f, 1f));
        }
        
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        
        if (ImGui.Button(track.IsLiked ? "♥##plike" : "♡##plike", new Vector2(30, 30)))
        {
            _library.ToggleLike(track);
        }
        
        ImGui.PopStyleColor();
        
        if (track.IsLiked)
        {
            ImGui.PopStyleColor();
        }
    }

    private void RenderPlaybackControls()
    {
        float centerX = ImGui.GetContentRegionAvail().X / 2;
        float buttonSize = 40;
        float smallButtonSize = 35;
        float totalWidth = smallButtonSize * 4 + buttonSize + 20;
        
        ImGui.SetCursorPosX(centerX - totalWidth / 2);
        ImGui.SetCursorPosY(5);
        
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 20f);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        
        // Shuffle
        bool shuffleEnabled = _audio.ShuffleEnabled;
        if (shuffleEnabled)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.9f, 0.4f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
        }
        
        if (ImGui.Button("🔀##shuffle", new Vector2(smallButtonSize, smallButtonSize)))
        {
            _audio.ShuffleEnabled = !_audio.ShuffleEnabled;
            _library.Data.ShuffleEnabled = _audio.ShuffleEnabled;
            _library.Save();
        }
        
        ImGui.PopStyleColor();
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(shuffleEnabled ? "Перемешивание: вкл" : "Перемешивание: выкл");
        }
        
        ImGui.SameLine(0, 4);
        
        // Previous
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.9f, 0.9f, 1f));
        
        if (ImGui.Button("⏮##prev", new Vector2(smallButtonSize, smallButtonSize)))
        {
            _audio.PlayPrevious();
        }
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Предыдущий трек");
        }
        
        ImGui.SameLine(0, 4);
        
        // Play/Pause (большая кнопка)
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1f, 1f, 1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.9f, 0.9f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0f, 0f, 0f, 1f));
        
        bool isPlaying = _audio.IsPlaying;
        if (ImGui.Button(isPlaying ? "⏸##pp" : "▶##pp", new Vector2(buttonSize, buttonSize)))
        {
            _audio.TogglePlayPause();
        }
        
        ImGui.PopStyleColor(3);
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(isPlaying ? "Пауза" : "Воспроизвести");
        }
        
        ImGui.SameLine(0, 4);
        
        // Next
        if (ImGui.Button("⏭##next", new Vector2(smallButtonSize, smallButtonSize)))
        {
            _audio.PlayNext();
        }
        
        ImGui.PopStyleColor(); // Text color
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Следующий трек");
        }
        
        ImGui.SameLine(0, 4);
        
        // Repeat
        var repeatMode = _audio.RepeatMode;
        string repeatIcon = repeatMode switch
        {
            RepeatMode.RepeatOne => "🔂",
            _ => "🔁"
        };
        
        if (repeatMode != RepeatMode.None)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.9f, 0.4f, 1f));
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.7f, 0.7f, 0.7f, 1f));
        }
        
        if (ImGui.Button($"{repeatIcon}##repeat", new Vector2(smallButtonSize, smallButtonSize)))
        {
            _audio.RepeatMode = (RepeatMode)(((int)_audio.RepeatMode + 1) % 3);
            _library.Data.RepeatMode = _audio.RepeatMode;
            _library.Save();
        }
        
        ImGui.PopStyleColor();
        
        string repeatTooltip = repeatMode switch
        {
            RepeatMode.None => "Повтор: выкл",
            RepeatMode.RepeatOne => "Повтор: один трек",
            RepeatMode.RepeatAll => "Повтор: все треки",
            _ => ""
        };
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(repeatTooltip);
        }
        
        ImGui.PopStyleColor(); // Button transparent
        ImGui.PopStyleVar(); // FrameRounding
    }

    private void RenderVolumeAndQueue()
    {
        float rightAlign = ImGui.GetContentRegionAvail().X;
        
        ImGui.SetCursorPosY(10);
        ImGui.SetCursorPosX(rightAlign - 180);
        
        // Иконка громкости
        string volumeIcon = _volume switch
        {
            0 => "🔇",
            < 0.3f => "🔈",
            < 0.7f => "🔉",
            _ => "🔊"
        };
        
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        
        if (ImGui.Button($"{volumeIcon}##vol", new Vector2(30, 30)))
        {
            // Toggle mute
            if (_volume > 0)
            {
                _library.Data.Volume = _volume; // Сохраняем для восстановления
                _volume = 0;
            }
            else
            {
                _volume = _library.Data.Volume > 0 ? _library.Data.Volume : 0.5f;
            }
            _audio.SetVolume(_volume);
        }
        
        ImGui.PopStyleColor();
        
        ImGui.SameLine();
        ImGui.SetCursorPosY(15);
        
        // Слайдер громкости
        ImGui.SetNextItemWidth(100);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabMinSize, 10f);
        
        if (ImGui.SliderFloat("##volume", ref _volume, 0f, 1f, ""))
        {
            _audio.SetVolume(_volume);
            _library.Data.Volume = _volume;
            _library.Save();
        }
        
        ImGui.PopStyleVar(2);
        
        ImGui.SameLine();
        ImGui.SetCursorPosY(10);
        
        // Кнопка очереди
        int queueCount = _audio.QueueCount;
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0f, 0f, 0f, 0f));
        
        if (ImGui.Button($"☰##queue", new Vector2(30, 30)))
        {
            ImGui.OpenPopup("QueuePopup");
        }
        
        ImGui.PopStyleColor();
        
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Очередь ({queueCount})");
        }
        
        // Popup с очередью
        RenderQueuePopup();
    }

    private void RenderQueuePopup()
    {
        ImGui.SetNextWindowSize(new Vector2(350, 400), ImGuiCond.FirstUseEver);
        
        if (ImGui.BeginPopup("QueuePopup"))
        {
            ImGui.Text("Очередь воспроизведения");
            
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 60);
            
            if (ImGui.SmallButton("Очистить"))
            {
                _audio.ClearQueue();
            }
            
            ImGui.Separator();
            
            var queue = _audio.GetQueueCopy();
            
            if (queue.Count == 0)
            {
                ImGui.TextDisabled("Очередь пуста");
            }
            else
            {
                for (int i = 0; i < queue.Count && i < 20; i++)
                {
                    var track = queue[i];
                    ImGui.PushID($"q_{i}");
                    
                    ImGui.Text($"{i + 1}.");
                    ImGui.SameLine();
                    ImGui.Text(TruncateText(track.Title, 30));
                    ImGui.SameLine();
                    ImGui.TextDisabled(TruncateText(track.Author, 15));
                    
                    ImGui.PopID();
                }
                
                if (queue.Count > 20)
                {
                    ImGui.TextDisabled($"...и ещё {queue.Count - 20} треков");
                }
            }
            
            ImGui.EndPopup();
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalHours >= 1)
            return time.ToString(@"h\:mm\:ss");
        return time.ToString(@"m\:ss");
    }
}