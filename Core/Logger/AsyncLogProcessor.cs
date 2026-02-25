using System.Text;
using System.Threading.Channels;

namespace LMP.Core.Logger;

public sealed class AsyncLogProcessor : IDisposable, IAsyncDisposable
{
    private readonly Channel<LogMessage> _channel;
    private readonly Task _workerTask;
    private readonly CancellationTokenSource _cts;
    private readonly string _logDirectory;
    private readonly string _currentLogFile;

    private const int MAX_BUFFER_SIZE = 5000;
    private const int BATCH_SIZE = 50;

    public AsyncLogProcessor()
    {
        _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        if (!Directory.Exists(_logDirectory))
            Directory.CreateDirectory(_logDirectory);

        // Используем дату запуска для имени файла
        string fileName = $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        _currentLogFile = Path.Combine(_logDirectory, fileName);

        var options = new BoundedChannelOptions(MAX_BUFFER_SIZE)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        };
        _channel = Channel.CreateBounded<LogMessage>(options);

        _cts = new CancellationTokenSource();
        _workerTask = Task.Factory.StartNew(ProcessQueueAsync, TaskCreationOptions.LongRunning);
    }

    public void Enqueue(LogMessage message)
    {
        _channel.Writer.TryWrite(message);
    }

    private async Task ProcessQueueAsync()
    {
        using var fs = new FileStream(_currentLogFile, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true);
        using var sw = new StreamWriter(fs, Encoding.UTF8);

        var buffer = new List<LogMessage>(BATCH_SIZE);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (await _channel.Reader.WaitToReadAsync(_cts.Token))
                {
                    while (buffer.Count < BATCH_SIZE && _channel.Reader.TryRead(out var msg))
                    {
                        buffer.Add(msg);
                    }

                    foreach (var log in buffer)
                    {
                        var line = FormatLog(log);

                        // --- НАЧАЛО ИЗМЕНЕНИЙ: Вывод в консоль только для DEBUG ---
#if DEBUG
                        WriteToConsole(log, line);
#endif
                        // --- КОНЕЦ ИЗМЕНЕНИЙ ---

                        await sw.WriteAsync(line);
                    }

                    await sw.FlushAsync();
                    buffer.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Если сломался файл лога, пишем в stderr, чтобы хоть где-то увидеть
                Console.Error.WriteLine($"LOGGER FAILURE: {ex.Message}");
            }
        }

        while (_channel.Reader.TryRead(out var msg))
        {
            var line = FormatLog(msg);
#if DEBUG
            WriteToConsole(msg, line);
#endif
            await sw.WriteLineAsync(line);
        }
    }

    // --- Метод для красивого цветного вывода ---
#if DEBUG
    private static void WriteToConsole(LogMessage log, string formattedLine)
    {
        // Меняем цвет в зависимости от уровня
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

        // Пишем в консоль (без блокировки асинхронности файла, т.к. Console быстрый для Dev)
        Console.Write(formattedLine);

        // Возвращаем цвет обратно
        Console.ForegroundColor = originalColor;
    }
#endif

    private static string FormatLog(LogMessage log)
    {
        // Оптимизированное форматирование
        var sb = new StringBuilder();
        sb.Append('[').Append(log.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")).Append("] ");
        sb.Append('[').Append(GetLevelString(log.Level)).Append("] ");

        sb.Append(log.Message);

        if (log.Exception != null)
        {
            sb.AppendLine();
            sb.Append("   >>> EXCEPTION: ").Append(log.Exception.ToString());
        }

        sb.AppendLine();
        return sb.ToString();
    }

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

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        try { await _workerTask; } catch { }
        _cts.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().Wait();
    }
}