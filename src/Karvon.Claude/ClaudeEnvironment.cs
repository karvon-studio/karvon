using Karvon.Claude.Tools;

namespace Karvon.Claude;

/// <summary>
/// The environment Claude Code is started with: Karvon's own, without the variables that hold a secret (by their name
/// or their value), so that what runs in Claude's shell cannot print them. Claude Code's own sign-in stays (its
/// ANTHROPIC_ and CLAUDE_ variables, and the cloud's credentials when it goes through Bedrock), as do the variables
/// that only point at an agent (SSH_AUTH_SOCK, GPG_AGENT_INFO), which use a key without giving it.
/// </summary>
public static class ClaudeEnvironment
{
    static readonly string[] OwnPrefixes = ["ANTHROPIC_", "CLAUDE_"];

    static readonly string[] Bedrock = ["AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN", "AWS_BEARER_TOKEN_BEDROCK"];

    /// <summary>The variables that hold a secret taken out of <paramref name="environment"/>; their names, sorted.</summary>
    public static IReadOnlyList<string> Clean(IDictionary<string, string?> environment)
    {
        var bedrock = environment.TryGetValue("CLAUDE_CODE_USE_BEDROCK", out var use) && use is { Length: > 0 } and not "0" and not "false";
        var removed = environment
            .Where(v => v.Value is { Length: > 0 } value && !Kept(v.Key, bedrock) && (Redaction.IsSecretName(v.Key) || Redaction.HoldsSecret(value)))
            .Select(v => v.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var name in removed)
            environment.Remove(name);
        return removed;
    }

    static bool Kept(string name, bool bedrock) =>
        OwnPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) || bedrock && Bedrock.Contains(name, StringComparer.Ordinal);
}
