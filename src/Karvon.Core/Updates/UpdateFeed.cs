using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Karvon.Build;
using Karvon.Workspace;

namespace Karvon.Updates;

/// <summary>A file of a release: its name, where it downloads from, its size.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary>A release of Karvon Studio: its version, its notes (Markdown), its files.</summary>
public sealed record ReleaseInfo(string Version, string Tag, string Name, string Notes, string? PageUrl, DateTimeOffset? Published,
    bool Prerelease, IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? Asset(string name) => Assets.FirstOrDefault(a => a.Name == name);
}

/// <summary>
/// Where Karvon Studio's releases are: GitHub's (karvon-studio/karvon), read anonymously; a private repository's through
/// the user's own GitHub CLI (gh, its sign-in: Karvon never sees a token), when it is installed. KARVON_UPDATE_FEED
/// names another: a releases JSON file in GitHub's format, whose files are file: URLs (the tests').
/// </summary>
public sealed class UpdateFeed(string? feed = null)
{
    public const string Repository = "karvon-studio/karvon";

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Karvon-Studio (https://github.com/karvon-studio/karvon)");
        return http;
    }

    /// <summary>The feed Karvon Studio reads: KARVON_UPDATE_FEED's, else GitHub's.</summary>
    public static UpdateFeed Default => new(Environment.GetEnvironmentVariable("KARVON_UPDATE_FEED") is { Length: > 0 } f ? f : null);

    /// <summary>Whether the releases came through gh (their files download through it too).</summary>
    bool _viaGh;

    /// <summary>The releases, newest first as GitHub lists them (drafts left out).</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> ReleasesAsync(CancellationToken ct)
    {
        if (feed is not null)
            return Parse(await File.ReadAllTextAsync(feed, ct));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=30");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && Gh() is { } gh)
        {
            // A private repository (or the API's rate limit): the user's own gh, signed in, may read it.
            var (code, output) = await RunAsync(gh, ["api", $"repos/{Repository}/releases?per_page=30"], ct);
            if (code != 0)
                throw new InvalidOperationException($"gh could not read {Repository}'s releases: {output.Trim()}");
            _viaGh = true;
            return Parse(output);
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"{Repository}'s releases are not public (with the GitHub CLI, gh, signed in, Karvon reads them through it).");
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GitHub's releases JSON, read: each release's version is its tag's, without its v.</summary>
    public static IReadOnlyList<ReleaseInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static bool True(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        List<ReleaseInfo> releases = [];
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (True(release, "draft") || Str(release, "tag_name") is not { Length: > 0 } tag)
                continue;
            var assets = release.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(asset => new ReleaseAsset(Str(asset, "name") ?? "", Str(asset, "browser_download_url") ?? "",
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0)).Where(x => x.Name.Length > 0).ToList()
                : [];
            releases.Add(new ReleaseInfo(tag.TrimStart('v'), tag, Str(release, "name") is { Length: > 0 } name ? name : tag, Str(release, "body") ?? "",
                Str(release, "html_url"), DateTimeOffset.TryParse(Str(release, "published_at"), out var published) ? published : null,
                True(release, "prerelease"), assets));
        }
        return releases;
    }

    /// <summary>A release's file into a path, its progress as it comes (bytes so far, of how many).</summary>
    public async Task DownloadAsync(ReleaseInfo release, ReleaseAsset asset, string path, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        if (Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            File.Copy(uri.LocalPath, partial, overwrite: true);
            progress?.Report((new FileInfo(partial).Length, new FileInfo(partial).Length));
        }
        else if (_viaGh && Gh() is { } gh)
        {
            var folder = Directory.CreateTempSubdirectory("karvon-update-").FullName;
            try
            {
                progress?.Report((0, asset.Size));
                var (code, output) = await RunAsync(gh, ["release", "download", release.Tag, "--repo", Repository, "--pattern", asset.Name, "--dir", folder, "--clobber"], ct);
                if (code != 0)
                    throw new InvalidOperationException($"gh could not download {asset.Name}: {output.Trim()}");
                File.Move(Path.Combine(folder, asset.Name), partial, overwrite: true);
                progress?.Report((asset.Size, asset.Size));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        else
        {
            using var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var from = await response.Content.ReadAsStreamAsync(ct);
            await using (var to = File.Create(partial))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var reported = 0L;
                int read;
                while ((read = await from.ReadAsync(buffer, ct)) > 0)
                {
                    await to.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (done - reported >= 1 << 20 || done == total)
                    {
                        progress?.Report((done, total));
                        reported = done;
                    }
                }
            }
        }
        File.Move(partial, path, overwrite: true);
    }

    static string? Gh() => TaskVariables.Find("gh", Environment.GetEnvironmentVariable("PATH") ?? "", "/");

    static async Task<(int Code, string Output)> RunAsync(string program, IReadOnlyList<string> args, CancellationToken ct)
    {
        var lines = new List<string>();
        using var runner = ProcessRunner.Start(program, args, Path.GetTempPath(), line =>
        {
            lock (lines)
                lines.Add(line);
        });
        var code = await runner.WaitAsync(ct);
        lock (lines)
            return (code, string.Join('\n', lines));
    }
}
