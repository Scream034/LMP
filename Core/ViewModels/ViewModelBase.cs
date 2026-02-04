using System.Reactive.Disposables;
using LMP.Core.Services;
using ReactiveUI;

namespace LMP.Core.ViewModels;

public abstract class ViewModelBase : ReactiveObject, IDisposable
{
    // Статическое для кода
    public static LocalizationService SL => LocalizationService.Instance;
    
    // Нестатическое для XAML биндинга (через DataContext)
    public LocalizationService L => LocalizationService.Instance;

    /// <summary>
    /// Контейнер для всех подписок и IDisposable объектов.
    /// Автоматически очищается при вызове Dispose().
    /// </summary>
    protected CompositeDisposable Disposables { get; } = [];

    private bool _isDisposed;

    /// <summary>
    /// Универсальный helper-метод для создания ReactiveCommand.
    /// Гарантирует подписку на ThrownExceptions для предотвращения утечек памяти
    /// и регистрирует команду для автоматической очистки.
    /// </summary>
    /// <remarks>
    /// ThrownExceptions subscription to prevent memory leak.
    /// ReactiveCommand внутри использует ScheduledSubject для ThrownExceptions.
    /// Если на него никто не подписан, исключения накапливаются в ConcurrentQueue,
    /// которая держит ссылку на команду, создавая циклическую зависимость.
    /// </remarks>
    protected TCommand CreateCommand<TCommand>(TCommand command) where TCommand : IReactiveCommand
    {
        // 1. Подписываемся на ошибки, чтобы очистить внутреннюю очередь исключений
        command.ThrownExceptions
            .Subscribe(ex => Log.Error($"[{GetType().Name}] Command error: {ex.Message}"))
            .DisposeWith(Disposables);
        
        // 2. Если команда реализует IDisposable, добавляем её в общий список очистки
        if (command is IDisposable disposable)
        {
            disposable.DisposeWith(Disposables);
        }
        
        return command;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed) return;
        if (disposing)
        {
            // Очищаем все подписки и команды, зарегистрированные в этом ViewModel
            Disposables.Dispose();
        }
        _isDisposed = true;
    }
}