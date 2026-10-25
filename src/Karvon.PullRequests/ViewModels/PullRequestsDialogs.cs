using CommunityToolkit.Mvvm.ComponentModel;
using Karvon.Ui;

namespace Karvon.PullRequests.ViewModels;

/// <summary>A new pull request from the checked-out branch: into which branch, its title and description, a draft or not.</summary>
public sealed partial class NewPullRequestDialog(string noun, string source, string target, string title, string body)
    : OverlayDialog($"New {char.ToUpperInvariant(noun[0])}{noun[1..]}", "Create")
{
    public string Source => source;

    public string Explanation => $"{source} is pushed first, then the {noun} asks to merge it into the branch below.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string Target { get; set; } = target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string Body { get; set; } = body;

    [ObservableProperty]
    public partial bool Draft { get; set; }

    public override bool CanConfirm => Title.Trim().Length > 0 && Target.Trim().Length > 0 && Target.Trim() != source;
}
