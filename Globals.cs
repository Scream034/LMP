using System.Text.Json;

namespace LMP;

public static class Globals
{
    public const string AppId = "LMP";
    public const string AppName = "Lite Music Player";

    public static class Folder
    {
        public readonly static string Data = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + Path.DirectorySeparatorChar + AppId;
        public readonly static string Downloads = Path.Combine(Data, "Downloads");
        public readonly static string ImageCache = Path.Combine(Data, "ImageCache");
        public readonly static string StreamCache = Path.Combine(Data, "StreamCache");
        public readonly static string SearchCache = Path.Combine(Data, "SearchCache");

        public static void Create()
        {
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(ImageCache);
            Directory.CreateDirectory(StreamCache);
            Directory.CreateDirectory(SearchCache);
        }
    }

    public static class File
    {
        public readonly static string Cookie = Path.Combine(Folder.Data, "auth_cookies.txt");
        public readonly static string Library = Path.Combine(Folder.Data, "library.json");
        public readonly static string Theme = Path.Combine(Folder.Data, "theme.json");
    }

    public static class Json
    {
        public static readonly JsonSerializerOptions Beautiful = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public static readonly JsonSerializerOptions Compact = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }
}
