using Avalonia.Threading;
using Karvon.Modules;
using Renci.SshNet.Common;

namespace Karvon.Servers;

/// <summary>
/// The remote folder open as the workspace: what changes in its copy (saved in the editor, written by Claude, a tool or
/// a git checkout) goes to its machine half a second after, and before a task or a build runs there; what changed there
/// comes when it opens and when Pull is asked. A file changed on both sides is asked about: whose to keep.
/// </summary>
sealed class RemoteFolderSession : IDisposable
{
    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);
    readonly Func<CancellationToken, Task<IRemoteFiles>> _files;
    readonly Action<string> _status;
    readonly Func<string, string, string, Task<bool>> _confirm;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _ending = new();
    FileSystemWatcher? _watcher;
    Timer? _timer;

    public RemoteFolderSession(RemoteFolder folder, ServerHost host, Func<CancellationToken, Task<IRemoteFiles>> files, Action<string> status,
        Func<string, string, string, Task<bool>> confirm)
    {
        Folder = folder;
        Host = host;
        _files = files;
        _status = status;
        _confirm = confirm;
        Workspace = new RemoteWorkspace($"SSH: {host.Name}", $"{host.Name}:{folder.RemoteRoot}")
        {
            Run = task => task with { Program = "ssh", Arguments = SshCommand.Running(host, folder.CommandThere(task.Program, task.Arguments, task.WorkingDirectory, task.Environment)), Environment = null },
            Terminal = local => ("ssh", SshCommand.Running(host, $"cd {RemoteFolder.Quote(folder.RemotePathOf(local) ?? folder.RemoteRoot)} && exec \"${{SHELL:-sh}}\" -l")),
            LocalPath = folder.LocalPathOf,
            FlushAsync = async () => await PushAsync(),
        };
    }

    public RemoteFolder Folder { get; }

    public ServerHost Host { get; }

    public RemoteWorkspace Workspace { get; }

    /// <summary>The copy watched: a change sends what changed a moment after (several at once, as one).</summary>
    public void Watch()
    {
        _timer = new Timer(_ => Dispatcher.UIThread.Post(() => _ = PushAsync()));
        _watcher = new FileSystemWatcher(Folder.LocalRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    void OnChanged(object sender, FileSystemEventArgs e)
    {
        var relative = Path.GetRelativePath(Folder.LocalRoot, e.FullPath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.Split('/').Any(RemoteFolder.Skipped.Contains) || e.FullPath.EndsWith(".karvon-part", StringComparison.Ordinal))
            return;
        _timer?.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>What changed here, sent; each conflict asked about (keep mine there, or leave it).</summary>
    public Task<RemoteSync> PushAsync() => SyncAsync(push: true);

    /// <summary>What changed there, brought; each conflict asked about (take theirs here, or keep mine).</summary>
    public Task<RemoteSync> PullAsync() => SyncAsync(push: false);

    async Task<RemoteSync> SyncAsync(bool push)
    {
        var ct = _ending.Token;
        try
        {
            await _gate.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return RemoteSync.None;
        }
        try
        {
            if (!push)
                _status($"Pulling {Host.Name}:{Folder.RemoteRoot}...");
            var files = await _files(ct);
            var result = push ? await Folder.PushAsync(files, ct) : await Folder.PullAsync(files, ct);
            foreach (var conflict in result.Conflicts.Take(5))
            {
                var answer = push
                    ? await _confirm($"{conflict} changed on {Host.Name}", $"{conflict} changed on {Host.Name} too since it was copied. Overwrite it there with yours?", "Overwrite")
                    : await _confirm($"{conflict} changed here", $"{conflict} changed here and on {Host.Name}. Take the one on {Host.Name} (yours is lost)?", "Take theirs");
                if (answer)
                    await (push ? Folder.PushOneAsync(files, conflict, ct) : Folder.PullOneAsync(files, conflict, ct));
            }
            if (push && result.Copied + result.Deleted > 0)
                _status($"Sent {Changes(result)} to {Host.Name}.");
            else if (!push)
                _status($"{Host.Name}:{Folder.RemoteRoot}: {(result.Copied + result.Deleted > 0 ? Changes(result) + " pulled" : "up to date")}"
                    + (result.Left.Count > 0 ? $"; left there: {string.Join(", ", result.Left.Take(3))}{(result.Left.Count > 3 ? "..." : "")}." : "."));
            return result;
        }
        catch (Exception ex) when (ex is ServerException or SshException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            _status(push
                ? $"The changes were not sent to {Host.Name}: {ex.Message} They are kept here and go with the next change."
                : $"{Host.Name}:{Folder.RemoteRoot} was not pulled: {ex.Message}");
            return RemoteSync.None;
        }
        catch (OperationCanceledException)
        {
            return RemoteSync.None;
        }
        finally
        {
            _gate.Release();
        }
    }

    static string Changes(RemoteSync sync) =>
        string.Join(", ", new[] { sync.Copied > 0 ? $"{sync.Copied} file{(sync.Copied == 1 ? "" : "s")}" : null, sync.Deleted > 0 ? $"{sync.Deleted} deleted" : null }.OfType<string>());

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer?.Dispose();
        _ending.Cancel();
    }
}
