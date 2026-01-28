namespace LMP.Core.Youtube.Search;

/// <summary>
/// Filter applied to a YouTube search query.
/// </summary>
public enum SearchFilter
{
    /// <summary>
    /// No filter applied.
    /// </summary>
    None,

    /// <summary>
    /// Only search for videos (Standard YouTube).
    /// </summary>
    Video,

    /// <summary>
    /// Only search for playlists (Standard YouTube).
    /// </summary>
    Playlist,

    /// <summary>
    /// Only search for channels (Standard YouTube).
    /// </summary>
    Channel,

    // --- YouTube Music Specific (WEB_REMIX context) ---

    /// <summary>
    /// General Music search (Songs + Videos + Albums).
    /// </summary>
    Music,

    /// <summary>
    /// Only search for songs (YouTube Music).
    /// </summary>
    MusicSong,

    /// <summary>
    /// Only search for music videos (YouTube Music).
    /// </summary>
    MusicVideo,

    /// <summary>
    /// Only search for albums (YouTube Music).
    /// </summary>
    MusicAlbum,

    /// <summary>
    /// Only search for artists (YouTube Music).
    /// </summary>
    MusicArtist,

    /// <summary>
    /// Only search for playlists (YouTube Music).
    /// </summary>
    MusicPlaylist
}