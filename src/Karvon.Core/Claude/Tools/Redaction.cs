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

    [GeneratedRegex(@"(?i)\b((?:password|pwd)\s*=\s*)[^;'""\s]+")]
    private static partial Regex PasswordSetting();

    [GeneratedRegex(@"(\w+://[^:/@\s]+:)[^@\s]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)\b(bearer\s+)[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)(password|passwd|secret|token|api_?key|private_?key|credential)")]
    private static partial Regex SecretName();
}
