using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Karvon.Claude.Tools;

/// <summary>
/// The secrets Karvon holds this session (what the keyring gave it, what the user typed into a sign-in), by their
/// values, so that a text that holds one is told by the value itself: as it is, base64 or URL-encoded. Only their names
/// are ever said ("«secret: karvon-jira/acme»"). A sealed one ("«hidden: …»") is never given back either: what an agent
/// writes or runs with its mark in it does not run (<see cref="SecretMasks.Sealed"/>).
/// </summary>
public static class KnownSecrets
{
    /// <summary>Shorter values are too likely to be words or numbers of a text that holds no secret.</summary>
    public const int MinLength = 8;

    static readonly ConcurrentDictionary<string, (string Name, bool Sealed)> Names = new(StringComparer.Ordinal);

    /// <summary>A secret Karvon holds, by its name (a keyring key, a site's).</summary>
    public static void Add(string name, string? value)
    {
        if (value is not { Length: >= MinLength })
            return;
        // One that is sealed stays so, whoever else holds it.
        Names.AddOrUpdate(value, (name, false), (_, known) => known.Sealed ? known : (name, false));
        Names.TryAdd(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), (name, false));
        if (Uri.EscapeDataString(value) is var escaped && escaped != value)
            Names.TryAdd(escaped, (name, false));
    }

    /// <summary>A secret that no agent gets in any way, its mark given back neither: hidden as it is and as it is usually written.</summary>
    public static void Seal(string name, string? value)
    {
        if (value is not { Length: >= MinLength })
            return;
        foreach (var form in Forms(value))
            if (form.Length >= MinLength)
                Names[form] = (name, true);
    }

    /// <summary>A value as it is, and as texts carry it: escaped for a URL, JSON or HTML, in base64 wherever it begins, in hex.</summary>
    public static IEnumerable<string> Forms(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var forms = new HashSet<string>(StringComparer.Ordinal) { value, Uri.EscapeDataString(value), Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal) };
        forms.Add(WebUtility.UrlEncode(value));
        forms.Add(JsonEncodedText.Encode(value).ToString());
        forms.Add(JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString());
        forms.Add(WebUtility.HtmlEncode(value));
        if (bytes.Length >= 6)
        {
            forms.Add(Convert.ToHexString(bytes));
            forms.Add(Convert.ToHexStringLower(bytes));
            // Inside a longer text its base64 has one of three forms, by where it begins; their ends belong to what is beside it.
            for (var before = 0; before < 3; before++)
            {
                var coded = Convert.ToBase64String([.. new byte[before], .. bytes]).TrimEnd('=');
                var first = before == 0 ? 0 : before + 1;
                var rest = (before + bytes.Length) % 3;
                var inner = coded[first..(rest == 0 ? coded.Length : coded.Length - 1)];
                forms.Add(inner);
                forms.Add(inner.Replace('+', '-').Replace('/', '_'));
            }
            forms.Add(Convert.ToBase64String(bytes));
        }
        return forms.Where(f => f.Length > 0);
    }

    /// <summary>The values held and their names, the longest first (so a value inside another's encoding goes last).</summary>
    public static IEnumerable<(string Value, string Name)> All =>
        Names.Select(n => (n.Key, n.Value.Name)).OrderByDescending(n => n.Key.Length);

    /// <summary>The sealed ones, the longest first.</summary>
    public static IEnumerable<(string Value, string Name)> AllSealed =>
        Names.Where(n => n.Value.Sealed).Select(n => (n.Key, n.Value.Name)).OrderByDescending(n => n.Key.Length);

    public static bool IsSealed(string value) => Names.TryGetValue(value, out var known) && known.Sealed;

    /// <summary>Whether <paramref name="text"/> has a sealed value in it.</summary>
    public static bool HoldsSealed(string text) => Names.Any(n => n.Value.Sealed && text.Contains(n.Key, StringComparison.Ordinal));

    /// <summary>What stands for a value where it is hidden.</summary>
    public static string MarkOf(string value, string name) => IsSealed(value) ? $"«hidden: {name}»" : $"«secret: {name}»";

    public static bool IsEmpty => Names.IsEmpty;

    /// <summary>For the tests: none held.</summary>
    internal static void Clear() => Names.Clear();
}
