using System.Runtime.InteropServices;
using LMP.Core.Audio.Interfaces;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Бэкенд воспроизведения на базе miniaudio через P/Invoke.
/// </summary>
/// <remarks>
/// Требует нативные библиотеки miniaudio:
/// - Windows: miniaudio.dll
/// - Linux: libminiaudio.so
/// - macOS: libminiaudio.dylib
/// </remarks>
public sealed unsafe partial class MiniaudioBackend : IPlaybackBackend
{
    #region Native Bindings
    
    private const string LibName = "miniaudio";
    
    [StructLayout(LayoutKind.Sequential)]
    private struct MaDeviceConfig
    {
        public int DeviceType;
        public uint SampleRate;
        public uint Channels;
        public int Format;
        public uint PeriodSizeInFrames;
        public uint Periods;
        public IntPtr DataCallback;
        public IntPtr UserData;
    }
    
    [StructLayout(LayoutKind.Sequential, Size = 8192)] // Примерный размер
    private struct MaDevice
    {
        // Opaque структура, размер зависит от платформы
        public fixed byte Data[8192];
    }
    
    private delegate void MaDeviceDataCallback(
        MaDevice* device, 
        void* output, 
        void* input, 
        uint frameCount);
    
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int ma_device_config_init(int deviceType);
    
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int ma_device_init(
        IntPtr context, 
        MaDeviceConfig* config, 
        MaDevice* device);
    
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int ma_device_start(MaDevice* device);
    
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int ma_device_stop(MaDevice* device);
    
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial void ma_device_uninit(MaDevice* device);
    
    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial int ma_device_set_master_volume(MaDevice* device, float volume);
    
    private const int MA_DEVICE_TYPE_PLAYBACK = 1;
    private const int MA_FORMAT_F32 = 5;
    
    #endregion
    
    private MaDevice* _device;
    private GCHandle _callbackHandle;
    private MaDeviceDataCallback? _nativeCallback;
    private AudioDataCallback? _dataCallback;
    
    private volatile float _volume = 1.0f;
    private volatile bool _isPlaying;
    private volatile bool _initialized;
    private bool _disposed;
    
    private int _sampleRate;
    private int _channels;
    
    /// <inheritdoc/>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (_initialized && _device != null)
            {
                ma_device_set_master_volume(_device, _volume);
            }
        }
    }
    
    /// <inheritdoc/>
    public bool IsPlaying => _isPlaying;
    
    /// <inheritdoc/>
    public int BufferedSamples => 0; // TODO: Получать из miniaudio
    
    /// <inheritdoc/>
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (_initialized)
        {
            throw new InvalidOperationException("Backend already initialized");
        }
        
        _sampleRate = sampleRate;
        _channels = channels;
        _dataCallback = dataCallback;
        
        try
        {
            _device = (MaDevice*)NativeMemory.AllocZeroed((nuint)sizeof(MaDevice));
            
            // Создаём делегат и закрепляем его в памяти
            _nativeCallback = NativeDataCallback;
            _callbackHandle = GCHandle.Alloc(_nativeCallback);
            
            var config = new MaDeviceConfig
            {
                DeviceType = MA_DEVICE_TYPE_PLAYBACK,
                SampleRate = (uint)sampleRate,
                Channels = (uint)channels,
                Format = MA_FORMAT_F32,
                PeriodSizeInFrames = 1024, // ~21ms при 48kHz
                Periods = 3, // Triple buffering
                DataCallback = Marshal.GetFunctionPointerForDelegate(_nativeCallback),
                UserData = IntPtr.Zero
            };
            
            int result = ma_device_init(IntPtr.Zero, &config, _device);
            if (result != 0)
            {
                throw new InvalidOperationException($"Failed to initialize miniaudio device: {result}");
            }
            
            _initialized = true;
            Log.Info($"Miniaudio backend initialized: {sampleRate}Hz, {channels}ch");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }
    
    /// <inheritdoc/>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (!_initialized || _device == null)
        {
            throw new InvalidOperationException("Backend not initialized");
        }
        
        if (_isPlaying) return;
        
        int result = ma_device_start(_device);
        if (result != 0)
        {
            throw new InvalidOperationException($"Failed to start playback: {result}");
        }
        
        _isPlaying = true;
        Log.Debug("Playback started");
    }
    
    /// <inheritdoc/>
    public void Stop()
    {
        if (!_initialized || _device == null || !_isPlaying)
        {
            return;
        }
        
        ma_device_stop(_device);
        _isPlaying = false;
        Log.Debug("Playback stopped");
    }
    
    private void NativeDataCallback(MaDevice* device, void* output, void* input, uint frameCount)
    {
        if (_dataCallback == null || output == null)
        {
            return;
        }
        
        try
        {
            int totalSamples = (int)frameCount * _channels;
            var buffer = new Span<float>(output, totalSamples);
            
            int samplesWritten = _dataCallback(buffer);
            
            // Заполняем остаток тишиной
            if (samplesWritten < (int)frameCount)
            {
                int filledSamples = samplesWritten * _channels;
                buffer[filledSamples..].Clear();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Error in audio callback", ex);
            // Заполняем тишиной при ошибке
            new Span<float>(output, (int)frameCount * _channels).Clear();
        }
    }
    
    private void Cleanup()
    {
        if (_device != null)
        {
            if (_initialized)
            {
                _ = ma_device_stop(_device);
                ma_device_uninit(_device);
            }
            NativeMemory.Free(_device);
            _device = null;
        }
        
        if (_callbackHandle.IsAllocated)
        {
            _callbackHandle.Free();
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _isPlaying = false;
        Cleanup();
        
        Log.Debug("Miniaudio backend disposed");
    }
}