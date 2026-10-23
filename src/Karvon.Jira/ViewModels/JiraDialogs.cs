using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;

namespace Karvon.Jira.ViewModels;

/// <summary>
/// A Jira site, added or changed: its address, how it signs in (Cloud's email and API token; Server's or Data Center's
/// personal access token, or name and password), the secret (kept in the system keyring when asked), its date fields;
/// Test connection signs in with them and says as whom.
/// </summary>
public sealed partial class JiraSiteDialog : OverlayDialog
{
    readonly Func<JiraSite, string, Task<string>> _test;

    public JiraSiteDialog(JiraSite? site, bool canKeep, bool hasSecret, Func<JiraSite, string, Task<string>> test)
        : base(site is null ? "Add Jira Site" : $"Edit {site.Name}", site is null ? "Add" : "Save")
    {
        _test = test;
        Id = site?.Id ?? Guid.NewGuid().ToString("N")[..12];
        CanKeep = canKeep;
        HasSecret = hasSecret;
        if (site is not null)
        {
            Name = site.Name;
            Url = site.Url;
            AuthIndex = (int)site.Auth;
            User = site.User;
            StartDateField = site.StartDateField ?? "";
            EndDateField = site.EndDateField ?? "";
        }
    }

    public string Id { get; }

    /// <summary>How it signs in, as the form's choice names them.</summary>
    public static IReadOnlyList<string> AuthChoices { get; } =
        ["Jira Cloud: email and API token", "Server or Data Center: personal access token", "Server or Data Center: name and password"];

    public bool CanKeep { get; }

    /// <summary>Its secret is known already (changing the site leaves it as it is unless a new one is given).</summary>
    public bool HasSecret { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(UserLabel), nameof(SecretLabel), nameof(NeedsUser), nameof(SecretHint))]
    public partial int AuthIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string User { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    public partial bool Keep { get; set; } = true;

    [ObservableProperty]
    public partial string StartDateField { get; set; } = "";

    [ObservableProperty]
    public partial string EndDateField { get; set; } = "";

    [ObservableProperty]
    public partial string? TestMessage { get; private set; }

    [ObservableProperty]
    public partial bool TestFailed { get; private set; }

    [ObservableProperty]
    public partial bool IsTesting { get; private set; }

    public JiraAuth Auth => (JiraAuth)Math.Clamp(AuthIndex, 0, 2);

    public bool NeedsUser => Auth != JiraAuth.PersonalAccessToken;

    public string UserLabel => Auth == JiraAuth.CloudToken ? "Email" : "User name";

    public string SecretLabel => Auth switch
    {
        JiraAuth.CloudToken => "API token",
        JiraAuth.PersonalAccessToken => "Personal access token",
        _ => "Password",
    };

    public string SecretHint => Auth switch
    {
        JiraAuth.CloudToken => "Made at id.atlassian.com: Security, API tokens.",
        JiraAuth.PersonalAccessToken => "Made in Jira: your profile, Personal Access Tokens.",
        _ => "Where the site lets its REST API sign in with a password.",
    };

    /// <summary>What is missing, or null.</summary>
    public string? Problem =>
        Name.Trim().Length == 0 ? "A name." :
        !Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ? "The site's address (https://...)." :
        NeedsUser && User.Trim().Length == 0 ? $"The {UserLabel.ToLowerInvariant()}." :
        Secret.Length == 0 && !HasSecret ? $"The {SecretLabel.ToLowerInvariant()}." :
        null;

    public override bool CanConfirm => Problem is null;

    /// <summary>The site the form says (its secret aside).</summary>
    public JiraSite Site => new(Id, Name.Trim(), Url.Trim().TrimEnd('/'), Auth, NeedsUser ? User.Trim() : "",
        StartDateField.Trim() is { Length: > 0 } start ? start : null, EndDateField.Trim() is { Length: > 0 } end ? end : null);

    /// <summary>The secret to sign in with: the one typed, else the one it has.</summary>
    public Func<string?>? KnownSecret { get; init; }

    [RelayCommand]
    async Task TestAsync()
    {
        if (Problem is { } problem)
        {
            (TestMessage, TestFailed) = (problem, true);
            return;
        }
        IsTesting = true;
        TestMessage = "Signing in...";
        try
        {
            TestMessage = await _test(Site, Secret.Length > 0 ? Secret : KnownSecret?.Invoke() ?? "");
            TestFailed = false;
        }
        catch (JiraException ex)
        {
            (TestMessage, TestFailed) = (ex.Message, true);
        }
        finally
        {
            IsTesting = false;
        }
    }
}

/// <summary>A site's token or password, asked when Karvon has none for it (it was not kept, or the keyring has none).</summary>
public sealed partial class JiraSecretDialog(JiraSite site, bool canKeep) : OverlayDialog($"Sign in to {site.Name}", "Sign In")
{
    public string Question { get; } = site.Auth switch
    {
        JiraAuth.CloudToken => $"The API token of {site.User} at {site.Url}.",
        JiraAuth.PersonalAccessToken => $"Your personal access token at {site.Url}.",
        _ => $"The password of {site.User} at {site.Url}.",
    };

    public bool CanKeep { get; } = canKeep;

    [ObservableProperty]
    public partial string Secret { get; set; } = "";

    [ObservableProperty]
    public partial bool Keep { get; set; } = true;

    public override bool CanConfirm => Secret.Length > 0;
}

/// <summary>
/// A new issue: its project and type (as the project has them), its summary and description, its start and end dates;
/// it is made as the signed-in user's.
/// </summary>
public sealed partial class JiraCreateDialog : OverlayDialog
{
    readonly Func<string, Task<IReadOnlyList<JiraIssueType>>> _types;

    public JiraCreateDialog(JiraSite site, IReadOnlyList<JiraProject> projects, Func<string, Task<IReadOnlyList<JiraIssueType>>> types)
        : base($"New Issue in {site.Name}", "Create")
    {
        _types = types;
        Projects = projects;
        HasDates = site.StartDateField is not null || site.EndDateField is not null;
        Project = projects.FirstOrDefault();
    }

    public IReadOnlyList<JiraProject> Projects { get; }

    public bool HasDates { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial JiraProject? Project { get; set; }

    async partial void OnProjectChanged(JiraProject? value)
    {
        Types = [];
        if (value is null)
            return;
        try
        {
            Types = [.. (await _types(value.Key)).Where(t => !t.Subtask)];
            Type = Types.FirstOrDefault();
        }
        catch (JiraException ex)
        {
            Error = ex.Message;
        }
    }

    [ObservableProperty]
    public partial IReadOnlyList<JiraIssueType> Types { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial JiraIssueType? Type { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string Start { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem))]
    public partial string End { get; set; } = "";

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public static DateOnly? DateOf(string text) => DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", out var d) ? d : null;

    public string? Problem =>
        Project is null ? "A project." :
        Type is null ? "Its type." :
        Summary.Trim().Length == 0 ? "A summary." :
        Start.Trim().Length > 0 && DateOf(Start) is null || End.Trim().Length > 0 && DateOf(End) is null ? "Dates as 2026-10-24." :
        null;

    public override bool CanConfirm => Problem is null;
}
