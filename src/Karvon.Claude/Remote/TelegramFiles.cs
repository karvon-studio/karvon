using System.Text;
using Karvon.ViewModels;

namespace Karvon.Claude.Remote;

/// <summary>An image sent with a prompt from Telegram, as Claude takes it (PNG, JPEG, GIF or WebP, 5 MB at most).</summary>
public sealed record RemoteImage(string Name, string MediaType, byte[] Data);

/// <summary>
/// Photos and files sent in Telegram, with the prompt their caption is: a photo (or an image sent as a file) goes to the
/// agent as an image; a text file (a log, code, JSON) in the prompt, whole; anything else is saved on this machine and the
/// prompt says where. Several sent at once (an album) are one prompt.
/// </summary>
public sealed partial class TelegramRemote
{
    /// <summary>A text file this long at most is put in the prompt; a longer one is saved, and the prompt says where.</summary>
    public const int MaxInline = 100 * 1024;

    readonly Dictionary<string, List<TelegramMessage>> _albums = [];

    /// <summary>How long the rest of an album is waited for after its first photo or file.</summary>
    public TimeSpan AlbumWait { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Where the files that are neither images nor text are saved: a folder of their own each time.</summary>
    public string FilesFolder { get; set; } = Path.Combine(Path.GetTempPath(), "karvon-telegram");

    /// <summary>A photo or file of an album, kept until the rest has come; true when it is kept (to be done with the rest).</summary>
    bool Gathered(TelegramBot bot, TelegramMessage message)
    {
        if (message.MediaGroupId is not { } group)
            return false;
        bool first;
        lock (_albums)
        {
            first = !_albums.TryGetValue(group, out var album);
            if (first)
                _albums[group] = album = [];
            album!.Add(message);
        }
        if (first)
            _ = Task.Run(async () =>
            {
                await Task.Delay(AlbumWait);
                List<TelegramMessage> album;
                lock (_albums)
                {
                    album = _albums[group];
                    _albums.Remove(group);
                }
                await OnUIAsync(() => Guarded(() => PromptedAsync(bot, album)));
            });
        return true;
    }

    async Task Guarded(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is TelegramException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
        {
            _problem = "Telegram: " + ex.Message;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// The text of the messages and their files, fetched: the images apart, the text files in the text, the others saved
    /// (the text says where); null with why, when one could not be fetched.
    /// </summary>
    async Task<(string Text, IReadOnlyList<RemoteImage> Images, string? Problem)> GatherAsync(TelegramBot bot, IReadOnlyList<TelegramMessage> messages)
    {
        var text = new StringBuilder(string.Join("\n", messages.Select(m => (m.Text ?? m.Caption ?? "").Trim()).Where(t => t.Length > 0)));
        List<RemoteImage> images = [];
        string? folder = null;
        foreach (var file in messages.SelectMany(m => new[] { m.Photo, m.Document }).OfType<TelegramFile>())
        {
            byte[] data;
            try
            {
                data = await bot.DownloadAsync(file);
            }
            catch (TelegramException ex)
            {
                return ("", [], ex.Message);
            }
            var name = SafeName(file.Name);
            if (ClaudeAttachment.ImageMediaType(data) is { } mediaType && data.Length <= ClaudeAttachment.MaxBytes)
                images.Add(new RemoteImage(name, mediaType, data));
            else if (data.Length <= MaxInline && TextOf(data) is { } content)
                text.Append($"\n\n<file name=\"{name}\">\n{content.TrimEnd()}\n</file>");
            else
            {
                folder ??= Directory.CreateDirectory(Path.Combine(FilesFolder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture))).FullName;
                var path = Path.Combine(folder, name);
                for (var n = 2; File.Exists(path); n++)
                    path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
                await File.WriteAllBytesAsync(path, data);
                text.Append($"\n\n{name} was sent with this; it is saved at {path}");
            }
        }
        return (text.ToString().Trim(), images, null);
    }

    /// <summary>The file's text when it is UTF-8 text; null for anything else.</summary>
    static string? TextOf(byte[] data)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(data);
            return text.Contains('\0') ? null : text.TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    static string SafeName(string name)
    {
        var safe = string.Concat(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return safe is "" or "." or ".." ? "file" : safe;
    }
}
