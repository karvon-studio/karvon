using System.Text.RegularExpressions;

namespace Karvon.Claude.Tools;

/// <summary>Secrets never leave Karvon's tools: passwords in connection strings and URLs, bearer tokens, secret-looking variables.</summary>
public static partial class Redaction
{
    public const string Mask = "***";

    public static string Text(string text)
    {
        text = PasswordSetting().Replace(text, m => m.Groups[1].Value + Mask);
        text = UrlCredentials().Replace(text, m => m.Groups[1].Value + Mask + "@");
        return BearerToken().Replace(text, m => m.Groups[1].Value + Mask);
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
        PasswordSetting().IsMatch(value) || UrlCredentials().IsMatch(value) || BearerToken().IsMatch(value) || KnownToken().IsMatch(value);

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|glpat-[A-Za-z0-9_-]{20,}|xox[abposr]-[A-Za-z0-9-]{10,}|[rs]k_live_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|sk-(?:ant-|proj-)?[A-Za-z0-9_-]{32,})|-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----")]
    private static partial Regex KnownToken();

    [GeneratedRegex(@"(?i)\b((?:password|pwd)\s*=\s*)[^;'""\s]+")]
    private static partial Regex PasswordSetting();

    [GeneratedRegex(@"(\w+://[^:/@\s]+:)[^@\s]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)\b(bearer\s+)[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)(password|passwd|passphrase|secret|token|api_?key|access_?key|private_?key|credential)")]
    private static partial Regex SecretName();
}
