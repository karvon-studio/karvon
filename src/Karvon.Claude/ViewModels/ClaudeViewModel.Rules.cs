using System.Collections.Concurrent;
using Avalonia.Threading;
using Karvon.Claude;

namespace Karvon.ViewModels;

/// <summary>
/// The user's rules for Karvon's tools, as its tool server enforces them: a call they say to ask for, which Claude Code
/// let through without asking (Bypass permissions, auto mode, an allow rule of its own), asked here on a card of
/// Karvon's own, in the conversation it came from.
/// </summary>
public partial class ClaudeViewModel
{
    const string AskPrefix = "karvon-rules-";

    /// <summary>Karvon's own questions, waiting for the user: its tool server waits for each answer.</summary>
    readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _asks = new();

    Task<bool> AskAboutToolAsync(ClaudeToolRequest ask, CancellationToken cancellationToken)
    {
        var request = new ClaudePermissionRequest(AskPrefix + Guid.NewGuid().ToString("N"), ask.ToolName, null, ask.Input, null, [], null);
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _asks[request.RequestId] = answer;
        var registration = cancellationToken.Register(() =>
        {
            // Claude was interrupted: the call does not run, and its card is answered.
            if (_asks.TryRemove(request.RequestId, out var waiting))
            {
                waiting.TrySetResult(false);
                Dispatcher.UIThread.Post(() => ApplyAnsweredElsewhere(request.RequestId, "Cancelled"));
            }
        });
        Dispatcher.UIThread.Post(() =>
        {
            if (ask.Caller is ClaudeSession session && session != _session)
            {
                if (_background.Any(b => b.Session == session))
                    HandleInBackground(session, request);
                else if (_asks.TryRemove(request.RequestId, out var gone))
                    gone.TrySetResult(false);
                return;
            }
            Apply(request, live: true);
        });
        return answer.Task.ContinueWith(t =>
        {
            registration.Dispose();
            return t.Result;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Whether the folder Claude works in is trusted (<see cref="WorkspaceTrust"/>): until it is, the sessions do not use
    /// what its repository brings, and building, testing and running its code ask.
    /// </summary>
    public bool IsFolderTrusted => WorkspaceTrust.IsTrusted(_settings.TrustedFolders, _workingDirectory());

    /// <summary>
    /// Trusts the folder: Karvon's tools build, test and run its code as anywhere else at once; the sessions started after
    /// use its Claude Code settings, its .mcp.json and its data sources.
    /// </summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void TrustFolder()
    {
        WorkspaceTrust.Trust(_settings.TrustedFolders, _workingDirectory());
        _settings.Save();
        OnPropertyChanged(nameof(IsFolderTrusted));
    }

    /// <summary>The user allowed a call of Karvon's tools on Claude Code's prompt: its tool server does not ask again.</summary>
    void Approved(ClaudePermissionRequest request)
    {
        if (!request.RequestId.StartsWith(AskPrefix, StringComparison.Ordinal) && Tools?.Find(request.ToolName) is not null)
            Tools.Approve(request.ToolName, request.Input);
    }
}
