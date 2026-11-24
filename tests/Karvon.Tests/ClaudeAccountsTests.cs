using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Karvon.Claude;
using Karvon.Modules;
using Karvon.ViewModels;
using Karvon.Views;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>Several Claude Code accounts (~/.claude, ~/.claude-NAME): chosen, added and signed in to in the panel.</summary>
public class ClaudeAccountsTests
{
    static string Home => Services.UserHome.Path;

    /// <summary>A name no other test's account has; its folder is the tests' home's.</summary>
    static string NewName(string end = "") => "t" + Guid.NewGuid().ToString("N")[..8] + end;

    static string Folder(string name) => Path.Combine(Home, ".claude-" + name);

    static void Delete(params string[] names)
    {
        foreach (var name in names.Where(n => Directory.Exists(Folder(n))))
            Directory.Delete(Folder(name), recursive: true);
    }

    /// <summary>The sign-in as the CLI leaves it.</summary>
    static void SignedIn(string name) => File.WriteAllText(Path.Combine(Folder(name), ".claude.json"), "{\"oauthAccount\":{\"emailAddress\":\"" + name + "@example.com\"}}");

    static string? LastConfig(string log) => FakeClaude.Log(log).LastOrDefault(e => e.Kind == "config").Data is { ValueKind: System.Text.Json.JsonValueKind.String } config ? config.GetString() : null;

    static List<string> Notices(ClaudeViewModel vm) => vm.Items.OfType<NoticeChatItem>().Select(n => n.Text).ToList();

