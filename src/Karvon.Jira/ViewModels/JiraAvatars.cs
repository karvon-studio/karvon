using Avalonia.Media.Imaging;

namespace Karvon.Jira.ViewModels;

/// <summary>
/// The avatars a board's cards show, each read once: from the site signed in, or from elsewhere (Gravatar) without its
/// secret (<see cref="JiraClient.AvatarAsync"/>). One that cannot be read or drawn (an SVG) is null: its card shows the
/// user's initials instead.
/// </summary>
public sealed class JiraAvatars(Func<Task<JiraClient>> client)
{
    readonly Dictionary<string, Task<Bitmap?>> _read = new(StringComparer.Ordinal);

    public Task<Bitmap?> GetAsync(string url)
    {
        lock (_read)
        {
            if (!_read.TryGetValue(url, out var task))
                _read[url] = task = ReadAsync(url);
            return task;
        }
    }

    async Task<Bitmap?> ReadAsync(string url)
    {
        try
        {
            if (await (await client()).AvatarAsync(url, default) is not { } bytes)
                return null;
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not signed in, or not a picture Avalonia draws.
            return null;
        }
    }
}
