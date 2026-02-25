using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace LMP.Core.Models;

/// <summary>
/// Bitmap с автоматическим подсчётом ссылок.
/// Dispose вызывается только когда refCount достигает 0.
/// Thread-safe.
/// </summary>
public sealed class RefCountedBitmap : IDisposable
{
    private int _refCount;
    private bool _isDisposed;
    private readonly Lock _lock = new();

    public Bitmap Bitmap { get; }
    public int RefCount => Volatile.Read(ref _refCount);
    public long EstimatedBytes { get; }
    public DateTime CachedAt { get; }

    public RefCountedBitmap(Bitmap bitmap)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        _refCount = 1; // Начинаем с 1 (cache держит ссылку)
        
        long pixelCount = (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
        EstimatedBytes = pixelCount * 4; // RGBA
        CachedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Увеличивает счётчик ссылок.
    /// Возвращает true если успешно, false если объект уже disposed.
    /// </summary>
    public bool AddRef()
    {
        lock (_lock)
        {
            if (_isDisposed) return false;
            
            Interlocked.Increment(ref _refCount);
            return true;
        }
    }

    /// <summary>
    /// Уменьшает счётчик ссылок.
    /// Вызывает Dispose если счётчик достиг 0.
    /// </summary>
    public void Release()
    {
        lock (_lock)
        {
            if (_isDisposed) return;

            var newCount = Interlocked.Decrement(ref _refCount);

            Debug.Assert(newCount >= 0, "RefCount не может быть отрицательным!");

            if (newCount == 0)
            {
                DisposeInternal();
            }
        }
    }

    private void DisposeInternal()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            Bitmap.Dispose();
            Log.Trace($"[RefCountedBitmap] Disposed bitmap {Bitmap.PixelSize}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[RefCountedBitmap] Dispose failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // При явном Dispose сбрасываем refCount в 0
        lock (_lock)
        {
            _refCount = 0;
            DisposeInternal();
        }
    }
}