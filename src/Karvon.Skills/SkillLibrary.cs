using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Karvon.Skills;

/// <summary>Where a skill, subagent or command comes from.</summary>
public enum SkillScope
{
    Project,
    User,
    Plugin,
}

/// <summary>A skill: a folder with a <c>SKILL.md</c> (frontmatter <c>name</c> and <c>description</c>, then instructions).</summary>
public sealed record Skill(string Name, string Description, SkillScope Scope, string Folder, string? Plugin)
{
    public string File => Path.Combine(Folder, "SKILL.md");

    /// <summary>Unique across scopes, by folder: <c>project:release-notes</c>, <c>plugin:docs:pdf</c>.</summary>
    public string Id => Scope == SkillScope.Plugin
        ? $"plugin:{Plugin}:{Path.GetFileName(Folder)}"
        : $"{Scope.ToString().ToLowerInvariant()}:{Path.GetFileName(Folder)}";

    public string Where => Scope switch
    {
        SkillScope.Project => "this folder",
        SkillScope.User => "your skills",
        _ => "plugin " + Plugin,
    };

    /// <summary>A plugin's skill is the plugin's: it is changed (and removed) with the plugin.</summary>
    public bool IsYours => Scope != SkillScope.Plugin;
}

/// <summary>A Claude Code subagent or command: a Markdown file with frontmatter.</summary>
public sealed record AgentFile(string Name, string Description, SkillScope Scope, string File);

/// <summary>A hook from a Claude Code settings file: an event, what it matches, and the command it runs.</summary>
public sealed record AgentHook(string Event, string Matcher, string Command, string File);

/// <summary>One check of a skill, and whether it passed.</summary>
public sealed record SkillCheck(string Text, bool Passed);

/// <summary>
/// The skills, subagents, commands and hooks Claude Code reads: the folder's (<c>.claude</c>), the user's (Claude
/// Code's configuration folder: ~/.claude, or the account's) and the installed plugins'.
/// </summary>
public sealed partial class SkillLibrary(string? workspace, string claudeDirectory)
{
    public string? Workspace { get; } = workspace is { Length: > 0 } ? workspace : null;

    /// <summary>Claude Code's configuration folder: its skills, agents, commands, settings and plugins.</summary>
    public string ClaudeDirectory { get; } = claudeDirectory;

    string? ProjectClaude => Workspace is null ? null : Path.Combine(Workspace, ".claude");

    public string? ProjectSkills => ProjectClaude is { } project ? Path.Combine(project, "skills") : null;

    public string UserSkills => Path.Combine(ClaudeDirectory, "skills");

    public IReadOnlyList<Skill> Skills()
    {
        var skills = new List<Skill>();
        if (ProjectSkills is { } project)
            skills.AddRange(SkillsIn(project, SkillScope.Project, null));
        skills.AddRange(SkillsIn(UserSkills, SkillScope.User, null));
        foreach (var (plugin, folder) in PluginSkillFolders())
            skills.AddRange(SkillsIn(folder, SkillScope.Plugin, plugin));
        return skills;
    }

