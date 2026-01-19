using System.Numerics;
using System.Collections.Concurrent;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using ImGuiNET;
using MyLiteMusicPlayer.Services;
using MyLiteMusicPlayer.UI.Components;
using MyLiteMusicPlayer.UI.Tabs;
using DiscordRPC;
using MyLiteMusicPlayer.Models;

namespace MyLiteMusicPlayer.UI;

public class MainWindow : IDisposable
{
    private IWindow _window = null!;
    private ImGuiController _controller = null!;
    private GL _gl = null!;
    private IInputContext _inputContext = null!;
    private DiscordRpcClient? _discord;

    // Сервисы
    private readonly GoogleAuthService _auth;
    private readonly LibraryService _library;
    private readonly YoutubeProvider _youtube;
    private readonly AudioEngine _audio;
    private readonly DownloadService _downloads;
    
    // UI компоненты
    private PlayerBar _playerBar = null!;
    private LoginButton _loginButton = null!;
    
    // Табы
    private readonly List<ITab> _tabs = new();
    private readonly Dictionary<string, ITab> _tabsById = new();
    private string _activeTabId = "home";
    private readonly List<string> _tabsToClose = new();
    
    private readonly ConcurrentQueue<Action> _uiActionQueue = new();

    // Scancode маппинг для клавиатуры
    private static readonly Dictionary<int, ImGuiKey> ScancodeToImGuiKey = new()
    {
        { 0x1E, ImGuiKey.A }, { 0x30, ImGuiKey.B }, { 0x2E, ImGuiKey.C },
        { 0x20, ImGuiKey.D }, { 0x12, ImGuiKey.E }, { 0x21, ImGuiKey.F },
        { 0x22, ImGuiKey.G }, { 0x23, ImGuiKey.H }, { 0x17, ImGuiKey.I },
        { 0x24, ImGuiKey.J }, { 0x25, ImGuiKey.K }, { 0x26, ImGuiKey.L },
        { 0x32, ImGuiKey.M }, { 0x31, ImGuiKey.N }, { 0x18, ImGuiKey.O },
        { 0x19, ImGuiKey.P }, { 0x10, ImGuiKey.Q }, { 0x13, ImGuiKey.R },
        { 0x1F, ImGuiKey.S }, { 0x14, ImGuiKey.T }, { 0x16, ImGuiKey.U },
        { 0x2F, ImGuiKey.V }, { 0x11, ImGuiKey.W }, { 0x2D, ImGuiKey.X },
        { 0x15, ImGuiKey.Y }, { 0x2C, ImGuiKey.Z },
        { 0x0B, ImGuiKey._0 }, { 0x02, ImGuiKey._1 }, { 0x03, ImGuiKey._2 },
        { 0x04, ImGuiKey._3 }, { 0x05, ImGuiKey._4 }, { 0x06, ImGuiKey._5 },
        { 0x07, ImGuiKey._6 }, { 0x08, ImGuiKey._7 }, { 0x09, ImGuiKey._8 },
        { 0x0A, ImGuiKey._9 },
        { 0x01, ImGuiKey.Escape }, { 0x0F, ImGuiKey.Tab }, { 0x39, ImGuiKey.Space },
        { 0x1C, ImGuiKey.Enter }, { 0x0E, ImGuiKey.Backspace }, { 0x53, ImGuiKey.Delete },
        { 0x48, ImGuiKey.UpArrow }, { 0x50, ImGuiKey.DownArrow },
        { 0x4B, ImGuiKey.LeftArrow }, { 0x4D, ImGuiKey.RightArrow },
    };

