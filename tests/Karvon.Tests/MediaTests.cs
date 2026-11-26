using Karvon.Machine.Media;

namespace Karvon.Tests;

/// <summary>Sound and video played by the system's GStreamer: its pictures handed over, where it is, its end.</summary>
public class MediaTests
{
    internal static string Video(int frames = 45)
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("karvon-media-").FullName, "clip one.webm");
        Assert.True(GstPlayer.Render($"videotestsrc num-buffers={frames} ! video/x-raw,width=64,height=48,framerate=30/1 ! vp8enc ! webmmux ! filesink location=\"{file}\"",
            TimeSpan.FromSeconds(30)), "the test video was not made");
        return file;
    }

    internal static string Sound()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("karvon-media-").FullName, "tone.wav");
        Assert.True(GstPlayer.Render($"audiotestsrc num-buffers=50 ! audioconvert ! wavenc ! filesink location=\"{file}\"", TimeSpan.FromSeconds(30)),
            "the test sound was not made");
        return file;
    }

    [Fact]
    public async Task A_video_s_pictures_come_as_it_plays_it_seeks_and_it_ends()
    {
        Assert.SkipUnless(GstPlayer.IsAvailable, "GStreamer is not on this computer");
        var file = Video();
        using var player = new GstPlayer(file, audioSink: "fakesink sync=true");
        VideoFrame? frame = null;
        var ended = new TaskCompletionSource();
        player.Frame += f => frame = f;
        player.Ended += () => ended.TrySetResult();
        player.Volume = 0.5;
        player.Play();
        Assert.True(player.IsPlaying);
        await UiTests.WaitUntil(() => frame is not null && player.Position > TimeSpan.Zero, TimeSpan.FromSeconds(10));
        Assert.Equal((64, 48, 64 * 48 * 4), (frame!.Width, frame.Height, frame.Pixels.Length));
        Assert.InRange(player.Duration.TotalSeconds, 1.3, 1.7);
        player.Pause();
        Assert.False(player.IsPlaying);
        player.Seek(TimeSpan.FromSeconds(1));
        await UiTests.WaitUntil(() => player.Position >= TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
        Assert.True(player.Position >= TimeSpan.FromSeconds(0.9));
        player.Play();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task A_sound_plays_to_its_end_and_a_file_that_is_none_says_why()
    {
        Assert.SkipUnless(GstPlayer.IsAvailable, "GStreamer is not on this computer");
        using (var player = new GstPlayer(Sound(), audioSink: "fakesink sync=true"))
        {
            var ended = new TaskCompletionSource();
            player.Ended += () => ended.TrySetResult();
            player.Play();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.InRange(player.Duration.TotalSeconds, 0.9, 1.4);
        }
        var junk = Path.Combine(Directory.CreateTempSubdirectory("karvon-media-").FullName, "not.mp4");
        File.WriteAllBytes(junk, [.. Enumerable.Range(0, 4096).Select(i => (byte)(i * 7919 % 251))]);
        using var broken = new GstPlayer(junk, audioSink: "fakesink");
        var failed = new TaskCompletionSource<string>();
        broken.Failed += text => failed.TrySetResult(text);
        broken.Play();
        Assert.False(string.IsNullOrWhiteSpace(await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));
    }
}
