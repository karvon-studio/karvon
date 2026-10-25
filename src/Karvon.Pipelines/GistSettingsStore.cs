using System.Text.Json;
using System.Text.Json.Nodes;
using Karvon.Pipelines.Providers;
using Karvon.Services;

namespace Karvon.Pipelines;

/// <summary>
/// Settings Sync's GitHub: a secret gist of the user's (through gh, signed in as it is; gh's login has the gist scope),
/// found by its description on a machine that does not know it yet, its id kept once found or made.
/// </summary>
sealed class GistSettingsStore(Func<CancellationToken, Task<IGitHubApi>> connect, SettingsSyncState state) : ISettingsSyncStore
{
    public const string Description = "Karvon Studio Settings Sync";

    public string Name => "GitHub (a secret gist)";

    /// <summary>GitHub through the signed-in gh; else why not.</summary>
    public static Func<CancellationToken, Task<IGitHubApi>> Gh(Func<string?> gh) => async ct =>
        gh() is { } cli && await GhCliApi.SignedInAsync(cli, "github.com", ct)
            ? new GhCliApi(cli, "github.com")
            : throw new InvalidOperationException("GitHub needs gh, signed in (gh auth login): then turn Settings Sync on again.");

    public async Task<SyncedSettings?> ReadAsync(CancellationToken ct)
    {
        using var github = await connect(ct);
        try
        {
            if ((state.Location ?? await FindAsync(github, ct)) is not { } id)
                return null;
            JsonNode? gist;
            try
            {
                gist = JsonNode.Parse(await github.GetAsync($"/gists/{id}", ct));
            }
            catch (PipelineException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
            {
                // Deleted on GitHub: made again with the next write.
                state.Location = null;
                return null;
            }
            state.Location = id;
            return gist?["files"]?[FolderSettingsStore.FileName]?["content"]?.GetValue<string>() is { } content ? SyncedSettings.Parse(content) : null;
        }
        catch (PipelineException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }

    static async Task<string?> FindAsync(IGitHubApi github, CancellationToken ct) =>
        JsonNode.Parse(await github.GetAsync("/gists?per_page=100", ct)) is JsonArray gists
            ? gists.FirstOrDefault(g => (string?)g?["description"] == Description && g?["files"]?[FolderSettingsStore.FileName] is not null)?["id"]?.GetValue<string>()
            : null;

    public async Task WriteAsync(SyncedSettings settings, CancellationToken ct)
    {
        using var github = await connect(ct);
        var files = new JsonObject { [FolderSettingsStore.FileName] = new JsonObject { ["content"] = settings.ToJson() } };
        try
        {
            if (state.Location is { } id)
                await github.SendAsync("PATCH", $"/gists/{id}", new JsonObject { ["files"] = files }.ToJsonString(), ct);
            else
                state.Location = JsonNode.Parse(await github.SendAsync("POST", "/gists",
                    new JsonObject { ["description"] = Description, ["public"] = false, ["files"] = files }.ToJsonString(), ct))?["id"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is PipelineException or JsonException)
        {
            throw new InvalidOperationException(ex.Message);
        }
    }
}
