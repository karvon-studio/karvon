using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Pipelines;
using Karvon.PullRequests.Providers;

namespace Karvon.PullRequests.ViewModels;

/// <summary>A comment of the conversation (or on a line of a file).</summary>
public sealed record CommentRow(PullRequestComment Comment)
{
    public string Author => Comment.Author;

    public string Body => Comment.Body;

    public string When => PipelineStatuses.Ago(Comment.Created, DateTimeOffset.Now);

    public string? Where => Comment.Path is { Length: > 0 } path ? Comment.Line is { } line ? $"{path}:{line}" : path : null;

    public bool HasWhere => Where is not null;
}

public sealed record ReviewRow(PullRequestReview Review)
{
    public string Author => Review.Author;

    public string Text => Review.Verdict.Text();

    public string Icon => Review.Verdict switch { ReviewVerdict.Approve => "Ok", ReviewVerdict.RequestChanges => "Error", _ => "Comment" };
}

public sealed record CheckRow(PullRequestCheck Check)
{
    public string Name => Check.Name;

    public string Icon => Check.Status.Icon();

    public string StatusText => Check.Status.Text();
}

public sealed record FileRow(PullRequestFile File)
{
    public string Letter => File.Letter;

    public string Path => File.Path;

    public string Name => System.IO.Path.GetFileName(File.Path);

    public string Folder => System.IO.Path.GetDirectoryName(File.Path)?.Replace('\\', '/') ?? "";

    public string Counts => $"+{File.Additions} −{File.Deletions}";
}

