using System.Diagnostics;
using Karvon.Lsp;

namespace Karvon.CodeMap;

/// <summary>The map of the workspace open: its store, and the scans that keep it as the workspace is.</summary>
public sealed class CodeMapWorkspace : IDisposable
{
    readonly Lock _lock = new();
    Task? _closed;

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
        Indexer.Changed += () => Changed?.Invoke();
        Indexer.ProgressChanged += () => Changed?.Invoke();
    }

    public string Root => Indexer.Root;

    public CodeMapStore Store { get; }

    public CodeMapIndexer Indexer { get; }

    /// <summary>The map changed, or a scan's progress: the window follows. Raised on any thread.</summary>
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

    /// <summary>
    /// The map closed: the scan stops, and the store closes once it has. Done then; the window does not wait for it (a scan
    /// waits for an answer asked for on the window's thread).
    /// </summary>
    public Task CloseAsync()
    {
        lock (_lock)
        {
            if (_closed is not null)
                return _closed;
            return _closed = Task.Run(async () =>
            {
                Indexer.Stop();
                await Indexer.Idle.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                Store.Dispose();
            });
        }
    }

    public void Dispose() => _ = CloseAsync();
}
