using System.Collections.ObjectModel;
using System.Reactive;
using System.Text;
using Avalonia.Threading;
using LMP.Core.ViewModels;
using LMP.Tests.Framework;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;

namespace LMP.UI.Features.Debug;

/// <summary>
/// ViewModel для вкладки "Tests" в Debug-окне.
/// <para>
/// Возможности:
/// <list type="bullet">
///   <item>Автоматический discovery тестов через рефлексию</item>
///   <item>Фильтрация по категории (Unit/Integration/Benchmark)</item>
///   <item>Фильтрация по тематической группе (NToken/SigCipher/Pipeline/Cache/Solver)</item>
///   <item>Полнотекстовый поиск по имени теста</item>
///   <item>Запуск одного теста, группы, или всех</item>
///   <item>Редактирование test-config.json без перекомпиляции</item>
/// </list>
/// </para>
/// </summary>
public sealed class TestRunnerViewModel : ViewModelBase
{
    private readonly TestRunner _runner;
    private CancellationTokenSource? _runCts;

    // ═══════════════════════════════════════════════════════════════
    // PROPERTIES
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Все обнаруженные тесты.</summary>
    public ObservableCollection<TestItemViewModel> AllTests { get; } = [];

    /// <summary>Отфильтрованные тесты для отображения.</summary>
    [Reactive] public ObservableCollection<TestItemViewModel> FilteredTests { get; set; } = [];

    /// <summary>Выбранный фильтр категории (null = все).</summary>
    [Reactive] public TestCategory? SelectedCategory { get; set; }

    /// <summary>Выбранный фильтр тематической группы (null = все).</summary>
    [Reactive] public string? SelectedGroup { get; set; }

    /// <summary>Текст поиска по имени теста.</summary>
    [Reactive] public string SearchFilter { get; set; } = "";

    /// <summary>Идёт ли запуск тестов.</summary>
    [Reactive] public bool IsRunning { get; set; }

    /// <summary>Суммарная статистика.</summary>
    [Reactive] public string Summary { get; set; } = "";

    /// <summary>Лог выполнения.</summary>
    [Reactive] public string LogOutput { get; set; } = "";

    /// <summary>Прогресс batch-запуска (0-100).</summary>
    [Reactive] public int Progress { get; set; }

    /// <summary>
    /// Все доступные тематические группы для UI-кнопок фильтра.
    /// Заполняется при discovery.
    /// </summary>
    public ObservableCollection<GroupFilterItem> AvailableGroups { get; } = [];

    // ═══════════════════════════════════════════════════════════════
    // COMMANDS
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Запустить ВСЕ тесты.</summary>
    public ReactiveCommand<Unit, Unit> RunAllCommand { get; }

    /// <summary>Запустить только Unit.</summary>
    public ReactiveCommand<Unit, Unit> RunUnitCommand { get; }

    /// <summary>Запустить только Integration.</summary>
    public ReactiveCommand<Unit, Unit> RunIntegrationCommand { get; }

    /// <summary>Запустить только Benchmarks.</summary>
    public ReactiveCommand<Unit, Unit> RunBenchmarksCommand { get; }

    /// <summary>Запустить отфильтрованные тесты (по текущему фильтру группы/категории).</summary>
    public ReactiveCommand<Unit, Unit> RunFilteredCommand { get; }

    /// <summary>Запустить один тест по клику.</summary>
    public ReactiveCommand<TestItemViewModel, Unit> RunSingleCommand { get; }

    /// <summary>Переключить фильтр группы (toggle).</summary>
    public ReactiveCommand<string?, Unit> ToggleGroupFilterCommand { get; }

