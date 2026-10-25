using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Testing;

namespace Karvon.ViewModels;

/// <summary>A file of the Code Coverage Results window.</summary>
public sealed record CoverageRow(FileCoverage File, string Root)
{
    public string Name => Path.GetFileName(File.Path);

    public string Folder => Path.GetDirectoryName(Path.GetRelativePath(Root, File.Path))?.Replace('\\', '/') ?? "";

    public string Percent => $"{File.Percent:0.#}%";

    public string Lines => $"{File.Covered} / {File.Total}";

    public bool IsLow => File.Percent < 50;
}

/// <summary>
/// Code coverage (Test > Analyze Code Coverage for All Tests): the tests run with their frameworks' coverage (.NET's
/// coverlet), or a report loaded (Cobertura, LCOV: Vitest's, pytest-cov's); each file's lines colored in the editor,
/// the files listed by how much is covered.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverage), nameof(CoverageSummary))]
    public partial CoverageReport? Coverage { get; private set; }

    public bool HasCoverage => Coverage is not null;

    public string CoverageSummary => Coverage is { } c
        ? $"Covered {c.Percent:0.#}% ({c.Covered} of {c.Total} lines, {c.Files.Count} files)"
        : "Analyze the code coverage of the tests (Test menu), or load a coverage report (Cobertura's XML, LCOV).";

    public ObservableCollection<CoverageRow> CoverageRows { get; } = [];

    /// <summary>The editors color the lines covered (Show Code Coverage Coloring).</summary>
    [ObservableProperty]
    public partial bool ShowCoverageColoring { get; set; } = true;

    partial void OnShowCoverageColoringChanged(bool value) => ApplyCoverage();

    /// <summary>Every framework's tests run with coverage measured (those that can), the reports merged and shown.</summary>
    [RelayCommand(CanExecute = nameof(CanRunTests))]
    public async Task AnalyzeCodeCoverage()
    {
        ShowToolWindow(ToolWindows.TestExplorer);
        IsTesting = true;
        try
        {
            if (!await DiscoverTestsAsync())
                return;
            var frameworks = TestFrameworks.Where(f => f.CanCollectCoverage).ToList();
            if (frameworks.Count == 0)
            {
                TestSummary = "None of the workspace's test frameworks measures coverage in Karvon: load a report of its own (Test > Load Coverage Report).";
                return;
            }
            _lastRun = [.. _discovered.Keys.Where(k => frameworks.Contains(_discovered[k].Framework))];
            _testRuns++;
            foreach (var key in _lastRun)
                _testCases[key].SetResult(TestState.NotRun, TimeSpan.Zero, null, null, null);
            SetStatus("Analyzing code coverage...", StatusKind.Busy);
            var cts = _testCts = new CancellationTokenSource();
            var merged = new CoverageReport();
            var problems = new List<string>();
            foreach (var framework in frameworks)
            {
                void OnResult(TestItemResult r) => Dispatcher.UIThread.Post(() => ApplyResult(framework, r));
                var (reports, problem) = await framework.RunCoverageAsync(null, OnResult, cts.Token);
                foreach (var report in reports)
                    merged.Merge(CoverageReport.Load(report, Solution?.Directory));
                if (problem is not null)
                    problems.Add(problem);
                if (cts.IsCancellationRequested)
                    break;
            }
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            UpdateTestCounts();
            if (merged.Total > 0)
                ShowCoverage(merged);
            TestSummary = cts.IsCancellationRequested ? "Code coverage canceled."
                : merged.Total > 0 ? $"{CoverageSummary}: {PassedCount} Passed, {FailedCount} Failed, {SkippedCount} Skipped."
                : string.Join(" ", problems.DefaultIfEmpty("No coverage was collected."));
            SetStatus(merged.Total > 0 ? CoverageSummary : "No coverage was collected", StatusKind.Ready);
        }
        catch (Exception ex)
        {
            TestSummary = "Code coverage failed: " + ex.Message;
            SetStatus("Ready", StatusKind.Ready);
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>A coverage report of the workspace's (one its tests' own tool made) loaded.</summary>
    [RelayCommand]
    async Task LoadCoverageReport()
    {
        if (Dialogs is null || await Dialogs.PickFilesAsync("Coverage report (Cobertura XML, LCOV)") is not [var file, ..])
            return;
        try
        {
            ShowCoverage(CoverageReport.Load(file, Solution?.Directory));
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
        {
            SetStatus("The coverage report could not be read: " + ex.Message);
        }
    }

    [RelayCommand]
    void ClearCoverage()
    {
        Coverage = null;
        CoverageRows.Clear();
        ApplyCoverage();
    }

    public void ShowCoverage(CoverageReport report)
    {
        Coverage = report;
        CoverageRows.Clear();
        foreach (var file in report.Files.OrderBy(f => f.Percent).ThenBy(f => f.Path, StringComparer.Ordinal))
            CoverageRows.Add(new CoverageRow(file, Solution?.Directory ?? ""));
        ApplyCoverage();
        ShowToolWindow(ToolWindows.CoverageResults);
    }

    void ApplyCoverage()
    {
        foreach (var editor in Editors)
            editor.Host.SetCoverage(ShowCoverageColoring ? Coverage?.For(editor.FilePath) : null);
    }

    [RelayCommand]
    void OpenCoverageFile(CoverageRow? row)
    {
        if (row is not null && File.Exists(row.File.Path))
            OpenDocument(row.File.Path);
    }
}
