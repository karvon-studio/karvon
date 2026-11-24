using Karvon.Lsp;

namespace Karvon.CodeMap;

/// <summary>
/// The map of the workspace open: its store, the scans that keep it as the workspace is, and the runs that write its
/// summaries (a while after the map changed, so a run of saves makes one run).
/// </summary>
public sealed class CodeMapWorkspace : IDisposable
{
    /// <summary>How long after the map changed the summaries are written (the first time, at once).</summary>
    public static TimeSpan SummaryDelay { get; set; } = TimeSpan.FromSeconds(60);

    readonly Lock _lock = new();
    readonly CancellationTokenSource _closing = new();
    Func<string, CancellationToken, Task<string>>? _writer;
    CancellationTokenSource? _summaries;
    Task? _summarizing;
    Timer? _summaryTimer;
    bool _summariesAgain;

    public CodeMapWorkspace(string root, string database, Func<string, CancellationToken, Task<IReadOnlyList<LspDocumentSymbol>?>> outline)
    {
        Store = new CodeMapStore(database);
        Indexer = new CodeMapIndexer(root, Store, outline);
        Indexer.Changed += () =>
        {
            Changed?.Invoke();
            ScheduleSummaries(SummaryDelay);
        };
        Indexer.ProgressChanged += () => Changed?.Invoke();
    }

    public string Root => Indexer.Root;

    public CodeMapStore Store { get; }

    public CodeMapIndexer Indexer { get; }

    /// <summary>The run writing summaries now, or the last one (its progress and problem); null before any.</summary>
    public CodeSummarizer? Summarizer { get; private set; }

    public bool IsSummarizing => _summarizing is { IsCompleted: false };

    /// <summary>The map or its summaries changed, or a run's progress: the window follows. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>The first scan.</summary>
    public void Start() => _ = Indexer.ScanAsync();

    /// <summary>
    /// The map as the workspace is now, before it is read: a scan, waited for at most <paramref name="wait"/> (the first
    /// one of a large workspace may take longer; the map has what was read so far, and says so).
    /// </summary>
    public async Task FreshAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        try
        {
            await Indexer.ScanAsync(cancellationToken).WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    /// <summary>Who writes the summaries from now on (null: no one): a run starts at once, and the one running stops.</summary>
    public void SetWriter(Func<string, CancellationToken, Task<string>>? writer)
    {
        lock (_lock)
        {
            _writer = writer;
            _summaries?.Cancel();
        }
        if (writer is not null)
            ScheduleSummaries(TimeSpan.Zero);
        Changed?.Invoke();
    }

    void ScheduleSummaries(TimeSpan delay)
    {
        lock (_lock)
        {
            if (_writer is null || _closing.IsCancellationRequested)
                return;
            _summaryTimer ??= new Timer(_ => RunSummaries());
            _summaryTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    void RunSummaries()
    {
        Func<string, CancellationToken, Task<string>> writer;
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_writer is null || _closing.IsCancellationRequested)
                return;
            if (_summarizing is { IsCompleted: false })
            {
                _summariesAgain = true;
                return;
            }
            writer = _writer;
            cts = _summaries = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            var summarizer = Summarizer = new CodeSummarizer(Store, writer);
            summarizer.ProgressChanged += () => Changed?.Invoke();
            _summarizing = Task.Run(async () =>
            {
                try
                {
                    // The map first, so that what is summarized is what the workspace has.
                    await Indexer.ScanAsync(cts.Token);
                    await summarizer.RunAsync(Root, cts.Token);
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    Changed?.Invoke();
                    bool again;
                    lock (_lock)
                    {
                        again = _summariesAgain && !cts.IsCancellationRequested;
                        _summariesAgain = false;
                    }
                    if (again)
                        ScheduleSummaries(SummaryDelay);
                }
            });
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _closing.Cancel();
            _summaryTimer?.Dispose();
        }
        Indexer.Stop();
        try
        {
            Task.WaitAll([_summarizing ?? Task.CompletedTask, Indexer.Idle], TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        Store.Dispose();
    }
}
