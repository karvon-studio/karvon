using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Karvon.Claude;

namespace Karvon.ViewModels;

/// <summary>
/// Several Claude Code accounts (subscriptions), each a configuration folder of its own (~/.claude, ~/.claude-NAME), as
/// with <c>CLAUDE_CONFIG_DIR=~/.claude-work claude</c> in a terminal: pick one, or add one and sign in to it.
/// </summary>
public sealed partial class ClaudeViewModel
{
    bool _loadingAccounts;

    /// <summary>The last switch to another account: its fresh conversation.</summary>
    Task _switching = Task.CompletedTask;

    /// <summary>The account chosen is signed in to first: no CLI of its starts meanwhile (both would write its settings).</summary>
    bool _signInFirst;

    public ObservableCollection<ClaudeAccount> Accounts { get; } = [];

    /// <summary>The account conversations run with; changing it starts a fresh conversation there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountName), nameof(HasSeveralAccounts))]
    public partial ClaudeAccount? SelectedAccount { get; set; }

    public string AccountName => SelectedAccount?.Name ?? "Default";

    /// <summary>The picker only matters once there is more than one account.</summary>
    public bool HasSeveralAccounts => Accounts.Count > 1;

    [ObservableProperty]
    public partial bool IsAccountsOpen { get; set; }

    /// <summary>The name typed for a new account.</summary>
    [ObservableProperty]
    public partial string NewAccountName { get; set; } = "";

    [ObservableProperty]
    public partial string? AccountMessage { get; set; }

    /// <summary>The CLI's own sign-in to an account, where the host can show it (a terminal), until it ends: its exit code.</summary>
    public Func<ClaudeAccount, Task<int>>? SignInWith { get; set; }

    public bool CanSignIn => SignInWith is not null;

    void LoadAccounts()
    {
        _loadingAccounts = true;
        var chosen = SelectedAccount?.ConfigDirectory ?? _settings.ClaudeAccount;
        Accounts.Clear();
        foreach (var account in ClaudeAccounts.Discover())
            Accounts.Add(account);
        SelectedAccount = Accounts.FirstOrDefault(a => chosen is not null && ClaudeAccount.SamePath(a.ConfigDirectory, chosen))
            ?? ClaudeAccounts.Current(Accounts);
        OnPropertyChanged(nameof(HasSeveralAccounts));
        _loadingAccounts = false;
    }

    partial void OnSelectedAccountChanged(ClaudeAccount? oldValue, ClaudeAccount? newValue)
    {
        if (_loadingAccounts || newValue is null || oldValue is not null && ClaudeAccount.SamePath(oldValue.ConfigDirectory, newValue.ConfigDirectory))
            return;
        _settings.ClaudeAccount = newValue.ConfigDirectory;
        _settings.Save();
        _switching = SwitchAccountAsync();
    }

    /// <summary>Conversations, history, usage and MCP servers belong to an account: start fresh in the new one.</summary>
    async Task SwitchAccountAsync()
    {
        await ResetAsync();
        Account = null;
        _account = default;
        Usage = null;
        ShowLimits([]);
        _mcpServers = [];
        McpGroups.Clear();
        if (!_signInFirst)
            WarmUp();
        FocusInputRequested?.Invoke();
    }

    [RelayCommand]
    void ToggleAccounts()
    {
        IsAccountsOpen = !IsAccountsOpen;
        if (!IsAccountsOpen)
            return;
        IsModesOpen = false;
        AccountMessage = null;
        // Picks up accounts signed in or added from a terminal meanwhile.
        LoadAccounts();
    }

    /// <summary>The account conversations run with from now on; one not signed in yet is signed in to (not the default one: it may use an API key).</summary>
    [RelayCommand]
    async Task SelectAccount(ClaudeAccount? account)
    {
        if (account is null)
            return;
        IsAccountsOpen = false;
        _signInFirst = !account.IsSignedIn && !account.IsDefault && CanSignIn;
        SelectedAccount = account;
        await _switching;
        if (_signInFirst)
            await SignIn(account);
    }

    /// <summary>Signs in to an account (again, when it is): the conversation goes on with it when it is the one chosen.</summary>
    [RelayCommand]
    async Task SignIn(ClaudeAccount? account)
    {
        CheckCli();
        if (account is null || CliPath is null || SignInWith is not { } signIn)
            return;
        IsAccountsOpen = false;
        Items.Add(new NoticeChatItem($"Signing in to the {account.Name} account in the Terminal window: finish it in the browser that opens.", isError: false));
        int exit;
        try
        {
            exit = await signIn(account);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Items.Add(new NoticeChatItem("Could not open a terminal to sign in: " + ex.Message, isError: true));
            return;
        }
        finally
        {
            _signInFirst = false;
        }
        LoadAccounts();
        if (exit != 0 || Accounts.FirstOrDefault(a => ClaudeAccount.SamePath(a.ConfigDirectory, account.ConfigDirectory)) is not { IsSignedIn: true })
        {
            Items.Add(new NoticeChatItem($"The sign-in to the {account.Name} account was not finished.", isError: true));
            return;
        }
        var chosen = SelectedAccount is { } selected && ClaudeAccount.SamePath(selected.ConfigDirectory, account.ConfigDirectory);
        Items.Add(new NoticeChatItem($"Signed in to the {account.Name} account." + (chosen ? "" : " Pick it in the accounts menu to use it."), isError: false));
        if (!chosen || IsRunning)
            return;
        // A CLI that started before the sign-in starts again, with the conversation it had.
        await SettleStartingAsync();
        await StopSessionAsync();
        WarmUp();
    }

    /// <summary>Adds ~/.claude-NAME, switches to it and opens the sign-in.</summary>
    [RelayCommand]
    async Task AddAccount()
    {
        ClaudeAccount account;
        try
        {
            account = ClaudeAccounts.Create(NewAccountName);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            AccountMessage = ex.Message;
            return;
        }
        NewAccountName = "";
        LoadAccounts();
        var added = Accounts.FirstOrDefault(a => ClaudeAccount.SamePath(a.ConfigDirectory, account.ConfigDirectory)) ?? account;
        await SelectAccount(added);
    }
}
