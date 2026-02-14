using LMP.Core.Audio.Interfaces;
using NAudio.Wave;

namespace LMP.Core.Audio.Backends;

/// <summary>
/// Бэкенд воспроизведения на базе NAudio (Windows/WaveOut).
/// Обеспечивает буферизацию и взаимодействие с аудио-драйвером.
/// </summary>
public sealed class NAudioBackend : IPlaybackBackend
{
    // Размер внутреннего буфера NAudio. 
    // 1 секунда обеспечивает баланс между стабильностью (нет заиканий) и отзывчивостью (Position).
    private const int InternalBufferSeconds = 1; 
    private const int DesiredLatencyMs = 150;
    
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private AudioDataCallback? _callback;
    
    private int _channels;
    private float[]? _floatBuffer;
    private byte[]? _byteBuffer;
    
    private volatile bool _playing;
    private volatile bool _disposed;
    
    private Task? _fillTask;
    private CancellationTokenSource? _cts;
    
    public string Name => "NAudio";
    
    /// <inheritdoc />
    public float Volume { get; set; } = 1.0f;
    
    /// <inheritdoc />
    public bool IsPlaying => _playing;
    
    /// <inheritdoc />
    // Возвращаем количество семплов (float), которые находятся в буфере драйвера, но еще не сыграны.
    public int BufferedSamples => _provider != null 
        ? _provider.BufferedBytes / sizeof(float) 
        : 0;
    
    public void Initialize(int sampleRate, int channels, AudioDataCallback dataCallback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        _callback = dataCallback ?? throw new ArgumentNullException(nameof(dataCallback));
        _channels = channels;
        
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        
        _provider = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(InternalBufferSeconds),
            DiscardOnBufferOverflow = true // Защита от переполнения
        };
        
        _waveOut = new WaveOutEvent
        {
            DesiredLatency = DesiredLatencyMs,
            NumberOfBuffers = 2 // Double buffering
        };
        
        _waveOut.Init(_provider);
        _waveOut.Volume = Volume;
        
        // Буфер для чтения из AudioPlayer (50мс за раз)
        int samplesPerRead = sampleRate * channels / 20; 
        _floatBuffer = new float[samplesPerRead];
        _byteBuffer = new byte[samplesPerRead * sizeof(float)];
        
        _cts = new CancellationTokenSource();
        // Запускаем цикл заполнения буфера
        _fillTask = Task.Factory.StartNew(() => FillBufferLoopAsync(_cts.Token), 
            TaskCreationOptions.LongRunning);
        
        Log.Info($"[NAudioBackend] Initialized: {sampleRate}Hz, {channels}ch");
    }
    
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waveOut == null || _playing) return;
        
        _waveOut.Play();
        _playing = true;
    }
    
    public void Stop()
    {
        if (_waveOut == null || !_playing) return;
        
        _waveOut.Pause();
        _playing = false;
    }
    
    private async Task FillBufferLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (_provider == null || _callback == null || _floatBuffer == null || _byteBuffer == null) 
                    break;

                // Если буфер драйвера почти полон (>80%), ждем.
                // Это предотвращает высокий CPU usage при spin-wait.
                if (_provider.BufferedDuration.TotalSeconds > InternalBufferSeconds * 0.8)
                {
                    await Task.Delay(20, ct);
                    continue;
                }
                
                // Запрашиваем данные у AudioPlayer через callback
                int framesRead = _callback(_floatBuffer);
                
                if (framesRead > 0)
                {
                    // Конвертация float[] -> byte[] (BlockCopy очень быстр)
                    int totalSamples = framesRead * _channels;
                    int bytes = totalSamples * sizeof(float);
                    Buffer.BlockCopy(_floatBuffer, 0, _byteBuffer, 0, bytes);
                    
                    // Отправка в NAudio
                    _provider.AddSamples(_byteBuffer, 0, bytes);
                }
                else
                {
                    // Если данных нет (пауза или буферизация), ждем немного
                    await Task.Delay(10, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"[NAudioBackend] Loop error: {ex.Message}");
                await Task.Delay(100, ct); // Anti-spam delay
            }
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _playing = false;
        _cts?.Cancel();
        
        // Корректное ожидание завершения задачи заполнения
        if (_fillTask != null)
        {
            try
            {
                _fillTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch { /* Игнорируем ошибки при остановке */ }
        }
        
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        
        _provider = null;
        _floatBuffer = null;
        _byteBuffer = null;
        
        _cts?.Dispose();
        _cts = null;
        
        Log.Debug("[NAudioBackend] Disposed");
    }
}