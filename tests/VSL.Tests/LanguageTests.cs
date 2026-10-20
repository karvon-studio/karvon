using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VSL.Claude;
using VSL.ViewModels;
using VSL.Views;
using static VSL.Tests.UiTests;

namespace VSL.Tests;

/// <summary>The Claude Code panel in English or Uzbek, with the VS extension's translations.</summary>
public class LanguageTests
{
    [AvaloniaFact]
    public async Task Claude_panel_switches_between_English_and_Uzbek()
    {
        var vm = NewViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1300, Height = 850 };
        window.Show();
        vm.ShowClaude();
        await WaitUntil(() => window.GetVisualDescendants().OfType<ClaudeView>().Any(v => v.IsEffectivelyVisible), TimeSpan.FromSeconds(5));
        var view = window.GetVisualDescendants().OfType<ClaudeView>().First();
        var prompt = view.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptBox");
        Assert.StartsWith("Ask Claude to edit", prompt.PlaceholderText);
        var language = view.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "LanguageButton");
        try
        {
            language.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("uz", ClaudeLanguage.Instance.Language);
            Assert.Equal("uz", vm.Settings.UiLanguage);
            Assert.StartsWith("Claude’dan tahrirlash", prompt.PlaceholderText);
            // Texts the shared view model builds are translated by their patterns, as in the extension.
            Assert.Equal("O‘ylayapti... (3s)", ClaudeLanguage.Instance.Translate("Thinking... (3s)"));
            await WaitUntil(() => false, TimeSpan.FromMilliseconds(100));
            Snapshot(window, "claude-uzbek");
            // The next window starts in the language chosen.
            Assert.Equal("uz", Services.Settings.Load().UiLanguage);
        }
        finally
        {
            ClaudeLanguage.Instance.Language = "en";
            vm.Settings.UiLanguage = null;
            vm.Settings.Save();
        }
        Assert.StartsWith("Ask Claude to edit", prompt.PlaceholderText);
        window.Close();
    }
}