    /// <summary>Отменить текущий запуск.</summary>
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Сбросить все результаты.</summary>
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }

    /// <summary>Очистить лог.</summary>
    public ReactiveCommand<Unit, Unit> ClearLogCommand { get; }

    /// <summary>Открыть test-config.json в редакторе по умолчанию.</summary>
    public ReactiveCommand<Unit, Unit> OpenConfigCommand { get; }

    /// <summary>Перезагрузить test-config.json из файла.</summary>
    public ReactiveCommand<Unit, Unit> ReloadConfigCommand { get; }

    // ═══════════════════════════════════════════════════════════════
    // CTOR
    // ═══════════════════════════════════════════════════════════════

    public TestRunnerViewModel()
    {
        _runner = new TestRunner(AppEntry.Services);
        _runner.TestStarting += OnTestStarting;
        _runner.TestCompleted += OnTestCompleted;

        var canRun = this.WhenAnyValue(x => x.IsRunning, running => !running);

        RunAllCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => RunBatchByCategoryAsync(null), canRun));

        RunUnitCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => RunBatchByCategoryAsync(TestCategory.Unit), canRun));

        RunIntegrationCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => RunBatchByCategoryAsync(TestCategory.Integration), canRun));

        RunBenchmarksCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            () => RunBatchByCategoryAsync(TestCategory.Benchmark), canRun));

        RunFilteredCommand = CreateCommand(ReactiveCommand.CreateFromTask(
            RunFilteredAsync, canRun));

        RunSingleCommand = CreateCommand(ReactiveCommand.CreateFromTask<TestItemViewModel>(
            RunSingleAsync, canRun));

        ToggleGroupFilterCommand = CreateCommand(ReactiveCommand.Create<string?>(group =>
        {
            // Toggle: если та же группа — сбрасываем, иначе ставим
            SelectedGroup = SelectedGroup == group ? null : group;
        }));

        CancelCommand = CreateCommand(ReactiveCommand.Create(
            () => _runCts?.Cancel(),
            this.WhenAnyValue(x => x.IsRunning)));

        ResetCommand = CreateCommand(ReactiveCommand.Create(ResetAll));
        ClearLogCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            _logBuilder.Clear();
            LogOutput = "";
        }));

        // ═══ CONFIG COMMANDS ═══
        OpenConfigCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            TestConfig.OpenInEditor();
            AppendLog($"Opened config: {TestConfig.GetConfigPath()}");
        }));

        ReloadConfigCommand = CreateCommand(ReactiveCommand.Create(() =>
        {
            TestConfig.Reload();
            AppendLog("Reloaded test-config.json from disk.");
        }));

        // Реагируем на все три фильтра
        this.WhenAnyValue(x => x.SelectedCategory, x => x.SelectedGroup, x => x.SearchFilter)
            .Subscribe(_ => ApplyFilter());

        DiscoverTests();
        UpdateSummary();
    }

    // ═══════════════════════════════════════════════════════════════
    // DISCOVERY
    // ═══════════════════════════════════════════════════════════════

    private void DiscoverTests()
    {
        var ordered = TestDiscovery.GetAllOrdered();

        foreach (var descriptor in ordered)
            AllTests.Add(new TestItemViewModel(descriptor));

        // Собираем уникальные группы для кнопок-фильтров
        var groups = TestDiscovery.GetAllGroups();
        foreach (var group in groups.OrderBy(g => g, StringComparer.Ordinal))
        {
            var count = ordered.Count(t => t.Group == group);
            AvailableGroups.Add(new GroupFilterItem(group, count));
        }

        ApplyFilter();
        AppendLog($"Discovered {AllTests.Count} tests in {groups.Count} groups");
        AppendLog($"Config: {TestConfig.GetConfigPath()}");
    }

    // ═══════════════════════════════════════════════════════════════
    // RUNNING
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Запускает тесты по категории (null = все).</summary>
    private async Task RunBatchByCategoryAsync(TestCategory? category)
    {
        var tests = category is not null
            ? AllTests.Where(t => t.Descriptor.Category == category.Value).ToList()
            : AllTests.ToList();

        await RunBatchCoreAsync(tests, category?.ToString() ?? "All");
    }

    /// <summary>Запускает тесты по текущему фильтру (группа + категория + поиск).</summary>
    private async Task RunFilteredAsync()
    {
        var tests = FilteredTests.ToList();
        var label = BuildFilterLabel();
        await RunBatchCoreAsync(tests, label);
    }

    /// <summary>Общая логика запуска batch.</summary>
    private async Task RunBatchCoreAsync(IReadOnlyList<TestItemViewModel> tests, string label)
    {
        if (tests.Count == 0)
        {
            AppendLog("No tests to run.");
            return;
        }

        IsRunning = true;
        Progress = 0;
        _runCts = new CancellationTokenSource();

        AppendLog($"\n{'═'.Repeat(60)}");
        AppendLog($"  Running {tests.Count} tests [{label}]...");
        AppendLog($"{'═'.Repeat(60)}\n");

        try
        {
            var descriptors = tests.Select(t => t.Descriptor).ToList();
            int completed = 0;

            void OnProgress(TestDescriptor _, TestResult __)
            {
                var pct = (int)(Interlocked.Increment(ref completed) * 100.0 / descriptors.Count);
                Dispatcher.UIThread.Post(() => Progress = pct);
            }

            _runner.TestCompleted += OnProgress;
            try
            {
                await Task.Run(() => _runner.RunBatchAsync(descriptors, _runCts.Token));
            }
            finally
            {
                _runner.TestCompleted -= OnProgress;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Batch error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            Progress = 100;
            UpdateSummary();

            AppendLog($"\n{'═'.Repeat(60)}");
            AppendLog($"  {Summary}");
            AppendLog($"{'═'.Repeat(60)}\n");
        }
    }

    /// <summary>Запускает один тест по клику.</summary>
    private async Task RunSingleAsync(TestItemViewModel item)
    {
        IsRunning = true;
        _runCts = new CancellationTokenSource();

        AppendLog($"\n▶ Running: {item.DisplayName}...");

        try
        {
            await Task.Run(() => _runner.RunAsync(item.Descriptor, _runCts.Token));
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            UpdateSummary();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // EVENT HANDLERS (background thread → UI thread)
    // ═══════════════════════════════════════════════════════════════

    private void OnTestStarting(TestDescriptor descriptor)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = AllTests.FirstOrDefault(t => t.Descriptor.Id == descriptor.Id);
            if (item is not null)
            {
                item.State = TestRunState.Running;
                item.Duration = "";
                item.ErrorMessage = null;
            }
        });
    }

    private void OnTestCompleted(TestDescriptor descriptor, TestResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var item = AllTests.FirstOrDefault(t => t.Descriptor.Id == descriptor.Id);
            if (item is not null)
            {
                item.State = result.State;
                item.Duration = result.DurationFormatted;
                item.ErrorMessage = result.ErrorMessage;
            }

            var icon = result.State switch
            {
                TestRunState.Passed => "✓",
                TestRunState.Failed => "✗",
                TestRunState.Skipped => "⊘",
                _ => "?",
            };

            var line = $"  {icon} [{descriptor.Group}] {descriptor.DisplayName} ({result.DurationFormatted})";
            if (result.ErrorMessage is not null)
                line += $" — {result.ErrorMessage}";

            AppendLog(line);
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // FILTER
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Применяет все три фильтра: категория + группа + текст.</summary>
    private void ApplyFilter()
    {
        var filtered = AllTests.AsEnumerable();

        // Фильтр по категории
        if (SelectedCategory is not null)
            filtered = filtered.Where(t => t.Descriptor.Category == SelectedCategory.Value);

        // Фильтр по тематической группе
        if (SelectedGroup is not null)
            filtered = filtered.Where(t => t.Descriptor.Group == SelectedGroup);

        // Фильтр по тексту
        if (!string.IsNullOrWhiteSpace(SearchFilter))
        {
            var search = SearchFilter;
            filtered = filtered.Where(t =>
                t.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Descriptor.ClassName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                t.Descriptor.Group.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        FilteredTests = new ObservableCollection<TestItemViewModel>(filtered);

        // Обновляем IsSelected у GroupFilterItems
        foreach (var g in AvailableGroups)
            g.IsSelected = g.Name == SelectedGroup;
    }

    /// <summary>Строит строку-описание текущего фильтра для лога.</summary>
    private string BuildFilterLabel()
    {
        var parts = new List<string>(3);
        if (SelectedCategory is not null) parts.Add(SelectedCategory.Value.ToString());
        if (SelectedGroup is not null) parts.Add(SelectedGroup);
        if (!string.IsNullOrWhiteSpace(SearchFilter)) parts.Add($"\"{SearchFilter}\"");
        return parts.Count > 0 ? string.Join(" + ", parts) : "All";
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    private void ResetAll()
    {
        foreach (var test in AllTests)
        {
            test.State = TestRunState.NotRun;
            test.Duration = "";
            test.ErrorMessage = null;
        }
        Progress = 0;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int passed = AllTests.Count(t => t.State == TestRunState.Passed);
        int failed = AllTests.Count(t => t.State == TestRunState.Failed);
        int skipped = AllTests.Count(t => t.State == TestRunState.Skipped);
        int notRun = AllTests.Count(t => t.State == TestRunState.NotRun);
        int running = AllTests.Count(t => t.State == TestRunState.Running);

        var sb = new StringBuilder(64);
        if (passed > 0) sb.Append($"{passed} passed");
        if (failed > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{failed} failed"); }
        if (skipped > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{skipped} skipped"); }
        if (running > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{running} running"); }
        if (notRun > 0) { if (sb.Length > 0) sb.Append(", "); sb.Append($"{notRun} pending"); }

        Summary = sb.Length > 0 ? sb.ToString() : "No tests";
    }

    private readonly StringBuilder _logBuilder = new(4096);

    private void AppendLog(string line)
    {
        _logBuilder.AppendLine(line);
        LogOutput = _logBuilder.ToString();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runCts?.Cancel();
            _runCts?.Dispose();
            _runner.TestStarting -= OnTestStarting;
            _runner.TestCompleted -= OnTestCompleted;
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// UI-модель одного теста в списке.
/// </summary>
public sealed class TestItemViewModel : ViewModelBase
{
    public TestDescriptor Descriptor { get; }

    public string DisplayName => Descriptor.DisplayName;
    public TestCategory Category => Descriptor.Category;
    public string Group => Descriptor.Group;
    public bool RequiresNetwork => Descriptor.RequiresNetwork;

    [Reactive] public TestRunState State { get; set; } = TestRunState.NotRun;
    [Reactive] public string Duration { get; set; } = "";
    [Reactive] public string? ErrorMessage { get; set; }

    /// <summary>Иконка состояния.</summary>
    public string StateIcon => State switch
    {
        TestRunState.NotRun => "○",
        TestRunState.Running => "◉",
        TestRunState.Passed => "✓",
        TestRunState.Failed => "✗",
        TestRunState.Skipped => "⊘",
        _ => "?",
    };

    /// <summary>Цвет состояния (hex).</summary>
    public string StateColor => State switch
    {
        TestRunState.Passed => "#4CAF50",
        TestRunState.Failed => "#F44336",
        TestRunState.Running => "#2196F3",
        TestRunState.Skipped => "#9E9E9E",
        _ => "#757575",
    };

    /// <summary>Badge категории.</summary>
    public string CategoryBadge => Category switch
    {
        TestCategory.Unit => "UNIT",
        TestCategory.Integration => "INT",
        TestCategory.Benchmark => "BENCH",
        _ => "?",
    };

    public TestItemViewModel(TestDescriptor descriptor)
    {
        Descriptor = descriptor;

        this.WhenAnyValue(x => x.State)
            .Subscribe(_ =>
            {
                this.RaisePropertyChanged(nameof(StateIcon));
                this.RaisePropertyChanged(nameof(StateColor));
            });
    }
}

/// <summary>
/// Элемент фильтра по тематической группе. Используется в UI как toggle-кнопка.
/// </summary>
public sealed class GroupFilterItem : ViewModelBase
{
    /// <summary>Имя группы: "NToken", "SigCipher" и т.д.</summary>
    public string Name { get; }

    /// <summary>Количество тестов в группе.</summary>
    public int Count { get; }

    /// <summary>Текст для кнопки: "NToken (5)".</summary>
    public string Label => $"{Name} ({Count})";

    /// <summary>Выбрана ли эта группа в фильтре.</summary>
    [Reactive] public bool IsSelected { get; set; }

    public GroupFilterItem(string name, int count)
    {
        Name = name;
        Count = count;
    }
}

/// <summary>Extension для char.Repeat.</summary>
internal static class StringExtensions
{
    public static string Repeat(this char c, int count) => new(c, count);
}