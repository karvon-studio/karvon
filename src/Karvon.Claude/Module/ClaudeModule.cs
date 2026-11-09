using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Karvon.Claude.Tools;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude;

/// <summary>
/// Claude Code: the CLI's conversation in a tool window (see <see cref="ClaudeViewModel"/>). It runs in the solution's
/// folder, sees the editor in front and what is selected in it, and uses Karvon's tools (the core's and the modules') as
/// the user's own; what they do shows where the user would have done it. The other modules reach it as
/// <see cref="IClaudeService"/>.
/// </summary>
public sealed class ClaudeModule : AgentPanelModule, IClaudeService
{
    public const string ToolWindowId = "Claude";

    /// <summary>The conversation the solution had open, kept with it.</summary>
    const string ConversationKey = "claude.conversation";

    ClaudeViewModel? _claude;

    public override string Id => "claude";

    public override string Name => "Claude Code";


    public override string Icon => "claude";

    public override string Description =>
        "Claude Code's conversation in Karvon: your own CLI, account and settings; the editor's file and selection, Ask Claude, "
        + "Karvon's tools for the build, tests, debugger, databases, containers and servers.";

    public override IReadOnlyList<ToolWindowInfo> ToolWindows =>
        [new(ToolWindowId, "Claude Code", "Claude Code", "Claude", DockSide.Right, () => new ClaudeView { DataContext = Claude }) { Gesture = "Ctrl+Escape", Show = Show }];

    /// <summary>The conversation, made the first time it is needed.</summary>
    public ClaudeViewModel Claude => _claude ??= Create();

    /// <summary>Output (Claude): every call of Karvon's tools, what it was about and what it gave.</summary>
    protected override string OutputName => "Claude";

    protected override void Started()
    {
        _ = Output;
        Workbench.ActiveDocumentChanged += _ => _claude?.RefreshContext();
    }

    ClaudeViewModel Create()
    {
        var claude = new ClaudeViewModel(Workbench.Settings, WorkingDirectory, EditorContext, Files)
        {
            Tools = NewToolServer(),
            McpServers = cwd => ExtraMcpServers?.Invoke(cwd),
        };
        Wire(claude, ToolWindowId, "Claude Code", () => "Claude", "claude");
        // Another account has other servers (its own .claude.json) and its own skills.
        claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudeViewModel.SelectedAccount))
                Changed?.Invoke();
        };
        return claude;
    }

    /// <summary>Another folder: the conversation runs there; a solution opened gets back the conversation it had.</summary>
    public override async Task WorkspaceChangedAsync()
    {
        ForgetFiles();
        if (_claude is { } claude)
            await claude.OnWorkingDirectoryChangedAsync();
        if (Workbench.WorkspaceState.GetValueOrDefault(ConversationKey) is { Length: > 0 } conversation)
            await Claude.RestoreConversationAsync(conversation);
    }

    /// <summary>The conversation open now, kept with the solution (one never opened this time keeps the one it would restore).</summary>
    public override void SaveWorkspaceState(IDictionary<string, string> state)
    {
        if (_claude is not { } claude)
            return;
        if (claude.SessionId is { } session)
            state[ConversationKey] = session;
        else
            state.Remove(ConversationKey);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_claude is { } claude)
            await claude.DisposeAsync();
    }

    // ---- IClaudeService -------------------------------------------------------------------------------------------

    public event Action? Changed;

    string IClaudeService.ToolWindowId => ToolWindowId;

    /// <summary>The chosen account's folder (CLAUDE_CONFIG_DIR or ~/.claude otherwise); always the home's in a home of the tests'.</summary>
    public string ConfigDirectory => Environment.GetEnvironmentVariable("KARVON_USER_HOME") is not null ? Path.Combine(Services.UserHome.Path, ".claude")
        : _claude?.SelectedAccount?.ConfigDirectory ?? ClaudeCli.ConfigDirectory;

    /// <summary>The chosen account's settings (~/.claude.json for the default one); always the home's in a home of the tests'.</summary>
    public string? SettingsFile =>
        Environment.GetEnvironmentVariable("KARVON_USER_HOME") is null && _claude?.SelectedAccount is { IsDefault: false } account
            ? Path.Combine(account.ConfigDirectory, ".claude.json") : null;

    public IReadOnlyList<IClaudeTool> BuiltInTools => Workbench.Settings.ClaudeKarvonTools ? ToolServer().Tools : [];

    public Func<string, string?>? ExtraMcpServers { get; set; }

    /// <summary>View > Claude Code (Ctrl+Esc), and Ask Claude in the editor.</summary>
    public void Show()
    {
        Workbench.ShowToolWindow(ToolWindowId);
        Claude.RefreshContext();
        Claude.RequestFocus();
    }

    public void Ask(string prompt)
    {
        Workbench.ShowToolWindow(ToolWindowId);
        Claude.Prompt = prompt;
        Claude.RequestFocus();
    }

    /// <summary>Solution Explorer > Ask Claude: the files, folders or projects go into the prompt as @mentions.</summary>
    public void AskAbout(IReadOnlyList<string> paths)
    {
        Workbench.ShowToolWindow(ToolWindowId);
        Claude.RefreshContext();
        Claude.AddFiles(paths);
    }

    public Task<string> AuthenticateMcpAsync(string name, Action<string?>? finished = null) => Claude.AuthenticateMcpAsync(name, finished);

    public async Task<string?> AddMcpServerAsync(string name, string transport, string target, string scope)
    {
        var problem = await Claude.AddMcpServerAsync(name, transport, target, scope);
        Changed?.Invoke();
        return problem;
    }

    /// <summary>claude -p with a fast model and no tools, as the chosen account; the run is not kept as a session.</summary>
    public async Task<string> PrintAsync(string directory, string prompt)
    {
        var claude = Claude;
        claude.CheckCli();
        if (claude.CliPath is not { } cli)
            throw new InvalidOperationException("Claude Code is not installed: the claude command was not found.");
        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "-p", "--model", "haiku", "--tools", "", "--no-session-persistence", "--output-format", "text" })
            psi.ArgumentList.Add(arg);
        // Not a nested session of the Claude Code Karvon may itself be running in.
        psi.Environment.Remove("CLAUDECODE");
        claude.SelectedAccount?.Apply(psi.Environment);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("claude could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            await stdin.WriteAsync(prompt);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("claude did not answer within 2 minutes.");
        }
        var (text, problem) = (await output, (await error).Trim());
        if (process.ExitCode != 0)
            throw new InvalidOperationException(problem.Length > 0 ? problem : $"exit code {process.ExitCode}");
        return text;
    }
}
