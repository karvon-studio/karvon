using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Docker.ViewModels;

namespace Karvon.Mcp.ViewModels;

/// <summary>A file a new server can go in: its origin, a label, and the file.</summary>
public sealed record McpWhere(McpOrigin Origin, string Label, string File)
{
    public override string ToString() => Label;
}

/// <summary>
/// Add MCP Server / Edit: its name, how Karvon reaches it (a program it starts, with its environment; or a URL, with its
/// headers) and, for a new one, which file keeps it. ${VAR} stays in the file and is filled in when it connects.
/// </summary>
public sealed partial class McpServerFormDialog : DockerDialog
{
    readonly Func<string, McpOrigin, bool> _isFree;

    public McpServerFormDialog(McpServerDefinition? existing, IReadOnlyList<McpWhere> places, McpWhere? where, Func<string, McpOrigin, bool> isFree)
        : base(existing is null ? "Add an MCP server" : $"Edit {existing.Name}", existing is null ? "Add" : "Save")
    {
        _isFree = isFree;
        Places = places;
        Where = where ?? places[0];
        IsNew = existing is null;
        if (existing is null)
            return;
        Name = existing.Name;
        IsHttp = existing.IsHttp;
        IsSse = existing.IsSse;
        CommandLine = existing.IsHttp ? "" : existing.CommandLine;
        Url = existing.Url ?? "";
        Environment = string.Join("\n", existing.Env.Select(p => $"{p.Key}={p.Value}"));
        Headers = string.Join("\n", existing.Headers.Select(p => $"{p.Key}: {p.Value}"));
    }

    public bool IsNew { get; }

    public IReadOnlyList<McpWhere> Places { get; }

    public string Subtitle => IsNew
        ? "Karvon writes it in the chosen file, in that agent's own format; the agents that read the file have it."
        : $"In {McpConfigs.Describe(Where.Origin)}: {Where.File}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(Subtitle))]
    public partial McpWhere Where { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Name { get; set; } = "";

    /// <summary>A URL (HTTP) rather than a program Karvon starts (stdio).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(IsStdio))]
    public partial bool IsHttp { get; set; }

    public bool IsStdio
    {
        get => !IsHttp;
        set => IsHttp = !value;
    }

    /// <summary>Only the older SSE transport (otherwise Karvon tries streamable HTTP, then SSE).</summary>
    [ObservableProperty]
    public partial bool IsSse { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string CommandLine { get; set; } = "";

    /// <summary>One NAME=value a line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Environment { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Url { get; set; } = "";

    /// <summary>One "Name: value" a line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Headers { get; set; } = "";

    /// <summary>What is wrong with the form, shown under it; null when it can be saved.</summary>
    public string? Problem
    {
        get
        {
            var name = Name.Trim();
            if (name.Length == 0 || name.Any(c => char.IsWhiteSpace(c) || c is '.' or '"'))
                return "Give it a name without spaces, dots or quotes.";
            if (!_isFree(name, Where.Origin))
                return $"There is a server named {name} in {Where.File} already.";
            if (IsHttp)
            {
                if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    return "The URL must start with http:// or https://.";
                return Lines(Headers, ':') is null ? "Each header line is Name: value." : null;
            }
            if (McpConfigs.SplitCommandLine(CommandLine).Count == 0)
                return "Write the command that starts the server.";
            return Lines(Environment, '=') is null ? "Each environment line is NAME=value." : null;
        }
    }

    public override bool CanConfirm => Problem is null;

    /// <summary>NAME=value (or Name: value) lines; null when one is not.</summary>
    static Dictionary<string, string>? Lines(string text, char separator)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var at = line.IndexOf(separator);
            if (at <= 0)
                return null;
            map[line[..at].Trim()] = line[(at + 1)..].Trim();
        }
        return map;
    }

    public McpServerDefinition ToDefinition(McpOrigin origin)
    {
        var words = McpConfigs.SplitCommandLine(CommandLine);
        return IsHttp
            ? new McpServerDefinition(origin, Name.Trim()) { Url = new Uri(Url.Trim()).ToString(), Headers = Lines(Headers, ':') ?? [], IsSse = IsSse }
            : new McpServerDefinition(origin, Name.Trim()) { Command = words[0], Args = words.Skip(1).ToList(), Env = Lines(Environment, '=') ?? [] };
    }
}
