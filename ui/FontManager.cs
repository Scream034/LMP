using System.Runtime.InteropServices;
using ImGuiNET;

namespace MyLiteMusicPlayer.UI;

public static class FontManager
{
    // Храним ссылку на массив диапазонов, чтобы GC не удалил его до построения атласа
    private static GCHandle _glyphRangesHandle;

    public static void LoadCyrillicFont(ImGuiIOPtr io)
    {
        io.Fonts.Clear();

        // 1. Пути к шрифтам
        string fontsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string mainFontPath = Path.Combine(fontsFolder, "segoeui.ttf");    // Основной текст
        string symbolFontPath = Path.Combine(fontsFolder, "seguisym.ttf"); // Иконки
        
        // Резервный вариант
        if (!File.Exists(mainFontPath)) mainFontPath = Path.Combine(fontsFolder, "arial.ttf");

        // 2. Загружаем основной шрифт (Кириллица + Латиница)
        try 
        {
            // GetGlyphRangesCyrillic() возвращает указатель на статический массив внутри ImGui,
            // его не нужно пинить или освобождать.
            io.Fonts.AddFontFromFileTTF(mainFontPath, 18.0f, null, io.Fonts.GetGlyphRangesCyrillic());
        }
        catch 
        {
            io.Fonts.AddFontDefault();
        }

        // 3. Создаем конфигурацию для слияния (Merge) вручную через выделение памяти
        // Это замена ImFontConfig_new(), который отсутствует
        int sizeOfConfig = Marshal.SizeOf<ImFontConfig>();
        IntPtr configPtr = Marshal.AllocHGlobal(sizeOfConfig);
        
        // Обязательно обнуляем память! Иначе в конфиге будет мусор.
        // Эквивалент memset(ptr, 0, size)
        byte[] emptyBytes = new byte[sizeOfConfig];
        Marshal.Copy(emptyBytes, 0, configPtr, sizeOfConfig);

        // Создаем обёртку над указателем
        ImFontConfigPtr iconConfig = new ImFontConfigPtr(configPtr);
        iconConfig.MergeMode = true; 
        iconConfig.PixelSnapH = true;
        iconConfig.GlyphOffset = new System.Numerics.Vector2(0, 2); // Сдвиг иконки вниз

        // 4. Генерируем диапазоны для иконок
        // ВАЖНО: Используем только символы из Unicode BMP (до 0xFFFF), 
        // так как стандартный ImGui не поддерживает 32-битные эмодзи (вроде 🔊 или 🔀).
        // Я подобрал безопасные аналоги из Segoe UI Symbol.
        string icons = "♪♥♡👍👎⋮⏮⏸▶⏭🔁🔂🔈🔉☰💾🔗🔍📂✖✓🗑✏➕";
        
        // Получаем указатель на диапазоны
        IntPtr rangesPtr = BuildRangesFromText(icons);

        // 5. Загружаем шрифт иконок поверх основного
        if (File.Exists(symbolFontPath))
        {
            try
            {
                // Размер 20.0f - иконки чуть крупнее текста
                io.Fonts.AddFontFromFileTTF(symbolFontPath, 20.0f, iconConfig, rangesPtr);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка загрузки иконок: {ex.Message}");
            }
        }

        // 6. Освобождаем память конфига (данные скопированы внутри AddFontFromFileTTF)
        Marshal.FreeHGlobal(configPtr);

        // 7. Строим атлас
        io.Fonts.Build();
    }

    /// <summary>
    /// Создает безопасный массив диапазонов для ImGui и закрепляет его в памяти
    /// </summary>
    private static IntPtr BuildRangesFromText(string text)
    {
        // Используем HashSet для удаления дубликатов
        var distinctChars = new HashSet<ushort>();
        
        foreach (char c in text)
        {
            // Пропускаем суррогатные пары (цветные эмодзи), так как ImGui (обычный) их не умеет
            // Если символ требует 2 char (HighSurrogate + LowSurrogate), он > 0xFFFF
            if (!char.IsSurrogate(c))
            {
                distinctChars.Add((ushort)c);
            }
        }

        // Формируем список пар {Start, End}
        var ranges = new List<ushort>();
        foreach (var c in distinctChars.OrderBy(x => x))
        {
            ranges.Add(c); // Start
            ranges.Add(c); // End
        }
        
        // Конец массива обозначается нулем
        ranges.Add(0);

        // Освобождаем предыдущий хэндл, если он был
        if (_glyphRangesHandle.IsAllocated) 
            _glyphRangesHandle.Free();

        // Закрепляем массив в памяти (Pin), чтобы GC его не переместил и не удалил
        var array = ranges.ToArray();
        _glyphRangesHandle = GCHandle.Alloc(array, GCHandleType.Pinned);

        return _glyphRangesHandle.AddrOfPinnedObject();
    }
}