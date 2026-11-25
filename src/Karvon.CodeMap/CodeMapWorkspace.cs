using System.Diagnostics;
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
    bool _running, _summariesAgain;
    Task? _closed;

    /// <summary>What the model left out, twice: not asked about again while the workspace is open, unless it changes.</summary>
    readonly HashSet<string> _leftOut = new(StringComparer.Ordinal);

    /// <summary>Since the first call waited for the first scan.</summary>
    Stopwatch? _waiting;

    public CodeMapWorkspace(string root, string database, Func<string, CancellationToken, Task<IReadOnlyList<LspDocumentSymbol>?>> outline)
        : this(root, new CodeMapStore(database), outline)
    {
    }

    public CodeMapWorkspace(string root, CodeMapStore store, Func<string, CancellationToken, Task<IReadOnlyList<LspDocumentSymbol>?>> outline)
    {
        Store = store;
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

    public bool IsSummarizing
    {
        get
        {
            lock (_lock)
                return _running;
        }
    }

    /// <summary>The map or its summaries changed, or a run's progress: the window follows. Raised on any thread.</summary>
    public event Action? Changed;

    /// <summary>The first scan.</summary>
    public void Start() => _ = Indexer.ScanAsync();

    /// <summary>How long the calls made during the first scan wait for it, all of them together: a small workspace's ends in that.</summary>
    static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(2);

    /// <summary>How long a call waits for what changed since the last scan to be read.</summary>
    static readonly TimeSpan ChangesWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The map as the workspace is now, before it is read: a scan, waited for a few seconds at most (the first one of a
    /// large workspace takes longer, and only the first calls wait for it; the map has what was read so far, and says so).
    /// </summary>
    public async Task FreshAsync(CancellationToken cancellationToken)
    {
        var wait = ChangesWait;
        if (!Indexer.IsComplete)
        {
            lock (_lock)
                wait = FirstWait - (_waiting ??= Stopwatch.StartNew()).Elapsed;
            if (wait < TimeSpan.Zero)
                wait = TimeSpan.Zero;
        }
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
            if (_writer is null || _closed is not null)
                return;
            _summaryTimer ??= new Timer(_ => RunSummaries());
            _summaryTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    void RunSummaries()
    {
        lock (_lock)
        {
            if (_writer is not { } writer || _closed is not null)
                return;
            if (_running)
            {
                _summariesAgain = true;
                return;
            }
            _running = true;
            var cts = _summaries = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
            var summarizer = Summarizer = new CodeSummarizer(Store, writer, _leftOut);
            summarizer.ProgressChanged += () => Changed?.Invoke();
            _summarizing = Task.Run(() => RunSummariesAsync(summarizer, cts));
        }
    }

    async Task RunSummariesAsync(CodeSummarizer summarizer, CancellationTokenSource cts)
    {
        try
        {
            // The map first, so that what is summarized is what the workspace has.
            await Indexer.ScanAsync(cts.Token);
            await summarizer.RunAsync(Root, cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException || _closing.IsCancellationRequested)
        {
        }
        finally
        {
            bool again;
            lock (_lock)
            {
                _running = false;
                again = _summariesAgain;
                _summariesAgain = false;
            }
            Changed?.Invoke();
            // Stopped for another writer: it starts now; else what changed meanwhile is written a while after.
            if (again)
                ScheduleSummaries(cts.IsCancellationRequested ? TimeSpan.Zero : SummaryDelay);
        }
    }

    /// <summary>
    /// The map closed: the scan and the summaries stop, and the store closes once they have. Done then; the window does not
    /// wait for it (a scan waits for an answer asked for on the window's thread).
    /// </summary>
    public Task CloseAsync()
    {
        lock (_lock)
        {
            if (_closed is not null)
                return _closed;
            _summaryTimer?.Dispose();
            var summarizing = _summarizing ?? Task.CompletedTask;
            return _closed = Task.Run(async () =>
            {
                Indexer.Stop();
                await _closing.CancelAsync();
                await Task.WhenAll(summarizing, Indexer.Idle).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Store.Dispose();
            });
        }
    }

    public void Dispose() => _ = CloseAsync();
}
