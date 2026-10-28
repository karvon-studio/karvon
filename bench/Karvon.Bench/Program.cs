using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using Karvon.Claude;
using Karvon.Services;
using Karvon.ViewModels;
using Karvon.Views;

namespace Karvon.Bench;

/// <summary>
/// The benchmark's runner (bench/README.md): Karvon headless, as the tests run it. It opens a task's workspace, waits
/// until its language servers are ready, starts one Claude Code session of an arm (A: Claude Code's tools only; B:
/// Karvon's tools, its system prompt and its shell rules; C: as B without the shell rules), sends the prompt, approves
/// what the session asks (the run is in a container of its own), and keeps everything the CLI wrote, each line with its
/// time. It ends at the session's result, or at the task's turn or time limit.
///
/// Usage: Karvon.Bench --task task.json --workspace DIR --arm A|B|C --model ID --out DIR [--effort LEVEL]
/// Writes to --out: transcript.jsonl, answer.txt and run.json.
/// </summary>
static class Program
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        var options = Options.Parse(args);
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        var exit = 2;
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                exit = await RunAsync(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Karvon.Bench: {ex}");
                File.WriteAllText(Path.Combine(options.Out, "run.json"),
                    new JsonObject { ["error"] = ex.Message, ["infra"] = true }.ToJsonString(Indented));
            }
            finally
            {
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return exit;
    }

    static async Task<int> RunAsync(Options options)
    {
        var task = JsonNode.Parse(File.ReadAllText(options.Task))!.AsObject();
        string? Text(string name) => task[name]?.GetValue<string>();
        var maxTurns = task["maxTurns"]?.GetValue<int>() ?? 40;
        var maxMinutes = task["maxMinutes"]?.GetValue<int>() ?? 15;
        Directory.CreateDirectory(options.Out);
        var run = new JsonObject
        {
            ["task"] = Text("id"),
            ["arm"] = options.Arm,
            ["model"] = options.Model,
            ["started"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        var clock = Stopwatch.StartNew();

        // Karvon as a user has it, every module on, but for the arm: its tools on or off, the shell rules on or off.
        Karvon.Modules.ModuleCatalog.Create = Karvon.BuiltInModules.Create;
        var settings = Settings.Load();
        settings.DisabledModules = [];
        settings.ClaudeKarvonTools = options.Arm != "A";
        settings.ClaudeKarvonToolsFirst = options.Arm == "B";
        settings.ClaudeModel = options.Model;
        settings.ClaudeEffort = options.Effort;
        var vm = new MainViewModel(settings) { RestoresSolutionState = false };
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        vm.Dialogs = new YesDialogs();

        var workspace = Path.GetFullPath(options.Workspace);
        var open = Text("open") ?? ".";
        await vm.OpenSolutionAsync(open == "." ? workspace : Path.Combine(workspace, open));
        if (Text("startup") is { } startup)
            vm.StartupProject = vm.StartupProjects.FirstOrDefault(p => p.FilePath == Path.Combine(workspace, startup)) ?? vm.StartupProject;
        if (task["database"] is JsonObject database)
            vm.Module<Karvon.Database.DatabaseModule>()!.Servers.Upsert(new ServerProfile
            {
                Name = database["name"]!.GetValue<string>(),
                Provider = DatabaseProvider.Sqlite,
                FilePath = Path.Combine(workspace, database["file"]!.GetValue<string>()),
            });

        // The workspace ready, as a user has it before asking: its language servers done loading it.
        foreach (var server in await vm.LanguageServersForClaudeAsync())
            await Until(() => server.IsReady, TimeSpan.FromMinutes(10));
        if (task["breakpoint"] is JsonObject breakpoint)
        {
            vm.Breakpoints.Toggle(Path.Combine(workspace, breakpoint["file"]!.GetValue<string>()), breakpoint["line"]!.GetValue<int>());
            await vm.StartDebuggingCommand.ExecuteAsync(null);
            await Until(() => vm.Mode == DebugMode.Break, TimeSpan.FromMinutes(5));
            if (vm.Mode != DebugMode.Break)
                throw new InvalidOperationException("The program did not stop at the task's breakpoint:\n" + vm.DebugOutput.Document.Text);
        }
        run["readinessMs"] = clock.ElapsedMilliseconds;

        // The session: Karvon's tools from its Claude Code module, as the panel starts one (none in arm A).
        var cli = ClaudeCli.FindExecutable() ?? throw new InvalidOperationException("Claude Code's CLI was not found.");
        var tools = options.Arm == "A" ? null : vm.Module<Karvon.Claude.ClaudeModule>()!.ToolServer();
        await using var session = await ClaudeSession.StartAsync(new ClaudeStartOptions(cli, workspace, options.Model, "bypassPermissions", Tools: tools));
        if (options.Effort is { Length: > 0 } effort)
            await session.ApplyFlagSettingsAsync(new { effortLevel = effort });
        try
        {
            var applied = await session.GetSettingsAsync();
            if (applied.ValueKind == JsonValueKind.Object && applied.TryGetProperty("applied", out var current) && current.TryGetProperty("effort", out var level))
                run["effort"] = level.ToString();
        }
        catch (InvalidOperationException)
        {
        }

        using var transcript = new StreamWriter(Path.Combine(options.Out, "transcript.jsonl"));
        var prompt = Stopwatch.StartNew();
        var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var turns = new HashSet<string>();
        var gate = new object();
        session.Line += line =>
        {
            lock (gate)
                transcript.WriteLine($"{{\"t\":{prompt.ElapsedMilliseconds},\"line\":{line}}}");
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "assistant" && root.TryGetProperty("message", out var message) && message.TryGetProperty("id", out var id)
                && root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.Null)
                lock (turns)
                    turns.Add(id.GetString() ?? "");
            if (type == "result")
                result.TrySetResult(root.Clone());
        };
        session.Event += ev =>
        {
            // Bypass permissions asks nothing; a question (AskUserQuestion) has no one to answer it.
            if (ev is ClaudePermissionRequest request)
                _ = request.ToolName == "AskUserQuestion"
                    ? session.RespondToPermissionAsync(request.RequestId, ClaudePermissionDecision.Deny("No one is here to answer: decide yourself and go on."))
                    : session.RespondToPermissionAsync(request.RequestId, ClaudePermissionDecision.AllowOnce(request.Input));
            if (ev is ClaudeExited exited)
                result.TrySetException(new InvalidOperationException($"Claude Code exited ({exited.ExitCode}): {exited.Stderr}"));
        };
        prompt.Restart();
        await session.SendUserMessageAsync(Text("prompt")!);

        // The limits: the turns (one model call each) and the minutes; a run stopped at one fails, its tokens counted.
        string? limit = null;
        var deadline = TimeSpan.FromMinutes(maxMinutes);
        while (!result.Task.IsCompleted)
        {
            await Task.WhenAny(result.Task, Task.Delay(500));
            int count;
            lock (turns)
                count = turns.Count;
            if (limit is null && (count > maxTurns || prompt.Elapsed > deadline))
            {
                limit = count > maxTurns ? "turns" : "time";
                await session.InterruptAsync();
                // An interrupted session answers with its result; one that does not is ended.
                if (await Task.WhenAny(result.Task, Task.Delay(TimeSpan.FromSeconds(30))) != result.Task)
                    break;
            }
        }
        run["durationMs"] = prompt.ElapsedMilliseconds;
        run["limit"] = limit;
        lock (gate)
            transcript.Flush();
        if (result.Task.IsCompletedSuccessfully)
        {
            var end = result.Task.Result;
            run["result"] = JsonNode.Parse(end.GetRawText());
            File.WriteAllText(Path.Combine(options.Out, "answer.txt"), end.TryGetProperty("result", out var answer) ? answer.GetString() ?? "" : "");
            // An API error (overloaded, rate or usage limits, the network) is the infrastructure's, not the model's: run again.
            run["infra"] = limit is null && end.TryGetProperty("is_error", out var isError) && isError.GetBoolean()
                && end.TryGetProperty("result", out var text) && Infrastructure(text.GetString() ?? "");
        }
        else
        {
            File.WriteAllText(Path.Combine(options.Out, "answer.txt"), "");
            run["infra"] = limit is null;
            run["error"] = result.Task.Exception?.InnerException?.Message ?? "no result";
        }
        File.WriteAllText(Path.Combine(options.Out, "run.json"), run.ToJsonString(Indented));

        await vm.CloseSolutionCoreAsync();
        window.Close();
        return 0;
    }

    static bool Infrastructure(string text) =>
        new[] { "overloaded", "rate limit", "rate_limit", "429", "529", "usage limit", "API Error", "Connection error", "ECONNRESET", "timed out" }
            .Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));

    static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(200);
    }

    sealed record Options(string Task, string Workspace, string Arm, string Model, string Out, string? Effort)
    {
        public static Options Parse(string[] args)
        {
            string? Arg(string name) => Array.IndexOf(args, name) is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;
            string Required(string name) => Arg(name) ?? throw new ArgumentException($"{name} is required (see Program.cs).");
            var arm = Required("--arm");
            if (arm is not ("A" or "B" or "C"))
                throw new ArgumentException("--arm is A, B or C.");
            return new(Required("--task"), Required("--workspace"), arm, Required("--model"), Required("--out"), Arg("--effort"));
        }
    }

    /// <summary>A user who says yes: to a build, to starting the program, to a change Karvon's tools ask about.</summary>
    sealed class YesDialogs : IDialogService
    {
        public Task<string?> PickSolutionAsync() => Task.FromResult<string?>(null);
        public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(initial);
        public Task<bool?> ConfirmSaveAsync(string fileName) => Task.FromResult<bool?>(true);
        public Task<bool?> ConfirmAsync(string title, string message) => Task.FromResult<bool?>(true);
        public Task<Karvon.ViewModels.NewItemChoice?> ChooseNewItemAsync(string title, string folder, Karvon.Workspace.ItemTemplate initial) =>
            Task.FromResult<Karvon.ViewModels.NewItemChoice?>(null);
        public Task<IReadOnlyList<string>> PickFilesAsync(string title) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
