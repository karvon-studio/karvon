using System.Collections.Concurrent;
using System.Text;

namespace Karvon.Claude.Tools;

/// <summary>
/// The secrets Karvon holds this session (what the keyring gave it, what the user typed into a sign-in), by their
/// values, so that a text that holds one is told by the value itself: as it is, base64 or URL-encoded. Only their names
/// are ever said ("«secret: karvon-jira/acme»").
/// </summary>
public static class KnownSecrets
{
    /// <summary>Shorter values are too likely to be words or numbers of a text that holds no secret.</summary>
    public const int MinLength = 8;

    static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.Ordinal);

    /// <summary>A secret Karvon holds, by its name (a keyring key, a site's).</summary>
    public static void Add(string name, string? value)
    {
        if (value is not { Length: >= MinLength })
            return;
        Names[value] = name;
        Names.TryAdd(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), name);
        if (Uri.EscapeDataString(value) is var escaped && escaped != value)
            Names.TryAdd(escaped, name);
    }

    /// <summary>The values held and their names, the longest first (so a value inside another's encoding goes last).</summary>
    public static IEnumerable<(string Value, string Name)> All =>
        Names.Select(n => (n.Key, n.Value)).OrderByDescending(n => n.Key.Length);

    public static bool IsEmpty => Names.IsEmpty;

    /// <summary>For the tests: none held.</summary>
    internal static void Clear() => Names.Clear();
}
