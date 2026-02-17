// Globals.cs
using System.Text.Json;

namespace LMP;

public static class Globals
{
    public const string AppId = "LMP";
    public const string AppName = "Lite Music Player";

    public static class Folder
    {
        public static readonly string Data = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppId);
        public static readonly string Cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppId);
        public static readonly string Logs = Path.Combine(Data, "Logs");
        public static readonly string Downloads = Path.Combine(Data, "Downloads");
        public static readonly string ImageCache = Path.Combine(Cache, "ImageCache");
        public static readonly string StreamCache = Path.Combine(Cache, "StreamCache");
        public static readonly string SearchCache = Path.Combine(Cache, "SearchCache");
        public static readonly string AudioCache = Path.Combine(Cache, "AudioCache");

        public static void Create()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Cache);
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(ImageCache);
            Directory.CreateDirectory(StreamCache);
            Directory.CreateDirectory(SearchCache);
            Directory.CreateDirectory(AudioCache);
            Directory.CreateDirectory(Logs);
        }
    }

    public static class File
    {
        public static readonly string Cookie = Path.Combine(Folder.Data, "auth_cookies.txt");
        public static readonly string Library = Path.Combine(Folder.Data, "library.json"); // Legacy
        public static readonly string Database = Path.Combine(Folder.Data, "library.db");  // New SQLite
        public static readonly string Theme = Path.Combine(Folder.Data, "theme.json");
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Beautiful = new()
        {
            WriteIndented = true,
        };

        public static readonly JsonSerializerOptions Compact = new()
        {
            WriteIndented = false,
        };
    }
}