public sealed record MergeChoice(MergeMethod Method, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// A pull request's page: what it asks (its branch into the target), its description, the conversation, the reviewers'
/// say and its checks; its files, each opening as a diff; and what a reviewer does: comment, approve, ask for changes,
/// merge (merge, squash or rebase; the branch deleted, if asked), close, check it out, or ask Claude to review it.
/// </summary>
public sealed partial class PullRequestPageViewModel : ObservableObject, IDisposable
{
    readonly PullRequestsViewModel _window;
    readonly IPullRequestProvider _provider;

    public PullRequestPageViewModel(PullRequestsViewModel window, PullRequestRepo repo, PullRequest pr)
    {
        _window = window;
        Repo = repo;
        _provider = repo.Provider ?? throw new InvalidOperationException("The repository's service is not connected.");
        PullRequest = pr;
        MergeChoices = _provider.CanRebase
            ? [new(MergeMethod.Merge, "Create a Merge Commit"), new(MergeMethod.Squash, "Squash and Merge"), new(MergeMethod.Rebase, "Rebase and Merge")]
            : [new(MergeMethod.Merge, "Merge"), new(MergeMethod.Squash, "Squash and Merge")];
        Merge = MergeChoices[0];
    }

    public PullRequestRepo Repo { get; }

    public string Prefix => _provider.Prefix;

    public string Short => _provider.Short;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading), nameof(Facts), nameof(Icon), nameof(StateText), nameof(IsOpen), nameof(IsMerged), nameof(IsClosed),
        nameof(IsDraft), nameof(Body), nameof(HasBody), nameof(MergeNote), nameof(HasMergeNote), nameof(CanMerge), nameof(Labels), nameof(HasLabels))]
    public partial PullRequest PullRequest { get; private set; }

    public string Heading => $"{PullRequest.Title} {Prefix}{PullRequest.Number}";

    public string Facts =>
        $"{PullRequest.Author} wants to merge {PullRequest.SourceBranch} into {PullRequest.TargetBranch} · opened {PipelineStatuses.Ago(PullRequest.Created, DateTimeOffset.Now)}"
        + (PullRequest.ChangedFiles > 0 ? $" · {PullRequest.ChangedFiles} file{(PullRequest.ChangedFiles == 1 ? "" : "s")}" : "")
        + (PullRequest.Additions + PullRequest.Deletions > 0 ? $" (+{PullRequest.Additions} −{PullRequest.Deletions})" : "");

    public string Icon => PullRequest.State.Icon();

    public string StateText => PullRequest.State switch
    {
        PullRequestState.Open => "Open",
        PullRequestState.Draft => "Draft",
        PullRequestState.Merged => "Merged",
        _ => "Closed",
    };

    public bool IsOpen => PullRequest.IsOpen;

    public bool IsMerged => PullRequest.State == PullRequestState.Merged;

    public bool IsClosed => PullRequest.State == PullRequestState.Closed;

    public bool IsDraft => PullRequest.State == PullRequestState.Draft;

    public string Body => PullRequest.Body.Length > 0 ? PullRequest.Body : "*No description.*";

    public bool HasBody => true;

    public string Labels => string.Join("  ", PullRequest.Labels);

    public bool HasLabels => PullRequest.Labels.Count > 0;

    public string? MergeNote => PullRequest.MergeNote;

    public bool HasMergeNote => IsOpen && MergeNote is not null;

    /// <summary>Open and not a draft, nor known not to merge.</summary>
    public bool CanMerge => PullRequest.State == PullRequestState.Open && PullRequest.Mergeable != false;

    public ObservableCollection<CommentRow> Comments { get; } = [];

    public ObservableCollection<ReviewRow> Reviews { get; } = [];

    public ObservableCollection<CheckRow> Checks { get; } = [];

    public ObservableCollection<FileRow> Files { get; } = [];

    public bool HasReviews => Reviews.Count > 0;

    public bool HasChecks => Checks.Count > 0;

    public string FilesHeader => $"Files Changed ({Files.Count})";

    public string ChecksHeader => $"Checks ({Checks.Count})";

    public string ReviewersText => PullRequest.Reviewers.Count > 0 ? "Review requested: " + string.Join(", ", PullRequest.Reviewers) : "";

    public IReadOnlyList<MergeChoice> MergeChoices { get; }

    [ObservableProperty]
    public partial MergeChoice Merge { get; set; }

    [ObservableProperty]
    public partial bool DeleteBranch { get; set; } = true;

    /// <summary>The comment (or review) being written.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommentCommand), nameof(RequestChangesCommand))]
    public partial string NewComment { get; set; } = "";

    bool HasComment => NewComment.Trim().Length > 0;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The tab shown: conversation, files or checks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConversation), nameof(IsFiles), nameof(IsChecks))]
    public partial string Tab { get; set; } = "conversation";

    public bool IsConversation => Tab == "conversation";

    public bool IsFiles => Tab == "files";

    public bool IsChecks => Tab == "checks";

    [RelayCommand]
    void ShowTab(string? tab) => Tab = tab ?? "conversation";

    [RelayCommand]
    void OpenCheck(CheckRow? row)
    {
        if (row?.Check.Url is { Length: > 0 } url)
            _window.Host.OpenUrl(url);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            PullRequest = await _provider.GetAsync(PullRequest.Number, default);
            var details = await _provider.DetailsAsync(PullRequest, default);
            var files = await _provider.FilesAsync(PullRequest, default);
            Replace(Comments, details.Comments.Select(c => new CommentRow(c)));
            Replace(Reviews, details.Reviews.Select(r => new ReviewRow(r)));
            Replace(Checks, details.Checks.Select(c => new CheckRow(c)));
            Replace(Files, files.Select(f => new FileRow(f)));
            OnPropertyChanged(nameof(HasReviews));
            OnPropertyChanged(nameof(HasChecks));
            OnPropertyChanged(nameof(FilesHeader));
            OnPropertyChanged(nameof(ChecksHeader));
            OnPropertyChanged(nameof(ReviewersText));
            Error = null;
        }
        catch (Exception ex) when (ex is PipelineException or IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    static void Replace<T>(ObservableCollection<T> items, IEnumerable<T> with)
    {
        items.Clear();
        foreach (var item in with)
            items.Add(item);
    }

    /// <summary>A file against its version before the change (the merge base's, from the patch; else the target's).</summary>
    [RelayCommand]
    public async Task OpenFileAsync(FileRow? row)
    {
        if (row?.File is not { } file)
            return;
        try
        {
            var after = file.Letter == "D" ? "" : await _provider.FileAtAsync(PullRequest.HeadSha, file.Path, default) ?? "";
            var before = file.Letter == "A" ? ""
                : UnifiedPatch.Reverse(after, file.Patch) ?? await _provider.FileAtAsync(PullRequest.BaseSha, file.OldPath ?? file.Path, default) ?? "";
            var name = Path.GetFileName(file.Path);
            _window.Host.ShowDiff(Path.Combine(Repo.Repository.Root, file.Path), before, after,
                $"{Path.GetFileName(file.OldPath ?? file.Path)};{PullRequest.TargetBranch}", $"{name};{Prefix}{PullRequest.Number}",
                $"pull-request:{Repo.Name}{Prefix}{PullRequest.Number}:{file.Path}");
        }
        catch (Exception ex) when (ex is PipelineException or IOException or HttpRequestException)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasComment))]
    Task CommentAsync() => ActAsync(() => _provider.CommentAsync(PullRequest, NewComment.Trim(), default), "Comment added.", clear: true);

    [RelayCommand]
    Task ApproveAsync() => ActAsync(() => _provider.ReviewAsync(PullRequest, ReviewVerdict.Approve, NewComment.Trim(), default), "Approved.", clear: true);

    [RelayCommand(CanExecute = nameof(HasComment))]
    Task RequestChangesAsync() =>
        ActAsync(() => _provider.ReviewAsync(PullRequest, ReviewVerdict.RequestChanges, NewComment.Trim(), default), "Changes requested.", clear: true);

    [RelayCommand]
    async Task MergeAsync()
    {
        var method = Merge;
        if (!await _window.ConfirmAsync($"{method.Text}?",
                $"Merge {Prefix}{PullRequest.Number} ({PullRequest.SourceBranch}) into {PullRequest.TargetBranch}"
                + (DeleteBranch && !PullRequest.IsFromFork ? $", and delete {PullRequest.SourceBranch} on the remote?" : "?"), "Merge"))
            return;
        await ActAsync(() => _provider.MergeAsync(PullRequest, method.Method, DeleteBranch, default), $"{Short} {Prefix}{PullRequest.Number} merged.");
    }

    [RelayCommand]
    async Task CloseAsync()
    {
        if (!await _window.ConfirmAsync($"Close {Prefix}{PullRequest.Number}?", $"Close \"{PullRequest.Title}\" without merging it?", "Close", danger: true))
            return;
        await ActAsync(() => _provider.CloseAsync(PullRequest, default), $"{Short} {Prefix}{PullRequest.Number} closed.");
    }

    [RelayCommand]
    async Task CheckoutAsync()
    {
        try
        {
            await _window.CheckoutAsync(PullRequest);
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    void OpenInBrowser()
    {
        if (PullRequest.Url.Length > 0)
            _window.Host.OpenUrl(PullRequest.Url);
    }

    /// <summary>The pull request as Claude Code's task: it reads it with its pull request tools and says what it finds.</summary>
    [RelayCommand]
    void AskClaude() => _window.Host.AskClaude(
        $"Review {_provider.Noun} {Prefix}{PullRequest.Number} (\"{PullRequest.Title}\", {PullRequest.SourceBranch} into {PullRequest.TargetBranch}) "
        + "of this repository: read it with mcp__karvon__pr_view and its changes with mcp__karvon__pr_diff, then tell me what is wrong or risky "
        + "and what you would change, file by file. Do not comment, approve or merge unless I ask.");

    async Task ActAsync(Func<Task> action, string done, bool clear = false)
    {
        IsBusy = true;
        try
        {
            await action();
            _window.Host.Status(done);
            if (clear)
                NewComment = "";
            Error = null;
        }
        catch (Exception ex) when (ex is PipelineException or IOException or HttpRequestException or TaskCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync();
        _ = _window.RefreshAsync();
    }

    public void Dispose()
    {
    }
}
