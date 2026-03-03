using System.Text;
using System.Threading.Channels;

namespace LMP.Core.Logger;

/// <summary>
/// Асинхронный процессор логов с буферизацией и batch-записью.
/// Использует bounded channel для back-pressure при высокой нагрузке.
/// </summary>
public sealed class AsyncLogProcessor : IDisposable, IAsyncDisposable
{
    private readonly Channel<LogMessage> _channel;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts;
    private readonly string _logDirectory;
    private readonly string _currentLogFile;

    /// <summary>
    /// Максимальный размер очереди. При переполнении старые сообщения отбрасываются.
    /// 5000 сообщений ≈ 500KB в памяти при среднем размере 100 байт.
    /// </summary>
    private const int MAX_BUFFER_SIZE = 5000;
    
    /// <summary>
    /// Размер batch для записи. Снижает количество syscall'ов.
    /// </summary>
    private const int BATCH_SIZE = 50;

    /// <summary>
    /// Создаёт процессор логов с указанной папкой для хранения.
    /// </summary>
    /// <param name="logDirectory">
    /// Полный путь к папке логов. Папка будет создана если не существует.
    /// Если null — используется fallback в папку приложения.
    /// </param>
    public AsyncLogProcessor(string? logDirectory = null)
    {
        // Используем переданную папку или fallback
        _logDirectory = logDirectory 
            ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        
        // Создаём папку если не существует
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }

        // Имя файла с датой-временем запуска для уникальности
        string fileName = $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        _currentLogFile = Path.Combine(_logDirectory, fileName);

        var options = new BoundedChannelOptions(MAX_BUFFER_SIZE)
        {
            // При переполнении отбрасываем старые — новые важнее
            FullMode = BoundedChannelFullMode.DropOldest,
            // Один reader (worker thread) — оптимизация
            SingleReader = true,
            // Много writer'ов (разные потоки пишут логи)
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<LogMessage>(options);

        _cts = new CancellationTokenSource();
        
        // LongRunning — выделяет отдельный поток, не занимает ThreadPool
        _workerTask = Task.Factory.StartNew(
            ProcessQueueAsync, 
            _cts.Token,
            TaskCreationOptions.LongRunning, 
            TaskScheduler.Default
        ).Unwrap(); // Unwrap т.к. ProcessQueueAsync возвращает Task
    }

    /// <summary>
    /// Добавляет сообщение в очередь. Неблокирующий вызов.
    /// При переполнении очереди старые сообщения отбрасываются.
    /// </summary>
    public void Enqueue(LogMessage message)
    {
        // TryWrite никогда не блокирует — либо добавит, либо отбросит старое
        _channel.Writer.TryWrite(message);
    }

    /// <summary>
    /// Основной worker loop. Читает batch сообщений и пишет в файл.
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        // FileStream с async I/O для неблокирующей записи
        await using var fs = new FileStream(
            _currentLogFile, 
            FileMode.Append, 
            FileAccess.Write, 
            FileShare.Read,  // Позволяет читать лог другим процессам
            bufferSize: 4096, 
            useAsync: true
        );
        await using var sw = new StreamWriter(fs, Encoding.UTF8);

        // Переиспользуемый буфер для batch'ей — zero-alloc в steady state
        var buffer = new List<LogMessage>(BATCH_SIZE);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // Асинхронно ждём появления данных
                if (await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    // Собираем batch
                    while (buffer.Count < BATCH_SIZE && _channel.Reader.TryRead(out var msg))
                    {
                        buffer.Add(msg);
                    }

                    // Пишем весь batch
                    foreach (var log in buffer)
                    {
                        var line = FormatLog(log);

#if DEBUG
                        // В Debug выводим в консоль с цветами
                        WriteToConsole(log, line);
#endif
                        await sw.WriteAsync(line).ConfigureAwait(false);
                    }

                    // Flush после batch'а, не после каждой строки
                    await sw.FlushAsync().ConfigureAwait(false);
                    buffer.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                // Нормальное завершение по CancellationToken
                break;
            }
            catch (Exception ex)
            {
                // Если сломался файл лога — пишем в stderr как fallback
                Console.Error.WriteLine($"LOGGER FAILURE: {ex.Message}");
            }
        }

        // Финальный drain — записываем всё что осталось в очереди
        while (_channel.Reader.TryRead(out var msg))
        {
            var line = FormatLog(msg);
#if DEBUG
            WriteToConsole(msg, line);
#endif
            await sw.WriteLineAsync(line).ConfigureAwait(false);
        }
        
        await sw.FlushAsync().ConfigureAwait(false);
    }

#if DEBUG
    /// <summary>
    /// Цветной вывод в консоль для Debug-сборок.
    /// </summary>
    private static void WriteToConsole(LogMessage log, string formattedLine)
    {
        var originalColor = Console.ForegroundColor;

        Console.ForegroundColor = log.Level switch
        {
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Debug => ConsoleColor.Gray,
            LogLevel.Info => ConsoleColor.White,
            LogLevel.Warning => ConsoleColor.DarkYellow,
            LogLevel.Error => ConsoleColor.Red,
            LogLevel.Fatal => ConsoleColor.Magenta,
            _ => ConsoleColor.White
        };

        Console.Write(formattedLine);
        Console.ForegroundColor = originalColor;
    }
#endif

    /// <summary>
    /// Форматирует лог-сообщение в строку.
    /// Использует StringBuilder для минимизации аллокаций.
    /// </summary>
    private static string FormatLog(LogMessage log)
    {
        // Capacity hint: timestamp(12) + level(5) + brackets(6) + message(~100) + newline
        var sb = new StringBuilder(128);
        
        sb.Append('[')
          .Append(log.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"))
          .Append("] [")
          .Append(GetLevelString(log.Level))
          .Append("] ")
          .Append(log.Message);

        if (log.Exception != null)
        {
            sb.AppendLine()
              .Append("   >>> EXCEPTION: ")
              .Append(log.Exception.ToString());
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Короткие строки уровней для компактности лога.
    /// </summary>
    private static string GetLevelString(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Fatal => "FTL",
        _ => "UNK"
    };

    /// <summary>
    /// Асинхронное освобождение ресурсов. Дожидается записи всех сообщений.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Сигнализируем что больше сообщений не будет
        _channel.Writer.Complete();
        
        // Даём время worker'у завершить запись
        await _cts.CancelAsync().ConfigureAwait(false);
        
        try 
        { 
            // Ждём завершения worker task
            await _workerTask.ConfigureAwait(false); 
        } 
        catch (OperationCanceledException) 
        { 
            // Ожидаемо при отмене
        }
        
        _cts.Dispose();
    }

    /// <summary>
    /// Синхронное освобождение. Блокирует до завершения записи.
    /// </summary>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}