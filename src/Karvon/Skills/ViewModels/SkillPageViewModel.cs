using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Karvon.Skills.ViewModels;

/// <summary>A file of a skill, opened in the editor.</summary>
public sealed record SkillFile(string Name, string Path);

/// <summary>
/// A skill's page: what it says it is for, its checks, "Try a prompt" (whether a prompt gives an agent a reason to load
/// it, by its name and description), and its files.
/// </summary>
public sealed partial class SkillPageViewModel(SkillsExplorerViewModel explorer, Skill skill) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhereLine), nameof(Description), nameof(Checks), nameof(HasProblems), nameof(Files), nameof(CanRemove))]
    public partial Skill Skill { get; private set; } = skill;

    public string WhereLine => $"{char.ToUpperInvariant(Skill.Where[0])}{Skill.Where[1..]} · {Skill.Folder}";

    public string Description => Skill.Description.Length > 0 ? Skill.Description : "(no description)";

    public IReadOnlyList<SkillCheck> Checks => SkillLibrary.Checks(Skill);

    public bool HasProblems => Checks.Any(c => !c.Passed);

    public bool CanRemove => Skill.IsYours;

    /// <summary>Who loads it: Claude Code by itself; the others through the memory files.</summary>
    public string LoadedBy => Skill.Scope switch
    {
        SkillScope.Project => "Claude Code loads it from this folder by itself. The other agents (Codex, Gemini CLI) find it in the AGENTS.md and GEMINI.md Karvon writes.",
        SkillScope.User => "Claude Code loads it in every project, by itself.",
        _ => $"Claude Code loads it with the plugin {Skill.Plugin}.",
    };

    public IReadOnlyList<SkillFile> Files => Directory.Exists(Skill.Folder)
        ? [.. Directory.EnumerateFiles(Skill.Folder, "*", SearchOption.AllDirectories)
            .Where(f => !System.IO.Path.GetFileName(f).StartsWith(".karvon-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .Select(f => new SkillFile(System.IO.Path.GetRelativePath(Skill.Folder, f).Replace('\\', '/'), f))]
        : [];

    [ObservableProperty]
    public partial string Prompt { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerdict))]
    public partial string? Verdict { get; set; }

    public bool HasVerdict => Verdict is not null;

    /// <summary>The skill read again (after a refresh).</summary>
    internal void Reload(Skill skill)
    {
        Skill = skill;
        OnPropertyChanged(nameof(LoadedBy));
    }

    /// <summary>"Likely to load: the prompt and the description share ..."; and a skill that matches it better.</summary>
    [RelayCommand]
    public void Try()
    {
        var prompt = Prompt.Trim();
        if (prompt.Length == 0)
            return;
        var (verdict, shared) = SkillLibrary.Try(Skill, prompt);
        var best = explorer.Library.Skills().Select(s => (Skill: s, Score: SkillLibrary.Try(s, prompt).Shared.Count)).MaxBy(s => s.Score);
        Verdict = verdict + (shared.Count > 0 ? $": the prompt and the description share \"{string.Join("\", \"", shared)}\"." : ": they share no words that matter.")
            + (best.Skill is { } other && other.Id != Skill.Id && best.Score > shared.Count ? $" {other.Name} matches it better." : "");
    }

    [RelayCommand]
    void Edit() => explorer.Host.OpenFile(Skill.File);

    [RelayCommand]
    void OpenFile(SkillFile? file)
    {
        if (file is not null)
            explorer.Host.OpenFile(file.Path);
    }

    [RelayCommand]
    void AskClaude() => explorer.Host.AskClaude(
        $"The skill {Skill.Name} ({Skill.File}) has problems: {string.Join("; ", Checks.Where(c => !c.Passed).Select(c => c.Text))}. Fix them, keeping what the skill says.");

    [RelayCommand]
    async Task Remove()
    {
        if (!Skill.IsYours || !await explorer.ConfirmAsync($"Remove {Skill.Name}?", $"Its folder is deleted: {Skill.Folder}.", "Remove", danger: true))
            return;
        try
        {
            Directory.Delete(Skill.Folder, recursive: true);
            explorer.Host.Status($"Removed the skill {Skill.Name}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            explorer.Error = $"{Skill.Name} was not removed: {ex.Message}";
        }
        explorer.Refresh();
    }
}
