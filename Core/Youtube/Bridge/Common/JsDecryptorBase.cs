// Core/Youtube/Bridge/Common/JsDecryptorBase.cs

using System.Text;
using Jint;

namespace LMP.Core.Youtube.Bridge.Common;

public abstract class JsDecryptorBase<T> : IYoutubeDecryptor, IDisposable
{
    protected readonly PlayerContextManager PlayerManager;
    protected readonly DecryptorCache<string, string> Cache;
    
    private readonly Lock _initLock = new();
    
    protected Engine? BundleEngine;
    protected string? BundleFuncName;
    protected Engine? FullEngine;
    protected string? FullFuncName;
    
    protected string? CurrentPlayerVersion;
    protected volatile bool IsInitialized;
    
    protected JsDecryptorBase(
        PlayerContextManager playerManager,
        string cacheFilePath,
        int maxMemory,
        int maxDisk)
    {
        PlayerManager = playerManager;
        Cache = new DecryptorCache<string, string>(cacheFilePath, maxMemory, maxDisk);
    }
    
    protected async ValueTask EnsureInitializedAsync(CancellationToken ct)
    {
        if (IsInitialized) return;
        
        lock (_initLock)
        {
            if (IsInitialized) return;
        }

        var context = await PlayerManager.GetOrLoadAsync(ct);
        
        lock (_initLock)
        {
            if (IsInitialized) return;
            
            CurrentPlayerVersion = context.Version;
            Cache.LoadAsync(context.Version).Wait(ct);

            try
            {
                InitializeCore(context);
                IsInitialized = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[{typeof(T).Name}] Initialization failed: {ex.Message}");
            }
        }
    }

    protected abstract void InitializeCore(PlayerContext context);

    protected bool TryInitBundle(string bundle, string funcName, string wrapperScript, string finalFuncName, string testToken)
    {
        // 1. ГАРАНТИРОВАННО СОХРАНЯЕМ БАНДЛ ДО ЕГО ИСПОЛНЕНИЯ
        SaveScript(CurrentPlayerVersion!, "bundle", bundle, funcName);

        try
        {
            BundleEngine = new Engine(opt => opt
                .TimeoutInterval(TimeSpan.FromSeconds(15))
                .LimitRecursion(200)
                .MaxStatements(2_000_000));
            
            BundleEngine.Execute(BrowserStubs);
            BundleEngine.Execute(bundle);
            BundleEngine.Execute(wrapperScript);
            
            var result = BundleEngine.Invoke(finalFuncName, testToken).AsString();
            if (string.IsNullOrEmpty(result) || result == testToken)
            {
                BundleEngine = null;
                return false;
            }
            
            BundleFuncName = finalFuncName;
            Cache.Set(testToken, result);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[{typeof(T).Name}] Bundle init failed: {ex.Message}");
            SaveError(CurrentPlayerVersion, "bundle", ex.ToString());
            BundleEngine?.Dispose();
            BundleEngine = null;
            return false;
        }
    }

