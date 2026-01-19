using System.Numerics;
using System.Collections.Concurrent;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using ImGuiNET;
using MyLiteMusicPlayer.Services;
using DiscordRPC;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.UI;

public class MainWindow : IDisposable
{
    private IWindow _window;
    private ImGuiController _controller = null!;
    private GL _gl = null!;
    private IInputContext _inputContext = null!;
    private DiscordRpcClient? _discord;

    private readonly AudioEngine _audio;
    private readonly YoutubeProvider _youtube;

    private readonly ConcurrentQueue<Action> _uiActionQueue = new();

    private string _searchQuery = "";
    private string _statusMessage = "Загрузка движка...";
    private bool _isSearching = false;
    private float _volume = 0.5f;

    public MainWindow()
    {
        _audio = new AudioEngine();
        _youtube = new YoutubeProvider();

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1024, 768);
        options.Title = "Lite YT Player";
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
        options.VSync = true;

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Closing += OnClose;
        _window.Resize += s => _gl?.Viewport(s);
    }

    public void Run() => _window.Run();

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _inputContext = _window.CreateInput();

        _controller = new ImGuiController(
            _gl,
            _window,
            _inputContext,
            () =>
            {
                var io = ImGui.GetIO();
                FontManager.LoadCyrillicFont(io);
                ImGuiClipboardBridge.Install();

                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
                io.BackendFlags |= ImGuiBackendFlags.HasSetMousePos;
            }
        );

        SetupInputHandlers();

        Task.Run(async () =>
        {
            await _youtube.InitializeAsync();
            EnqueueUiUpdate(() => _statusMessage = "Готов к работе");
        });

        InitializeDiscord();

        _audio.OnTrackChanged += (track) =>
        {
            EnqueueUiUpdate(() =>
            {
                _statusMessage = "Играет...";
                UpdateDiscord(track);
            });
        };
    }

    private void InitializeDiscord()
    {
        try
        {
            _discord = new DiscordRpcClient("1462627821871562824");
            _discord.Initialize();
        }
        catch { /* Ignore */ }
    }

    // --- INPUT HANDLING ---

    // Таблица маппинга scancode → ImGuiKey для основных клавиш
    // Scancode-ы одинаковы независимо от языковой раскладки!
    private static readonly Dictionary<int, ImGuiKey> ScancodeToImGuiKey = new()
{
    // Буквы (US QWERTY scancodes)
    { 0x1E, ImGuiKey.A }, { 0x30, ImGuiKey.B }, { 0x2E, ImGuiKey.C },
    { 0x20, ImGuiKey.D }, { 0x12, ImGuiKey.E }, { 0x21, ImGuiKey.F },
    { 0x22, ImGuiKey.G }, { 0x23, ImGuiKey.H }, { 0x17, ImGuiKey.I },
    { 0x24, ImGuiKey.J }, { 0x25, ImGuiKey.K }, { 0x26, ImGuiKey.L },
    { 0x32, ImGuiKey.M }, { 0x31, ImGuiKey.N }, { 0x18, ImGuiKey.O },
    { 0x19, ImGuiKey.P }, { 0x10, ImGuiKey.Q }, { 0x13, ImGuiKey.R },
    { 0x1F, ImGuiKey.S }, { 0x14, ImGuiKey.T }, { 0x16, ImGuiKey.U },
    { 0x2F, ImGuiKey.V }, { 0x11, ImGuiKey.W }, { 0x2D, ImGuiKey.X },
    { 0x15, ImGuiKey.Y }, { 0x2C, ImGuiKey.Z },
    
    // Цифры
    { 0x0B, ImGuiKey._0 }, { 0x02, ImGuiKey._1 }, { 0x03, ImGuiKey._2 },
    { 0x04, ImGuiKey._3 }, { 0x05, ImGuiKey._4 }, { 0x06, ImGuiKey._5 },
    { 0x07, ImGuiKey._6 }, { 0x08, ImGuiKey._7 }, { 0x09, ImGuiKey._8 },
    { 0x0A, ImGuiKey._9 },
    
    // Управление
    { 0x01, ImGuiKey.Escape },
    { 0x0F, ImGuiKey.Tab },
    { 0x3A, ImGuiKey.CapsLock },
    { 0x2A, ImGuiKey.LeftShift },
    { 0x36, ImGuiKey.RightShift },
    { 0x1D, ImGuiKey.LeftCtrl },
    { 0x38, ImGuiKey.LeftAlt },
    { 0x39, ImGuiKey.Space },
    { 0x1C, ImGuiKey.Enter },
    { 0x0E, ImGuiKey.Backspace },
    { 0x53, ImGuiKey.Delete },
    { 0x52, ImGuiKey.Insert },
    { 0x47, ImGuiKey.Home },
    { 0x4F, ImGuiKey.End },
    { 0x49, ImGuiKey.PageUp },
    { 0x51, ImGuiKey.PageDown },
    
    // Стрелки
    { 0x48, ImGuiKey.UpArrow },
    { 0x50, ImGuiKey.DownArrow },
    { 0x4B, ImGuiKey.LeftArrow },
    { 0x4D, ImGuiKey.RightArrow },
};

    private void SetupInputHandlers()
    {
        if (_inputContext.Keyboards.Count == 0) return;

        var kb = _inputContext.Keyboards[0];
        kb.KeyDown += OnKeyDown;
        kb.KeyUp += OnKeyUp;
        // KeyChar обрабатывается ImGuiController автоматически
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        HandleKeyEvent(key, scancode, true);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int scancode)
    {
        HandleKeyEvent(key, scancode, false);
    }

    private void HandleKeyEvent(Key key, int scancode, bool isDown)
    {
        var io = ImGui.GetIO();

        // 1. Обновляем модификаторы
        UpdateModifiers(io, key, isDown);

        // 2. Конвертируем scancode в ImGuiKey
        //    Это работает НЕЗАВИСИМО от раскладки клавиатуры!
        if (ScancodeToImGuiKey.TryGetValue(scancode, out var imguiKey))
        {
            io.AddKeyEvent(imguiKey, isDown);
        }
        // Для extended scancodes (стрелки, Delete и т.д. на полноразмерной клавиатуре)
        else if (ScancodeToImGuiKey.TryGetValue(scancode & 0x7F, out var extKey))
        {
            io.AddKeyEvent(extKey, isDown);
        }
    }

    private static void UpdateModifiers(ImGuiIOPtr io, Key key, bool isDown)
    {
        switch (key)
        {
            case Key.ControlLeft:
            case Key.ControlRight:
                io.AddKeyEvent(ImGuiKey.ModCtrl, isDown);
                break;
            case Key.ShiftLeft:
            case Key.ShiftRight:
                io.AddKeyEvent(ImGuiKey.ModShift, isDown);
                break;
            case Key.AltLeft:
            case Key.AltRight:
                io.AddKeyEvent(ImGuiKey.ModAlt, isDown);
                break;
            case Key.SuperLeft:
            case Key.SuperRight:
                io.AddKeyEvent(ImGuiKey.ModSuper, isDown);
                break;
        }
    }

    // --- RENDER LOOP ---

    private void OnRender(double delta)
    {
        while (_uiActionQueue.TryDequeue(out var action)) action();

        _controller.Update((float)delta);

        _gl.ClearColor(0.1f, 0.1f, 0.12f, 1.0f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        RenderImGui();
        _controller.Render();
    }

    private void RenderImGui()
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(_window.Size.X, _window.Size.Y));

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                 ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse;

        if (ImGui.Begin("MainInterface", flags))
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1), "LITE YT MUSIC PLAYER");
            ImGui.Separator();

            ImGui.Text("Поиск или URL:");
            ImGui.SetNextItemWidth(-1);

            bool inputEnter = ImGui.InputText("##search", ref _searchQuery, 512, ImGuiInputTextFlags.EnterReturnsTrue);

            if (ImGui.BeginPopupContextItem("InputContextMenu"))
            {
                if (ImGui.MenuItem("Копировать", "Ctrl+C", false, !string.IsNullOrEmpty(_searchQuery)))
                    ImGui.SetClipboardText(_searchQuery);

                if (ImGui.MenuItem("Вставить", "Ctrl+V"))
                    _searchQuery = ImGui.GetClipboardText();

                if (ImGui.MenuItem("Очистить"))
                    _searchQuery = "";

                ImGui.EndPopup();
            }

            if (ImGui.Button("Найти и играть") || inputEnter) StartSearch(false);
            ImGui.SameLine();
            if (ImGui.Button("В очередь")) StartSearch(true);

            ImGui.Spacing();
            ImGui.Separator();

            var current = _audio.CurrentTrack;
            if (current != null)
            {
                ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1), "Сейчас играет:");
                ImGui.TextWrapped(current.Title);
                ImGui.TextDisabled($"{current.Author} | {current.Duration:mm\\:ss}");
            }

            ImGui.Spacing();
            ImGui.Text($"Статус: {_statusMessage}");

            if (ImGui.SliderFloat("Громкость", ref _volume, 0f, 1f))
                _audio.SetVolume(_volume);

            if (ImGui.Button("Стоп")) _audio.Stop();
            ImGui.SameLine();
            if (ImGui.Button("Пропустить")) _audio.PlayNext();

            ImGui.Separator();
            ImGui.Text("Очередь:");

            ImGui.BeginChild("QueueList");
            var playlist = _audio.GetPlaylistCopy();
            if (playlist.Count == 0)
            {
                ImGui.TextDisabled("Очередь пуста");
            }
            else
            {
                int idx = 1;
                foreach (var t in playlist) ImGui.Text($"{idx++}. {t.Title}");
            }
            ImGui.EndChild();
        }
        ImGui.End();
    }

    private void StartSearch(bool enqueue)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery) || _isSearching) return;

        _isSearching = true;
        _statusMessage = "Ищу в YouTube...";

        Task.Run(async () =>
        {
            try
            {
                var track = await _youtube.SearchAndGetTrackAsync(_searchQuery);
                EnqueueUiUpdate(() =>
                {
                    if (track != null)
                    {
                        if (enqueue) _audio.Enqueue(track);
                        else _audio.PlayTrack(track);
                        _statusMessage = "Добавлено";
                    }
                    else
                    {
                        _statusMessage = "Не найдено";
                    }
                });
            }
            catch (Exception ex)
            {
                EnqueueUiUpdate(() => _statusMessage = $"Ошибка: {ex.Message}");
            }
            finally
            {
                EnqueueUiUpdate(() => _isSearching = false);
            }
        });
    }

    private void EnqueueUiUpdate(Action action) => _uiActionQueue.Enqueue(action);

    private void UpdateDiscord(TrackInfo track)
    {
        if (_discord == null || !_discord.IsInitialized) return;
        _discord.SetPresence(new RichPresence
        {
            Details = track.Title.Length > 60 ? track.Title[..57] + "..." : track.Title,
            State = "Слушает музыку",
            Assets = new Assets { LargeImageKey = "icon" }
        });
    }

    private void OnClose() => Dispose();

    public void Dispose()
    {
        _audio.Dispose();
        _discord?.Dispose();
        _controller?.Dispose();
        _inputContext?.Dispose();
        _gl?.Dispose();
        _window?.Dispose();
        GC.SuppressFinalize(this);
    }
}