    static IEnumerable<Skill> SkillsIn(string folder, SkillScope scope, string? plugin)
    {
        if (!Directory.Exists(folder))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file))
                continue;
            var front = Frontmatter.Read(ReadText(file));
            yield return new Skill(front.GetValueOrDefault("name") ?? Path.GetFileName(dir), front.GetValueOrDefault("description") ?? "", scope, dir, plugin);
        }
    }

    static string ReadText(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The installed plugins' <c>skills</c> folders (Claude Code keeps their copies under <c>plugins/cache</c>).</summary>
    IEnumerable<(string Plugin, string Folder)> PluginSkillFolders()
    {
        var root = Path.Combine(ClaudeDirectory, "plugins", "cache");
        if (!Directory.Exists(root))
            yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true };
        foreach (var folder in Directory.EnumerateDirectories(root, "skills", options).Order(StringComparer.Ordinal))
        {
            // cache/<marketplace>/<plugin>/<version>/skills, or cache/<plugin>/skills.
            var parts = Path.GetRelativePath(root, folder).Split(Path.DirectorySeparatorChar);
            yield return (parts.Length >= 3 ? parts[1] : parts[0], folder);
        }
    }

    public IReadOnlyList<AgentFile> Subagents() => AgentFiles("agents", recursive: false);

    public IReadOnlyList<AgentFile> Commands() => AgentFiles("commands", recursive: true);

    IReadOnlyList<AgentFile> AgentFiles(string kind, bool recursive)
    {
        var files = new List<AgentFile>();
        foreach (var (root, scope) in new[] { (ProjectClaude, SkillScope.Project), (ClaudeDirectory, SkillScope.User) })
        {
            var folder = root is null ? null : Path.Combine(root, kind);
            if (folder is null || !Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder, "*.md", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                var text = ReadText(file);
                var front = Frontmatter.Read(text);
                // A command's name is its path: frontend/test.md is /frontend:test.
                var name = kind == "commands"
                    ? "/" + Path.ChangeExtension(Path.GetRelativePath(folder, file), null).Replace(Path.DirectorySeparatorChar, ':')
                    : front.GetValueOrDefault("name") ?? Path.GetFileNameWithoutExtension(file);
                var description = front.GetValueOrDefault("description") ?? Frontmatter.Body(text).Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
                files.Add(new AgentFile(name, description, scope, file));
            }
        }
        return files;
    }

    /// <summary>The hooks of the folder's and the user's Claude Code settings.</summary>
    public IReadOnlyList<AgentHook> Hooks()
    {
        var hooks = new List<AgentHook>();
        var files = new List<string>();
        if (ProjectClaude is { } project)
            files.AddRange([Path.Combine(project, "settings.json"), Path.Combine(project, "settings.local.json")]);
        files.Add(Path.Combine(ClaudeDirectory, "settings.json"));
        foreach (var file in files.Where(File.Exists))
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(ReadText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException)
            {
                continue;
            }
            if (root?["hooks"] is not JsonObject events)
                continue;
            foreach (var (name, groups) in events)
                foreach (var group in groups as JsonArray ?? [])
                    foreach (var hook in group?["hooks"] as JsonArray ?? [])
                        hooks.Add(new AgentHook(name, Str(group, "matcher") is { Length: > 0 } matcher ? matcher : "*", Str(hook, "command") ?? Str(hook, "type") ?? "", file));
        }
        return hooks;
    }

    static string? Str(JsonNode? node, string name) => node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    // ---- Checks ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What makes a skill load and work: the name matches the folder and the rules for names, the description says when
    /// to use it, the files it links to exist, its scripts can run.
    /// </summary>
    public static IReadOnlyList<SkillCheck> Checks(Skill skill)
    {
        var checks = new List<SkillCheck>();
        var folder = Path.GetFileName(skill.Folder);
        checks.Add(skill.Name == folder
            ? new SkillCheck("The name matches the folder", true)
            : new SkillCheck($"The name \"{skill.Name}\" is not the folder's, \"{folder}\"", false));
        checks.Add(NameRule().IsMatch(skill.Name) && skill.Name.Length <= 64
            ? new SkillCheck("The name is lowercase words with hyphens", true)
            : new SkillCheck("The name should be lowercase letters, digits and hyphens, at most 64", false));
        checks.Add(skill.Description.Length == 0 ? new SkillCheck("There is no description: agents choose skills by it", false)
            : skill.Description.Length > 1024 ? new SkillCheck($"The description is {skill.Description.Length} characters; at most 1024 are read", false)
            : SaysWhen().IsMatch(skill.Description) ? new SkillCheck("The description says when to use it", true)
            : new SkillCheck("The description does not say when to use it (\"Use when…\")", false));

        var body = File.Exists(skill.File) ? ReadText(skill.File) : "";
        var missing = Link().Matches(body).Select(m => m.Groups[1].Value)
            .Where(l => !l.Contains("://", StringComparison.Ordinal) && !l.StartsWith("mailto:", StringComparison.Ordinal))
            .Where(l => !File.Exists(Path.Combine(skill.Folder, l)) && !Directory.Exists(Path.Combine(skill.Folder, l)))
            .Distinct().ToList();
        checks.Add(missing.Count == 0 ? new SkillCheck("Linked files exist", true) : new SkillCheck("Missing linked files: " + string.Join(", ", missing), false));

        if (!OperatingSystem.IsWindows() && Directory.Exists(skill.Folder))
        {
            var scripts = Directory.EnumerateFiles(skill.Folder, "*", SearchOption.AllDirectories).Where(IsScript).ToList();
            var stuck = scripts.Where(s => !File.GetUnixFileMode(s).HasFlag(UnixFileMode.UserExecute)).Select(s => Path.GetRelativePath(skill.Folder, s)).ToList();
            if (scripts.Count > 0)
                checks.Add(stuck.Count == 0 ? new SkillCheck("Scripts are executable", true) : new SkillCheck("Scripts that cannot run (chmod +x): " + string.Join(", ", stuck), false));
        }
        return checks;
    }

    static bool IsScript(string file)
    {
        if (Path.GetExtension(file) is ".sh" or ".bash")
            return true;
        try
        {
            using var stream = File.OpenRead(file);
            return stream.ReadByte() == '#' && stream.ReadByte() == '!';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ---- Try a prompt -------------------------------------------------------------------------------------------

    static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "to", "in", "on", "for", "with", "is", "are", "be", "it", "this", "that", "my", "me",
        "i", "you", "your", "we", "our", "can", "please", "how", "what", "do", "does", "use", "when", "skill", "from", "by", "at",
        "as", "if", "any", "all", "every", "about", "into", "should", "would", "could", "will", "these", "those", "them", "its",
    };

    /// <summary>
    /// Whether an agent has a reason to load the skill for a prompt: the words the prompt shares with the skill's name and
    /// description. Agents decide with a model; this says whether the description gives them the reason.
    /// </summary>
    public static (string Verdict, IReadOnlyList<string> Shared) Try(Skill skill, string prompt)
    {
        var words = Words(prompt);
        var shared = Words(skill.Name.Replace('-', ' ') + " " + skill.Description).Intersect(words).Order(StringComparer.Ordinal).ToList();
        var named = prompt.Contains(skill.Name, StringComparison.OrdinalIgnoreCase);
        var verdict = named || shared.Count >= 3 ? "Likely to load" : shared.Count >= 1 ? "Might load" : "Unlikely to load";
        return (verdict, shared);
    }

    static HashSet<string> Words(string text) =>
        [.. WordPattern().Matches(text.ToLowerInvariant()).Select(m => Stem(m.Value)).Where(w => w.Length > 2 && !Stop.Contains(w))];

    static string Stem(string word) =>
        word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 4 ? word[..^3] + "y"
        : word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 5 ? word[..^3]
        : word.EndsWith("ed", StringComparison.Ordinal) && word.Length > 4 ? word[..^2]
        : word.EndsWith("es", StringComparison.Ordinal) && word.Length > 4 ? word[..^2]
        : word.EndsWith('s') && !word.EndsWith("ss", StringComparison.Ordinal) && word.Length > 3 ? word[..^1]
        : word;

    /// <summary>A new skill from a template, in the folder's skills or the user's; its SKILL.md.</summary>
    public string Create(string name, bool forFolder)
    {
        var root = forFolder ? ProjectSkills ?? throw new InvalidOperationException("Open a solution to add a skill to its folder.") : UserSkills;
        var file = Path.Combine(root, name, "SKILL.md");
        if (!File.Exists(file))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"---\nname: {name}\ndescription: What this skill does. Use when …\n---\n\n# {name}\n\nInstructions for the agent.\n");
        }
        return file;
    }

    /// <summary>"Release Notes!" as a skill's name: release-notes.</summary>
    public static string NameFor(string text) =>
        string.Join("-", WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value));

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex NameRule();

    [GeneratedRegex(@"\b(when|whenever|use (it|this)|use for|for (any|every|all)|before|after)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SaysWhen();

    [GeneratedRegex(@"\]\(([^)\s#]+)(#[^)]*)?\)")]
    private static partial Regex Link();

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex WordPattern();
}

/// <summary>The YAML frontmatter of skills and agent files: <c>key: value</c> lines, quoted values, and <c>&gt;</c> or <c>|</c> blocks.</summary>
public static class Frontmatter
{
    public static Dictionary<string, string> Read(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
            return values;
        for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
                continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value is ">" or "|" or ">-" or "|-")
            {
                var block = new List<string>();
                while (i + 1 < lines.Length && (lines[i + 1].StartsWith(' ') || lines[i + 1].Length == 0) && lines[i + 1].Trim() != "---")
                    block.Add(lines[++i].Trim());
                value = string.Join(value.StartsWith('>') ? " " : "\n", block).Trim();
            }
            else if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
                value = value[1..^1];
            values[key] = value;
        }
        return values;
    }

    /// <summary>The text after the frontmatter.</summary>
    public static string Body(string text)
    {
        var normal = text.Replace("\r\n", "\n");
        if (!normal.StartsWith("---\n", StringComparison.Ordinal))
            return normal;
        var end = normal.IndexOf("\n---", 4, StringComparison.Ordinal);
        return end < 0 ? normal : normal[(normal.IndexOf('\n', end + 1) is var next and >= 0 ? next + 1 : normal.Length)..];
    }
}