    protected bool TryInitFull(string baseJs, string wrapperScript, string finalFuncName, string testToken)
    {
        try
        {
            FullEngine = new Engine(opt => opt
                .TimeoutInterval(TimeSpan.FromSeconds(30))
                .LimitRecursion(200)
                .MaxStatements(5_000_000));
            
            FullEngine.Execute(BrowserStubs);
            FullEngine.Execute(baseJs);
            FullEngine.Execute(wrapperScript);
            
            var result = FullEngine.Invoke(finalFuncName, testToken).AsString();
            if (string.IsNullOrEmpty(result) || result == testToken)
            {
                FullEngine = null;
                return false;
            }
            
            FullFuncName = finalFuncName;
            Cache.Set(testToken, result);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"[{typeof(T).Name}] Full JS init failed: {ex.Message}");
            SaveError(CurrentPlayerVersion, "full", ex.ToString());
            FullEngine?.Dispose();
            FullEngine = null;
            return false;
        }
    }

    protected void SaveScript(string version, string type, string code, string funcName)
    {
        try
        {
            // Берем папку из кэш-пути конкретного дешифратора, чтобы NToken сохранял к себе, а SigCipher - к себе
            var folder = Path.GetDirectoryName(Cache.GetType().GetField("_diskPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(Cache)?.ToString() ?? Globals.File.SigCipherCache)!;
            Directory.CreateDirectory(folder);
            
            var sb = new StringBuilder();
            sb.AppendLine("// ═══════════════════════════════════════════════════════════");
            sb.AppendLine($"// {typeof(T).Name.ToUpper()} {type.ToUpper()} — AUTO-GENERATED");
            sb.AppendLine($"// Player version: {version}");
            sb.AppendLine($"// Entry function: {funcName}");
            sb.AppendLine($"// Generated: {DateTime.UtcNow:O}");
            sb.AppendLine("// ═══════════════════════════════════════════════════════════\n");
            sb.AppendLine(code);
            
            var path = Path.Combine(folder, $"player_{version}_{type}.js");
            File.WriteAllText(path, sb.ToString());
            
            Log.Debug($"[{typeof(T).Name}] Script saved: {path}");
        }
        catch { /* ignore */ }
    }

    protected void SaveError(string? version, string type, string error)
    {
        if (version is null) return;
        try
        {
            var folder = Path.GetDirectoryName(Cache.GetType().GetField("_diskPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(Cache)?.ToString() ?? Globals.File.SigCipherCache)!;
            Directory.CreateDirectory(folder);
            
            var path = Path.Combine(folder, $"player_{version}_{type}_error.txt");
            
            var content = $"""
                === {typeof(T).Name.ToUpper()} {type.ToUpper()} ERROR ===
                Player version: {version}
                Timestamp: {DateTime.UtcNow:O}
                
                {error}
                """;
            
            File.WriteAllText(path, content);
        }
        catch { /* ignore */ }
    }
    
    protected static string Truncate(string s, int len = 20) =>
        s.Length <= len ? s : string.Concat(s.AsSpan(0, len), "...");

    public void FlushCache() => Cache.SaveAsync().Wait();

    public void InvalidateCache()
    {
        Cache.Clear();
        BundleEngine?.Dispose();
        BundleEngine = null;
        FullEngine?.Dispose();
        FullEngine = null;
        IsInitialized = false;
        Log.Info($"[{typeof(T).Name}] Cache invalidated");
    }

    public virtual void Dispose()
    {
        FlushCache();
        BundleEngine?.Dispose();
        FullEngine?.Dispose();
        GC.SuppressFinalize(this);
    }

    private const string BrowserStubs = """
        var _sink = new Proxy(function(){return _sink;}, {
            get: function(t,p) { return p === 'then' ? void 0 : _sink; },
            set: function() { return true; },
            apply: function() { return _sink; }
        });
        var window = this; window.self = window.top = window.globalThis = window;
        var location = { href: 'https://www.youtube.com/', hostname: 'www.youtube.com', protocol: 'https:' };
        window.location = location;
        var document = { 
            createElement: function() { return _sink; }, 
            getElementById: function() { return null; },
            querySelector: function() { return null; },
            querySelectorAll: function() { return []; },
            readyState: 'complete'
        };
        window.document = document;
        var navigator = { userAgent: '', platform: '' }; window.navigator = navigator;
        window.setTimeout = window.setInterval = function(f) { try { if (typeof f === 'function') f(); } catch(e) {} return 0; };
        window.clearTimeout = window.clearInterval = function() {};
        var XMLHttpRequest = function() {
            this.open = this.send = this.setRequestHeader = function() {};
            this.readyState = 4; this.status = 200; this.responseText = '';
        };
        var fetch = function() { return Promise.resolve({ ok: true, text: function() { return Promise.resolve(''); } }); };
        var ytcfg = { get: function() { return ''; }, set: function() {}, d: function() { return ''; } };
        var yt = { config_: {} };
        """;
}