    public MainWindow()
    {
        // Инициализация сервисов
        _auth = new GoogleAuthService();
        _library = new LibraryService();
        _youtube = new YoutubeProvider(_auth);
        _audio = new AudioEngine(_youtube);
        _downloads = new DownloadService(_youtube, _library);

        var options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 800);
        options.Title = "YTM Player";
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
            _gl, _window, _inputContext,
            () =>
            {
                var io = ImGui.GetIO();
                FontManager.LoadCyrillicFont(io);
                ImGuiClipboardBridge.Install();
                io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            }
        );

        SetupInputHandlers();
        SetupAudioCallbacks();
        InitializeTabs();
        InitializeDiscord();
        
        _playerBar = new PlayerBar(_audio, _library);
        _loginButton = new LoginButton(_auth);

        // Инициализация YouTube provider
        Task.Run(async () =>
        {
            await _youtube.InitializeAsync();
        });
    }

    private void SetupAudioCallbacks()
    {
        _audio.OnTrackChanged += track =>
        {
            EnqueueUiUpdate(() =>
            {
                _library.AddToRecentlyPlayed(track);
                UpdateDiscord(track);
            });
        };

        _audio.OnError += error =>
        {
            EnqueueUiUpdate(() =>
            {
                Console.WriteLine($"Audio error: {error}");
            });
        };
    }

    private void InitializeTabs()
    {
        // Создаём callback для радио
        Action<TrackInfo> onStartRadio = track =>
        {
            Task.Run(async () =>
            {
                var radioTracks = await _youtube.GetRadioAsync(track);
                EnqueueUiUpdate(() =>
                {
                    if (radioTracks.Count > 0)
                    {
                        _audio.PlayTrack(radioTracks[0]);
                        _library.AddToRecentlyPlayed(radioTracks[0]);
                        
                        foreach (var t in radioTracks.Skip(1))
                            _audio.Enqueue(t);
                    }
                });
            });
        };
        
        // Основные табы
        var homeTab = new HomeTab(_library, _audio, _youtube, _auth, _downloads, onStartRadio);
        var searchTab = new SearchTab(_youtube, _audio, _library, _downloads, onStartRadio, OpenPlaylistFromUrl);
        var libraryTab = new LibraryTab(_library, _auth, _youtube, OpenPlaylistById);
        var settingsTab = new SettingsTab(_library, _auth);
        
        AddTab(homeTab);
        AddTab(searchTab);
        AddTab(libraryTab);
        AddTab(settingsTab);
        
        _activeTabId = "home";
        homeTab.OnOpen();
    }

    private void AddTab(ITab tab)
    {
        _tabs.Add(tab);
        _tabsById[tab.Id] = tab;
    }

    private void OpenPlaylistById(string playlistId)
    {
        var playlist = _library.GetPlaylist(playlistId);
        if (playlist == null) return;
        
        string tabId = $"playlist_{playlistId}";
        
        if (!_tabsById.ContainsKey(tabId))
        {
            var tab = new PlaylistTab(
                playlist, 
                _library, 
                _audio, 
                _downloads, 
                track => StartRadio(track));
            
            AddTab(tab);
            tab.OnOpen();
        }
        
        _activeTabId = tabId;
    }

    private void OpenPlaylistFromUrl(string playlistName, string youtubeUrl)
    {
        var playlist = _library.CreatePlaylist(playlistName);
        OpenPlaylistById(playlist.Id);
    }

    private void StartRadio(TrackInfo track)
    {
        Task.Run(async () =>
        {
            var radioTracks = await _youtube.GetRadioAsync(track);
            EnqueueUiUpdate(() =>
            {
                if (radioTracks.Count > 0)
                {
                    _audio.PlayTrack(radioTracks[0]);
                    _library.AddToRecentlyPlayed(radioTracks[0]);
                    
                    foreach (var t in radioTracks.Skip(1))
                        _audio.Enqueue(t);
                }
            });
        });
    }

    private void SetupInputHandlers()
    {
        if (_inputContext.Keyboards.Count == 0) return;
        
        var kb = _inputContext.Keyboards[0];
        kb.KeyDown += (_, key, scancode) => HandleKeyEvent(key, scancode, true);
        kb.KeyUp += (_, key, scancode) => HandleKeyEvent(key, scancode, false);
    }

    private void HandleKeyEvent(Key key, int scancode, bool isDown)
    {
        var io = ImGui.GetIO();
        
        // Модификаторы
        if (key == Key.ControlLeft || key == Key.ControlRight) 
            io.AddKeyEvent(ImGuiKey.ModCtrl, isDown);
        if (key == Key.ShiftLeft || key == Key.ShiftRight) 
            io.AddKeyEvent(ImGuiKey.ModShift, isDown);
        if (key == Key.AltLeft || key == Key.AltRight) 
            io.AddKeyEvent(ImGuiKey.ModAlt, isDown);
        
        // Обычные клавиши
        if (ScancodeToImGuiKey.TryGetValue(scancode, out var imguiKey))
            io.AddKeyEvent(imguiKey, isDown);
        else if (ScancodeToImGuiKey.TryGetValue(scancode & 0x7F, out var extKey))
            io.AddKeyEvent(extKey, isDown);
        
        // Глобальные хоткеи (когда нет фокуса на текстовом поле)
        if (isDown && !io.WantTextInput)
        {
            HandleGlobalHotkeys(key, io.KeyCtrl);
        }
    }

    private void HandleGlobalHotkeys(Key key, bool ctrlPressed)
    {
        switch (key)
        {
            case Key.Space when !ctrlPressed:
                _audio.TogglePlayPause();
                break;
            
            case Key.Right when ctrlPressed:
                _audio.PlayNext();
                break;
            
            case Key.Left when ctrlPressed:
                _audio.PlayPrevious();
                break;
            
            case Key.Up when ctrlPressed:
                var vol = _audio.GetVolume();
                _audio.SetVolume(Math.Min(1f, vol + 0.1f));
                _library.Data.Volume = _audio.GetVolume();
                _library.Save();
                break;
            
            case Key.Down when ctrlPressed:
                var vol2 = _audio.GetVolume();
                _audio.SetVolume(Math.Max(0f, vol2 - 0.1f));
                _library.Data.Volume = _audio.GetVolume();
                _library.Save();
                break;
        }
    }

    private void InitializeDiscord()
    {
        if (!_library.Data.DiscordRpcEnabled) return;
        
        try
        {
            _discord = new DiscordRpcClient("1462627821871562824");
            _discord.Initialize();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discord RPC init failed: {ex.Message}");
        }
    }

    private void OnRender(double delta)
    {
        // Обрабатываем очередь UI обновлений
        while (_uiActionQueue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Console.WriteLine($"UI action error: {ex.Message}"); }
        }
        
        // Обрабатываем закрытие табов
        ProcessTabClosures();

        _controller.Update((float)delta);

        _gl.ClearColor(0.08f, 0.08f, 0.1f, 1.0f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        RenderUI();
        _controller.Render();
    }

    private void ProcessTabClosures()
    {
        foreach (var tabId in _tabsToClose)
        {
            if (_tabsById.TryGetValue(tabId, out var tab))
            {
                tab.OnClose();
                _tabs.Remove(tab);
                _tabsById.Remove(tabId);
                
                if (_activeTabId == tabId)
                    _activeTabId = "home";
            }
        }
        _tabsToClose.Clear();
    }

    private void RenderUI()
    {
        var windowSize = ImGui.GetIO().DisplaySize;
        
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(windowSize.X, windowSize.Y - 90)); // Оставляем место для плеера

        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
                                 ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse |
                                 ImGuiWindowFlags.NoBringToFrontOnFocus;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.08f, 0.08f, 0.1f, 1f));
        
        if (ImGui.Begin("MainWindow", flags))
        {
            RenderTabBar();
        }
        ImGui.End();
        
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
        
        // Плеер-бар снизу
        _playerBar.Render();
    }

    private void RenderTabBar()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(15, 12));
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.1f, 0.1f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.15f, 0.15f, 0.18f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.18f, 0.22f, 1f));
        
        ImGuiTabBarFlags tabBarFlags = ImGuiTabBarFlags.Reorderable | 
                                        ImGuiTabBarFlags.AutoSelectNewTabs |
                                        ImGuiTabBarFlags.FittingPolicyScroll;
        
        if (ImGui.BeginTabBar("MainTabs", tabBarFlags))
        {
            foreach (var tab in _tabs.ToList())
            {
                RenderTab(tab);
            }
            
            ImGui.EndTabBar();
        }
        
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
    }

    private void RenderTab(ITab tab)
    {
        ImGuiTabItemFlags tabFlags = ImGuiTabItemFlags.None;
        
        if (!tab.CanClose)
            tabFlags |= ImGuiTabItemFlags.NoCloseWithMiddleMouseButton;
        
        bool open = true;
        bool selected = ImGui.BeginTabItem(tab.Name, ref open, tabFlags);
        
        // Обработка закрытия таба
        if (!open && tab.CanClose)
        {
            _tabsToClose.Add(tab.Id);
        }
        
        if (selected)
        {
            // Переключение активного таба
            if (_activeTabId != tab.Id)
            {
                if (_tabsById.TryGetValue(_activeTabId, out var oldTab))
                    oldTab.OnClose();
                
                _activeTabId = tab.Id;
                tab.OnOpen();
            }
            
            ImGui.PopStyleVar(); // FramePadding
            
            // Рендер контента таба
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(20, 15));
            
            ImGui.BeginChild($"TabContent_{tab.Id}", Vector2.Zero, ImGuiChildFlags.None);
            tab.Render();
            ImGui.EndChild();
            
            ImGui.PopStyleVar();
            
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(15, 12));
            
            ImGui.EndTabItem();
        }
    }

    private void EnqueueUiUpdate(Action action) => _uiActionQueue.Enqueue(action);

    private void UpdateDiscord(TrackInfo track)
    {
        if (_discord == null || !_discord.IsInitialized) return;
        if (!_library.Data.DiscordRpcEnabled) return;
        
        try
        {
            _discord.SetPresence(new RichPresence
            {
                Details = TruncateForDiscord(track.Title, 60),
                State = $"by {TruncateForDiscord(track.Author, 40)}",
                Assets = new Assets 
                { 
                    LargeImageKey = "icon",
                    LargeImageText = "YTM Player"
                },
                Timestamps = new Timestamps
                {
                    Start = DateTime.UtcNow
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Discord RPC update failed: {ex.Message}");
        }
    }

    private static string TruncateForDiscord(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "Unknown";
        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }

    private void OnClose()
    {
        Dispose();
    }

    public void Dispose()
    {
        // Сохраняем данные
        _library.Save();
        
        // Освобождаем ресурсы
        _audio.Dispose();
        _auth.Dispose();
        _discord?.Dispose();
        _controller?.Dispose();
        _inputContext?.Dispose();
        _gl?.Dispose();
        _window?.Dispose();
        
        GC.SuppressFinalize(this);
    }
}