using ImGuiNET;
using System.IO;

namespace MyLiteMusicPlayer.UI;

public static class FontManager
{
    public static void LoadCyrillicFont(ImGuiIOPtr io)
    {
        io.Fonts.Clear();

        // Список кандидатов (Windows)
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] fontCandidates = 
        {
            Path.Combine(fontsFolder, "consola.ttf"), // Consolas
            Path.Combine(fontsFolder, "arial.ttf"),
            Path.Combine(fontsFolder, "seguiemj.ttf") // Emoji (иногда полезно)
        };

        bool loaded = false;
        
        // Получаем диапазоны символов для кириллицы
        nint glyphRange = io.Fonts.GetGlyphRangesCyrillic();

        foreach (var path in fontCandidates)
        {
            if (File.Exists(path))
            {
                try
                {
                    // Загружаем шрифт, размер 16px
                    io.Fonts.AddFontFromFileTTF(path, 16.0f, null, glyphRange);
                    loaded = true;
                    break;
                }
                catch
                {
                    // Игнорируем ошибки конкретного шрифта
                }
            }
        }
        
        if (!loaded)
        {
            io.Fonts.AddFontDefault();
        }

        // Важно перестроить атлас текстур
        io.Fonts.Build();
    }
}