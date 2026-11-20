using System.Text.Json;
using Avalonia.Headless.XUnit;
using Karvon.Bek;
using Karvon.Bek.ViewModels;
using static Karvon.Tests.UiTests;

namespace Karvon.Tests;

/// <summary>The Bek OS module: an app of the phone's previewed by bek-preview, its window and Claude's preview tools.</summary>
public class BekTests
{
    sealed class Host : IPreviewHost
    {
        public List<string> Lines { get; } = [];

        public void Said(string line)
        {
            lock (Lines)
                Lines.Add(line);
        }

        public void OpenUrl(string url) { }

        public void Status(string text) { }

        public Task<string?> PickComponentAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public void Each_action_is_the_request_bek_preview_takes()
    {
        Assert.Equal(("/api/tap?x=10.5&y=200", null), new PreviewAction.Tap(10.5, 200).Request());
        Assert.Equal(("/api/scroll?x=1&y=2&dx=0&dy=-48", null), new PreviewAction.Scroll(1, 2, 0, -48).Request());
        Assert.Equal(("/api/type", "héllo"), new PreviewAction.TypeText("héllo").Request());
        Assert.Equal(("/api/key?name=backspace", null), new PreviewAction.Press(PreviewKey.Backspace).Request());
        var change = new PreviewAction.Change(Dark: true, Locale: "uz-UZ", Size: "360x740").Request();
        Assert.Equal("/api/settings?dark=true&locale=uz-UZ&size=360x740", change.Path);
    }

    [Fact]
    public void The_state_is_read_from_its_snake_case_names()
    {
        const string json = """
            {"version":3,"screen":2,"app":{"name":"counter","title":"Counter","component":"/a/counter.wasm"},"running":false,
             "error":"the app stopped: trap","settings":{"width":536,"height":984,"dark":true,"locale":"uz-UZ","fixed_time":true},
             "timing":{"start":57.3,"update":0.2,"draw":4.9,"updates":2},"last_log":7}
            """;
        var state = JsonSerializer.Deserialize<PreviewState>(json, PreviewClient.Json)!;
        Assert.Equal(7, state.LastLog);
        Assert.True(state.Settings.FixedTime);
        Assert.Equal("536x984", state.Settings.Size);
        Assert.Contains("stopped: the app stopped: trap", BekClaudeTools.Line(state));
    }

    [Fact]
    public void A_component_or_an_apps_folder_with_its_build_is_previewed()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-bek-").FullName;
        try
        {
            Assert.Null(BekModule.ComponentOf(dir));
            File.WriteAllText(Path.Combine(dir, "manifest"), "name: notes\n");
            Assert.Null(BekModule.ComponentOf(dir));
            var release = Directory.CreateDirectory(Path.Combine(dir, "target", "wasm32-wasip2", "release")).FullName;
            var component = Path.Combine(release, "notes.wasm");
            File.WriteAllBytes(component, [0, 0x61, 0x73, 0x6d]);
            Assert.Equal((component, Path.Combine(dir, "manifest")), BekModule.ComponentOf(dir));
            Assert.Equal((component, null), BekModule.ComponentOf(component));
            Assert.Null(BekModule.ComponentOf(Path.Combine(dir, "manifest")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>bek-preview itself, with an app's component KARVON_BEK_COMPONENT names (bek-sdk's examples/counter: a count and "Add one").</summary>
    [AvaloniaFact]
    public async Task An_app_is_shown_tapped_and_shown_again()
    {
        var component = Environment.GetEnvironmentVariable("KARVON_BEK_COMPONENT");
        Assert.SkipUnless(component is { Length: > 0 } && File.Exists(component) && PreviewProcess.Find() is not null,
            "Set KARVON_BEK_COMPONENT to bek-sdk's counter example (cargo xtask examples), with bek-preview installed, to run it.");
        var host = new Host();
        var preview = new PreviewViewModel(host);
        try
        {
            await preview.OpenAsync(component!, null);
            await WaitUntil(() => preview.Screen is not null && preview.State is { Running: true }, TimeSpan.FromSeconds(30));
            var before = preview.State!.Screen;
            var page = await preview.Client!.OutlineAsync(CancellationToken.None);
            Assert.Contains("button.add {click}", page);
            var state = await preview.ActAsync(new PreviewAction.Tap(268, 244));
            Assert.NotNull(state);
            Assert.NotEqual(before, state.Screen);
            Assert.Contains("\"1\"", await preview.Client.OutlineAsync(CancellationToken.None));
            preview.Dark = true;
            await WaitUntil(() => preview.State is { Settings.Dark: true }, TimeSpan.FromSeconds(10));
            var window = new Avalonia.Controls.Window { Width = 460, Height = 900, Content = new Bek.Views.PreviewView { DataContext = preview } };
            window.Show();
            Assert.True(File.Exists(Snapshot(window, "bek-preview")));
            lock (host.Lines)
                Assert.Contains(host.Lines, line => line.StartsWith("bek-preview: http://127.0.0.1:", StringComparison.Ordinal));
        }
        finally
        {
            await preview.DisposeAsync();
        }
        Assert.False(preview.HasApp);
    }
}
