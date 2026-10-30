using System.Text.RegularExpressions;

namespace Karvon.Claude.Tools;

/// <summary>
/// Secrets never leave Karvon for Claude: those Karvon holds (<see cref="KnownSecrets"/>), told by their values; the
/// tokens told by their providers' prefixes and private keys; passwords in connection strings and URLs, bearer tokens,
/// secret-looking variables.
/// </summary>
public static partial class Redaction
{
    public const string Mask = "***";

    /// <summary>The tokens told by their form, with what they are.</summary>
    internal static readonly (string Name, Regex Pattern)[] Tokens =
    [
        ("a private key", PrivateKey()),
        ("a GitHub token", GitHubToken()),
        ("a GitLab token", GitLabToken()),
        ("a Slack token", SlackToken()),
        ("a Stripe key", StripeKey()),
        ("an AWS access key", AwsAccessKey()),
        ("a Google API key", GoogleApiKey()),
        ("an API key", ModelApiKey()),
    ];

    /// <summary>
    /// What is surely a secret hidden, and nothing else: the secrets Karvon holds and the tokens told by their form, each by
    /// what it is ("«secret: karvon-jira/acme»", "«secret: a GitHub token»"). For any text that goes to Claude, code
    /// included, where a password setting may be a test's and is left as it is.
    /// </summary>
    public static string Secrets(string text)
    {
        if (text.Length == 0)
            return text;
        foreach (var (value, name) in KnownSecrets.All)
            if (text.Contains(value, StringComparison.Ordinal))
                text = text.Replace(value, $"«secret: {name}»", StringComparison.Ordinal);
        foreach (var (name, pattern) in Tokens)
            text = pattern.Replace(text, $"«secret: {name}»");
        return text;
    }

    /// <summary>Every secret hidden: as <see cref="Secrets"/>, and the passwords in settings and URLs, bearer tokens.</summary>
    public static string Text(string text)
    {
        text = Secrets(text);
        text = PasswordSetting().Replace(text, m => m.Groups[1].Value + Mask);
        text = UrlCredentials().Replace(text, m => m.Groups[1].Value + Mask + "@");
        return BearerToken().Replace(text, m => m.Groups[1].Value + Mask);
    }

    /// <summary>What <see cref="Text"/> would hide in <paramref name="text"/>, each kind once ("a GitHub token").</summary>
    public static IReadOnlyList<string> Find(string text)
    {
        List<string> found = [];
        foreach (var (value, name) in KnownSecrets.All)
            if (text.Contains(value, StringComparison.Ordinal) && !found.Contains($"a secret Karvon keeps ({name})"))
                found.Add($"a secret Karvon keeps ({name})");
        foreach (var (name, pattern) in Tokens)
            if (pattern.IsMatch(text))
                found.Add(name);
        if (PasswordSetting().IsMatch(text))
            found.Add("a password in a connection string");
        if (UrlCredentials().IsMatch(text))
            found.Add("a password in a URL");
        if (BearerToken().IsMatch(text))
            found.Add("a bearer token");
        return found;
    }

    /// <summary>A variable's value: hidden when its name looks secret, otherwise with the passwords in it hidden.</summary>
    public static string Variable(string name, string value) => SecretName().IsMatch(name) ? Mask : Text(value);

    /// <summary>A name a secret goes by (GITHUB_TOKEN, DB_PASSWORD, AWS_SECRET_ACCESS_KEY...).</summary>
    public static bool IsSecretName(string name) => SecretName().IsMatch(name);

    /// <summary>
    /// A value that holds a secret: a password in a connection string or a URL, a bearer token, or a token by its
    /// provider's prefix (GitHub's, GitLab's, Slack's, Stripe's, AWS's access key, Google's API key, OpenAI's and
    /// Anthropic's keys), or a private key.
    /// </summary>
    public static bool HoldsSecret(string value) =>
        PasswordSetting().IsMatch(value) || UrlCredentials().IsMatch(value) || BearerToken().IsMatch(value) || Tokens.Any(t => t.Pattern.IsMatch(value));

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----(?:[\s\S]*?-----END (?:[A-Z]+ )?PRIVATE KEY-----|[\s\S]*)")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,})")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bglpat-[A-Za-z0-9_-]{20,}")]
    private static partial Regex GitLabToken();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\b[rs]k_live_[A-Za-z0-9]{20,}")]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}")]
    private static partial Regex GoogleApiKey();

    /// <summary>Anthropic's and OpenAI's keys.</summary>
    [GeneratedRegex(@"\bsk-(?:ant-[a-z0-9]+-|proj-)?[A-Za-z0-9_-]{32,}")]
    private static partial Regex ModelApiKey();

    [GeneratedRegex(@"(?i)\b((?:password|pwd)\s*=\s*)[^;'""\s]+")]
    internal static partial Regex PasswordSetting();

    [GeneratedRegex(@"(\w+://[^:/@\s]+:)[^@\s]+@")]
    internal static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)\b(bearer\s+)[A-Za-z0-9._~+/=-]{8,}")]
    internal static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)(password|passwd|passphrase|secret|token|api_?key|access_?key|private_?key|credential)")]
    private static partial Regex SecretName();
}
