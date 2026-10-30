using Bengine.Introspection.Servers;
using Karvon.Pipelines.Providers;

namespace Karvon.Pipelines;

/// <summary>
/// The CI services' tokens, by host: in the system keyring when the user keeps them there, else for this session only.
/// Never in a file, never in what Claude reads. GitHub's is needed only without a signed-in gh.
/// </summary>
public sealed class PipelineSecrets(ICredentialStore? keyring)
{
    readonly Dictionary<string, string> _session = new(StringComparer.OrdinalIgnoreCase);

    public bool HasKeyring => keyring is not null;

    static string Key(RepoRemote remote) => "karvon-pipelines/" + remote.Authority;

    public string? Get(RepoRemote remote)
    {
        lock (_session)
            if (_session.TryGetValue(remote.Authority, out var token))
                return token;
        if (keyring?.Get(Key(remote)) is { Length: > 0 } kept)
        {
            lock (_session)
                _session[remote.Authority] = kept;
            return kept;
        }
        return null;
    }

    public void Set(RepoRemote remote, string token, bool keep)
    {
        lock (_session)
            _session[remote.Authority] = token;
        Karvon.Claude.Tools.KnownSecrets.Add(Key(remote), token);
        if (keep)
            keyring?.Set(Key(remote), $"Karvon: {remote.ProviderName} on {remote.Authority}", token);
        else
            keyring?.Delete(Key(remote));
    }

    public void Forget(RepoRemote remote)
    {
        lock (_session)
            _session.Remove(remote.Authority);
        keyring?.Delete(Key(remote));
    }
}

/// <summary>A repository's CI service, made: GitHub through the user's signed-in gh (else a kept token), GitLab with its token.</summary>
public sealed class PipelineConnector(PipelineSecrets secrets, Func<string?> gh)
{
    public PipelineSecrets Secrets => secrets;

    /// <summary>The service, or null when a token is needed (none is kept, and for GitHub no gh is signed in).</summary>
    public async Task<IPipelineProvider?> ConnectAsync(RepoRemote remote, CancellationToken ct) =>
        remote.Provider == CiProvider.GitHub
            ? await GitHubAsync(remote, ct) is { } api ? new GitHubActions(remote, api) : null
            : GitLab(remote) is { } gitlab ? new GitLabCi(gitlab) : null;

    /// <summary>GitHub's API through the signed-in gh, else with a kept token; null when there is neither.</summary>
    public async Task<IGitHubApi?> GitHubAsync(RepoRemote remote, CancellationToken ct)
    {
        if (gh() is { } cli && await GhCliApi.SignedInAsync(cli, remote.Host, ct))
            return new GhCliApi(cli, remote.Host);
        return secrets.Get(remote) is { } token ? new GitHubTokenApi(remote.Host, token) : null;
    }

    /// <summary>A GitLab's API with its kept token; null when there is none.</summary>
    public GitLabApi? GitLab(RepoRemote remote) => secrets.Get(remote) is { } token ? new GitLabApi(remote, token) : null;

    /// <summary>Where the service makes a token with the scopes the window needs.</summary>
    public static string TokenPage(RepoRemote remote) => remote.Provider == CiProvider.GitHub
        ? $"https://{remote.Authority}/settings/tokens/new?scopes=repo,workflow&description=Karvon%20Studio"
        : $"{remote.Scheme}://{remote.Authority}/-/user_settings/personal_access_tokens?name=Karvon%20Studio&scopes=api";
}
