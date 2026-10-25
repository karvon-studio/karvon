using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Ui;

namespace Karvon.Pipelines.ViewModels;

/// <summary>
/// A CI service's token, by host (GitLab's personal access token; GitHub's when gh is not signed in): kept in the system
/// keyring when asked. Its page makes one with the scopes needed.
/// </summary>
public sealed partial class PipelineTokenDialog(RepoRemote remote, bool canKeep, bool hasToken, string? explanation = null)
    : OverlayDialog($"{remote.ProviderName} on {remote.Authority}", "Save")
{
    public RepoRemote Remote => remote;

    public bool CanKeep => canKeep;

    public bool HasToken => hasToken;

    public string Explanation => explanation ?? (remote.Provider == CiProvider.GitHub
        ? "Karvon reads GitHub Actions through your gh CLI when it is signed in (gh auth login). Without it, give a token with the repo and workflow scopes."
        : $"A personal access token of {remote.Authority}: read_api to watch the pipelines, api to retry and cancel them.");

    public string TokenPage => PipelineConnector.TokenPage(remote);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    public partial string Token { get; set; } = "";

    [ObservableProperty]
    public partial bool Keep { get; set; } = true;

    /// <summary>The kept token is to be forgotten.</summary>
    public bool Forget { get; private set; }

    public override bool CanConfirm => Token.Trim().Length > 0;

    public event Action<string>? OpenPage;

    [RelayCommand]
    void ShowTokenPage() => OpenPage?.Invoke(TokenPage);

    [RelayCommand]
    void ForgetToken()
    {
        Forget = true;
        Token = "-";
        Confirm();
    }
}
