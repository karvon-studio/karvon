using System.Net;
using System.Text;
using System.Text.Json;

namespace Karvon.Pipelines.Providers;

/// <summary>
/// A GitLab's REST v4 (gitlab.com's or a company's own) with a personal access token Karvon keeps: the pipelines' and the
/// merge requests'. Paths are under api/v4/; <see cref="Project"/> is the repository's.
/// </summary>
public sealed class GitLabApi : IDisposable
{
    readonly HttpClient _http;

    public GitLabApi(RepoRemote remote, string token, HttpMessageHandler? handler = null)
    {
        Remote = remote;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri($"{remote.Scheme}://{remote.Authority}/api/v4/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Karvon-Studio");
    }

    public RepoRemote Remote { get; }

    /// <summary>The project's path under api/v4/, ending in a slash.</summary>
    public string Project => $"projects/{Uri.EscapeDataString(Remote.Path)}/";

    public async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode ? body : throw new PipelineException(Message(response.StatusCode, body));
    }

    public async Task<string> SendAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return response.IsSuccessStatusCode ? body : throw new PipelineException(Message(response.StatusCode, body));
    }

    string Message(HttpStatusCode status, string body)
    {
        var said = "";
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            said = root.TryGetProperty("message", out var m) ? m.ValueKind == JsonValueKind.String ? m.GetString()! : m.GetRawText()
                : root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
        }
        return status switch
        {
            HttpStatusCode.Unauthorized => $"{Remote.Host} did not accept the token (401): give a new one.",
            HttpStatusCode.Forbidden => $"{Remote.Host}: the token may not do this (403); changes need its api scope.",
            HttpStatusCode.NotFound => $"{Remote.Host} has no {Remote.Path} the token can see (404).",
            _ => $"{Remote.Host}: HTTP {(int)status}" + (said.Length > 0 ? $": {said}" : "") + ".",
        };
    }

    public void Dispose() => _http.Dispose();
}
