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
            Assert.Equal((component, Path.Combine(dir, "manifest")), BekModule.ComponentOf(dir, null));
            Assert.Equal((component, null), BekModule.ComponentOf(component));
            Assert.Null(BekModule.ComponentOf(Path.Combine(dir, "manifest")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void An_app_in_a_cargo_workspace_is_found_in_its_target_folder_by_its_crate_and_only_apps_components_are_offered()
    {
        byte[] wasm = [0, 0x61, 0x73, 0x6d];
        var workspace = Directory.CreateTempSubdirectory("karvon-bek-ws-").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("karvon-bek-target-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(workspace, "Cargo.toml"), "[workspace]\nmembers = [\"apps/*\"]\n");
            var app = Directory.CreateDirectory(Path.Combine(workspace, "apps", "notes-app")).FullName;
            File.WriteAllText(Path.Combine(app, "manifest"), "name: notes\n");
            File.WriteAllText(Path.Combine(app, "Cargo.toml"), "[package]\nname = \"notes-app\"\nversion = \"0.1.0\"\n\n[lib]\ncrate-type = [\"cdylib\"]\n");
            var release = Directory.CreateDirectory(Path.Combine(workspace, "target", "wasm32-wasip2", "release")).FullName;
            var notes = Path.Combine(release, "notes_app.wasm");
            File.WriteAllBytes(notes, wasm);
            File.SetLastWriteTimeUtc(notes, DateTime.UtcNow.AddMinutes(-5));
            // Another app's, built later in the same target folder, is not this one's.
            File.WriteAllBytes(Path.Combine(release, "clock_app.wasm"), wasm);

            var manifest = Path.Combine(app, "manifest");
            Assert.Equal((notes, manifest), BekModule.ComponentOf(app, null));

            // CARGO_TARGET_DIR: its newer build there.
            var debug = Directory.CreateDirectory(Path.Combine(elsewhere, "wasm32-wasip2", "debug")).FullName;
            var moved = Path.Combine(debug, "notes_app.wasm");
            File.WriteAllBytes(moved, wasm);
            Assert.Equal((moved, manifest), BekModule.ComponentOf(app, elsewhere));

            // Preview on the Phone is on an app's component: cargo's build of one, or one with its manifest beside it; not on a web app's WebAssembly.
            Assert.True(BekModule.IsAppComponent(notes));
            var site = Directory.CreateDirectory(Path.Combine(workspace, "web", "wwwroot", "_framework")).FullName;
            var dotnet = Path.Combine(site, "dotnet.wasm");
            File.WriteAllBytes(dotnet, wasm);
            Assert.False(BekModule.IsAppComponent(dotnet));
            File.WriteAllBytes(Path.Combine(site, "clock.wasm"), wasm);
            File.WriteAllText(Path.Combine(site, "clock.manifest"), "name: clock\n");
            Assert.True(BekModule.IsAppComponent(Path.Combine(site, "clock.wasm")));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    /// <summary>The fake bek-preview (TestAssets/FakeBekPreview): bek-preview's API for a counter app, behaving as its component's name says.</summary>
    static string FakeBekPreview()
    {
        var dir = Directory.CreateTempSubdirectory("karvon-fake-bek-").FullName;
        var script = Path.Combine(dir, "bek-preview.py");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "FakeBekPreview", "bek-preview.py"), script);
        return TestTools.Command(script);
    }

    /// <summary>An app's component, in a folder of its own: its name says how the fake behaves.</summary>
    static string Component(string name)
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("karvon-bek-app-").FullName, name + ".wasm");
        File.WriteAllBytes(file, [0, 0x61, 0x73, 0x6d]);
        return file;
    }

    /// <summary>Whether the fake bek-preview started for <paramref name="component"/> still runs.</summary>
    static bool Running(string component)
    {
        var pid = int.Parse(File.ReadAllText(component + ".pid"), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [AvaloniaFact]
    public async Task Claudes_tools_open_the_app_see_it_tap_it_and_read_its_log_in_the_window()
    {
        var program = FakeBekPreview();
        var component = Component("counter");
        var preview = new PreviewViewModel(new Host()) { FindProgram = () => program };
        var tools = BekClaudeTools.Create(() => preview, () => Path.GetDirectoryName(component)).ToDictionary(t => t.Name);
        var ct = CancellationToken.None;
        try
        {
            // Opened by its name in the workspace: its state and its screen.
            var opened = await tools["bek_preview_open"].CallAsync(Args(new { component = "counter.wasm" }), ct);
            Assert.False(opened.IsError, opened.Text);
            Assert.StartsWith($"Counter ({component}): running; 536x984, light, en-US;", opened.Text);
            Assert.Equal("image/png", Assert.Single(opened.Images!).MimeType);
            Assert.True(preview.HasApp);
            // The window draws each screen as it comes (the one before it let go of).
            var window = new Avalonia.Controls.Window { Width = 460, Height = 900, Content = new Bek.Views.PreviewView { DataContext = preview } };
            window.Show();
            await WaitUntil(() => preview.Screen is not null, TimeSpan.FromSeconds(10));
            var first = preview.Screen;

            // Its page's nodes, as text others wrote.
            var screen = await tools["bek_preview_screen"].CallAsync(Args(new { tree = true }), ct);
            Assert.Contains("button.add {click} @218,220 100x48", screen.Text);
            Assert.Equal("the app's page", screen.Outside);

            // A tap, in the window's line of actions: the window shows the state it gave.
            var tapped = await tools["bek_preview_act"].CallAsync(Args(new { action = "tap", x = 268, y = 244 }), ct);
            Assert.False(tapped.IsError, tapped.Text);
            Assert.Contains("log up to line 2", tapped.Text);
            Assert.Equal(2, preview.State!.LastLog);
            var dark = await tools["bek_preview_act"].CallAsync(Args(new { action = "settings", dark = true, size = "360x740" }), ct);
            Assert.Contains("360x740, dark", dark.Text);
            Assert.True(preview.Dark);

            var log = await tools["bek_preview_log"].CallAsync(Args(new { after = 1 }), ct);
            Assert.Equal("2 [0.0s] app: count 1", log.Text);
            Assert.True((await tools["bek_preview_act"].CallAsync(Args(new { action = "fly" }), ct)).IsError);
            Assert.True((await tools["bek_preview_act"].CallAsync(Args(new { action = "tap" }), ct)).IsError);

            await WaitUntil(() => preview.Screen != first, TimeSpan.FromSeconds(10));
            Assert.NotSame(first, preview.Screen);
            using (var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window))
                Assert.NotNull(frame);
            Assert.Equal("Counter", preview.Title);
            window.Close();
        }
        finally
        {
            await preview.DisposeAsync();
        }
        Assert.False(Running(component));
        // With no app, the tools say how to open one.
        var none = await tools["bek_preview_screen"].CallAsync(Args(new { }), ct);
        Assert.True(none.IsError);
        Assert.Contains("bek_preview_open", none.Text);
    }

    [AvaloniaFact]
    public async Task An_app_asked_for_while_another_starts_takes_its_place_and_no_bek_preview_is_left_running()
    {
        var program = FakeBekPreview();
        var preview = new PreviewViewModel(new Host()) { FindProgram = () => program };
        var slow = Component("slow");
        var counter = Component("counter");
        var slower = Component("slow");
        try
        {
            var first = preview.OpenAsync(slow, null);
            await WaitUntil(() => File.Exists(slow + ".pid"), TimeSpan.FromSeconds(10));
            await preview.OpenAsync(counter, null);
            var superseded = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
            Assert.Contains("another app was opened in its place", superseded.Message);
            Assert.False(Running(slow));
            Assert.True(Running(counter));
            await WaitUntil(() => preview.State is not null, TimeSpan.FromSeconds(10));
            Assert.Equal("Counter", preview.Title);

            // Stopped while another starts: both end, the one shown and the one on its way.
            var starting = preview.OpenAsync(slower, null);
            await WaitUntil(() => File.Exists(slower + ".pid"), TimeSpan.FromSeconds(10));
            await preview.StopAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => starting);
            Assert.False(Running(slower));
            Assert.False(Running(counter));
            Assert.False(preview.HasApp);
            Assert.Equal("The preview was stopped.", preview.Message);
        }
        finally
        {
            await preview.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_bek_preview_that_never_says_it_is_ready_is_ended_and_says_why()
    {
        var program = FakeBekPreview();
        var never = Component("never");
        var lines = new List<string>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PreviewProcess.StartAsync(program, never, null, lines.Add, CancellationToken.None, readyWithin: TimeSpan.FromSeconds(2)));
        Assert.Equal("bek-preview did not say it was ready in 2 seconds: see its output (Output > Bek Preview).", error.Message);
        await WaitUntil(() => File.Exists(never + ".pid") && !Running(never), TimeSpan.FromSeconds(5));
        Assert.False(Running(never));
    }

    [AvaloniaFact]
    public async Task What_bek_preview_says_that_cannot_be_read_is_logged_and_the_window_goes_on()
    {
        var program = FakeBekPreview();
        var flaky = Component("flaky");
        var preview = new PreviewViewModel(new Host()) { FindProgram = () => program };
        try
        {
            await preview.OpenAsync(flaky, null);
            await WaitUntil(() => preview.Screen is not null, TimeSpan.FromSeconds(10));
            // The state after the tap is what is not JSON: logged, and the next one shows.
            await preview.TapAsync(268, 244);
            await WaitUntil(() => preview.Log.Contains("app: count 1"), TimeSpan.FromSeconds(10));
            Assert.Contains(preview.Log, line => line.StartsWith("preview: ", StringComparison.Ordinal));
            Assert.Contains("app: count 1", preview.Log);
            Assert.True(preview.HasApp);
        }
        finally
        {
            await preview.DisposeAsync();
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
