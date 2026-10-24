using System.Text.RegularExpressions;

namespace Karvon.Pipelines;

public enum CiProvider
{
    GitHub,
    GitLab,
}

/// <summary>
/// A repository's remote as the pipelines need it: its host, its path there (owner/repo, group/subgroup/project), the
/// service (github.com and github.* are GitHub's; any other host is taken for a GitLab), and where its pages are. A
/// credential in the URL is left out.
/// </summary>
public sealed partial record RepoRemote(string Host, string Path, CiProvider Provider, string Scheme = "https", int? Port = null)
{
    /// <summary>host[:port], as the keyring and the dialogs name it.</summary>
    public string Authority => Port is { } port ? $"{Host}:{port}" : Host;

    public string WebUrl => $"{Scheme}://{Authority}/{Path}";

    public string Name => Path;

    public string ProviderName => Provider == CiProvider.GitHub ? "GitHub Actions" : "GitLab CI";

    public static RepoRemote? Parse(string url)
    {
        url = url.Trim();
        string host, path, scheme = "https";
        int? port = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" or "git")
        {
            host = uri.Host;
            path = uri.AbsolutePath;
            if (uri.Scheme is "http" or "https")
            {
                scheme = uri.Scheme;
                port = uri.IsDefaultPort ? null : uri.Port;
            }
        }
        else if (ScpLike().Match(url) is { Success: true } m)
        {
            host = m.Groups["host"].Value;
            path = m.Groups["path"].Value;
        }
        else
            return null;
        path = path.Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        if (host.Length == 0 || !path.Contains('/'))
            return null;
        var github = host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || host.StartsWith("github.", StringComparison.OrdinalIgnoreCase);
        return new RepoRemote(host.ToLowerInvariant(), Uri.UnescapeDataString(path), github ? CiProvider.GitHub : CiProvider.GitLab, scheme, port);
    }

    /// <summary>user@host:path, as ssh's remotes are written.</summary>
    [GeneratedRegex(@"^(?:[^@/\s]+@)?(?<host>[^:/\s]+):(?<path>[^\s]+)$")]
    private static partial Regex ScpLike();
}