    [AvaloniaFact]
    public async Task An_account_is_added_signed_in_to_and_switched_from_the_header()
    {
        // The tests' own home: never the user's ~/.claude.
        Assert.NotEqual(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Home);
        var (dir, log) = FakeClaude.Workspace();
        var (name, other) = (NewName(), NewName());
        Directory.CreateDirectory(Folder(other));
        SignedIn(other);
        var settings = new Karvon.Services.Settings();
        var vm = new ClaudeViewModel(settings, () => dir, () => null, () => [], () => FakeClaude.Executable);
        List<ClaudeAccount> signedIn = [];
        vm.SignInWith = account =>
        {
            signedIn.Add(account);
            SignedIn(account.Name);
            return Task.FromResult(0);
        };
        var window = new Window { Width = 560, Height = 900, Content = new ClaudeView { DataContext = vm } };
        window.Show();
        try
        {
            vm.WarmUp();
            await WaitUntil(() => vm.Commands.Count > 0, TimeSpan.FromSeconds(10));
            Assert.Equal(FakeClaude.ConfigDirectory, LastConfig(log));
            vm.Prompt = "Hello";
            await vm.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => !vm.IsRunning, TimeSpan.FromSeconds(10));
            Assert.Contains(vm.Items, i => i is UserChatItem);

            // The header's button opens the menu: the default account, the others with their e-mail, the one in use ticked.
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "AccountsButton");
            Assert.True(button.IsEffectivelyVisible);
            vm.ToggleAccountsCommand.Execute(null);
            await WaitUntil(() => window.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "AccountsMenu" && b.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
            Assert.Contains(vm.Accounts, a => a.IsDefault);
            Assert.Equal((true, other + "@example.com"), vm.Accounts.Where(a => a.Name == other).Select(a => (a.IsSignedIn, a.Detail)).Single());
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(300));
            Snapshot(window, "claude-accounts");
            var list = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "AccountsList");
            Assert.Single(list.GetVisualDescendants().OfType<VsIcon>(), i => i.Kind == "Check" && i.IsVisible);

            // A name that makes no folder is refused in the menu; a good one (Enter) is added, switched to and signed in to.
            vm.NewAccountName = " - ";
            await vm.AddAccountCommand.ExecuteAsync(null);
            Assert.NotNull(vm.AccountMessage);
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "NewAccountBox");
            box.Text = name;
            box.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await WaitUntil(() => vm.SelectedAccount?.Name == name && vm.SelectedAccount.IsSignedIn, TimeSpan.FromSeconds(10));
            Assert.Equal((name, true, false), (vm.AccountName, vm.SelectedAccount!.IsSignedIn, vm.IsAccountsOpen));
            Assert.Equal([name], signedIn.Select(a => a.Name));
            Assert.Contains($"Signed in to the {name} account.", Notices(vm));
            Assert.Equal(Folder(name), settings.ClaudeAccount);
            Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "AccountText").IsEffectivelyVisible);
            // Its CLI starts with its folder, and the conversation is a fresh one.
            await WaitUntil(() => LastConfig(log) == Folder(name), TimeSpan.FromSeconds(10));
            Assert.Equal(Folder(name), LastConfig(log));
            Assert.DoesNotContain(vm.Items, i => i is UserChatItem);

            // Another account signed in to already: chosen at once, no sign-in asked.
            await vm.SelectAccountCommand.ExecuteAsync(vm.Accounts.Single(a => a.Name == other));
            await WaitUntil(() => LastConfig(log) == Folder(other), TimeSpan.FromSeconds(10));
            Assert.Equal((Folder(other), 1), (LastConfig(log), signedIn.Count));
            Assert.Empty(vm.Items);
            // Said in Uzbek too.
            try
            {
                ClaudeLanguage.Instance.Language = "uz";
                Assert.Equal($"{name} akkauntiga kirildi.", ClaudeLanguage.Instance.Translate($"Signed in to the {name} account."));
                Assert.Equal($"{name} akkauntiga kirish yakunlanmadi.", ClaudeLanguage.Instance.Translate($"The sign-in to the {name} account was not finished."));
            }
            finally
            {
                ClaudeLanguage.Instance.Language = "en";
            }
        }
        finally
        {
            await vm.DisposeAsync();
            window.Close();
            Delete(name, other);
        }
    }

    [AvaloniaFact]
    public async Task A_sign_in_that_does_not_finish_or_start_is_said_and_the_default_account_is_not_made_to()
    {
        var (dir, _) = FakeClaude.Workspace();
        var name = NewName();
        Directory.CreateDirectory(Folder(name));
        var vm = new ClaudeViewModel(new Karvon.Services.Settings(), () => dir, () => null, () => [], () => FakeClaude.Executable);
        var asked = 0;
        vm.SignInWith = _ => { asked++; return Task.FromResult(1); };
        try
        {
            vm.ToggleAccountsCommand.Execute(null);
            await vm.SelectAccountCommand.ExecuteAsync(vm.Accounts.Single(a => a.Name == name));
            Assert.Equal((1, name), (asked, vm.SelectedAccount!.Name));
            Assert.Contains($"The sign-in to the {name} account was not finished.", Notices(vm));

            vm.SignInWith = _ => throw new InvalidOperationException("App runs in the Terminal window's console: end it first.");
            await vm.SignInCommand.ExecuteAsync(vm.SelectedAccount);
            Assert.Contains("Could not open a terminal to sign in: App runs in the Terminal window's console: end it first.", Notices(vm));

            // The default account may use an API key: chosen, it is not signed in to (Sign in does).
            vm.SignInWith = _ => { asked++; return Task.FromResult(0); };
            await vm.SelectAccountCommand.ExecuteAsync(vm.Accounts.Single(a => a.IsDefault));
            Assert.Equal((1, true), (asked, vm.SelectedAccount!.IsDefault));
        }
        finally
        {
            await vm.DisposeAsync();
            Delete(name);
        }
    }

    [AvaloniaFact]
    public async Task The_CLIs_own_sign_in_runs_in_the_Terminal_windows_console()
    {
        FakeClaude.Ensure();
        var (name, givesUp) = (NewName(), NewName("-giveup"));
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        var claude = ClaudeOf(vm).Claude;
        try
        {
            claude.NewAccountName = name;
            var adding = claude.AddAccountCommand.ExecuteAsync(null);
            await WaitUntil(() => adding.IsCompleted, TimeSpan.FromSeconds(30));
            await adding;
            // `claude auth login` with the account's folder, away from the workspace's.
            Assert.Equal((name, $".claude-{name}@example.com"), (claude.SelectedAccount!.Name, claude.SelectedAccount.Email));
            Assert.Equal($"Claude Code: {name}", vm.ProgramConsole!.Name);
            using (var written = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder(name), ".claude.json"))))
                Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), written.RootElement.GetProperty("cwd").GetString());

            claude.NewAccountName = givesUp;
            var failing = claude.AddAccountCommand.ExecuteAsync(null);
            await WaitUntil(() => failing.IsCompleted, TimeSpan.FromSeconds(30));
            await failing;
            Assert.Contains($"The sign-in to the {givesUp} account was not finished.", Notices(claude));

            // A program that runs in the console is not ended for a sign-in.
            if (OperatingSystem.IsWindows())
                return;
            var running = await ((IWorkbench)vm).StartHereInConsoleAsync("sleeper", "/bin/sleep", ["60"], Home, new Dictionary<string, string?>());
            await claude.SignInCommand.ExecuteAsync(claude.SelectedAccount);
            Assert.Contains("Could not open a terminal to sign in: sleeper runs in the Terminal window's console: end it first.", Notices(claude));
            Assert.False(running.Exited.IsCompleted);
        }
        finally
        {
            await vm.ShutdownTerminalAsync();
            await claude.DisposeAsync();
            window.Close();
            Delete(name, givesUp);
        }
    }

    [Fact]
    public void The_sign_ins_of_the_other_accounts_are_kept_from_the_shell()
    {
        var home = Directory.CreateTempSubdirectory("karvon-accounts-").FullName;
        Directory.CreateDirectory(Path.Combine(home, ".claude-work"));
        var places = SecretFiles.UserPlaces(home).ToList();
        Assert.Contains(Path.Combine(home, ".claude", ".credentials.json"), places);
        Assert.Contains(Path.Combine(home, ".claude-work", ".credentials.json"), places);
        // The strict sandbox reads the chosen account's snapshot of the shell.
        Assert.Equal([Path.Combine(home, ".claude-work", "shell-snapshots")], ClaudeSandbox.ShellSnapshots(Path.Combine(home, ".claude-work")));
        Directory.Delete(home, recursive: true);
    }
}
