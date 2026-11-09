namespace Karvon.Claude;

/// <summary>OpenAI's Codex CLI in the panel (<see cref="CodexSession"/>).</summary>
public sealed class CodexBackend : IAgentBackend
{
    public const string BackendId = "codex";

    public string Id => BackendId;

    public string Name => "Codex";

    public string Description => "OpenAI's Codex CLI, signed in with ChatGPT or an API key";

    public string MissingTitle => "Codex is not installed";

    public string MissingHint => "Install the Codex CLI, sign in once by running codex in a terminal, then retry.";

    public string? InstallCommand => "npm install -g @openai/codex";

    public IReadOnlyList<string> Modes => CodexSession.Modes;

    /// <summary>The `codex` executable: KARVON_CODEX_PATH ("off": none), then where <see cref="AgentExecutables"/> looks.</summary>
    public string? Find() => FindExecutable();

    public static string? FindExecutable()
    {
        if (Environment.GetEnvironmentVariable("KARVON_CODEX_PATH") is { Length: > 0 } explicitPath)
            return File.Exists(explicitPath) ? explicitPath : null;
        return AgentExecutables.Find("codex");
    }

    public Task<IReadOnlyList<AgentModel>> ModelsAsync(string executable, string workingDirectory, CancellationToken cancellationToken = default) =>
        CodexSession.ModelsAsync(executable, workingDirectory, cancellationToken);

    public async Task<IAgentSession> StartAsync(AgentStartOptions options, CancellationToken cancellationToken = default) =>
        await CodexSession.StartAsync(options, cancellationToken);
}
