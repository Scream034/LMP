using System;
using System.Collections.Generic;

namespace MyLiteMusicPlayer.Models;

public class Playlist
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? ThumbnailUrl { get; set; }
    public string? YoutubePlaylistId { get; set; }
    public bool IsLocal { get; set; } = true;
    public bool IsFromAccount { get; set; } // Из Google аккаунта
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    
    public List<string> TrackIds { get; set; } = new();
    
    public int TrackCount => TrackIds.Count;
}