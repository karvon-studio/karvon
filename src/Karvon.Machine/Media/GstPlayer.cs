using System.Runtime.InteropServices;

namespace Karvon.Machine.Media;

/// <summary>A picture of a video: its size and its pixels, BGRA, a row after another.</summary>
public sealed record VideoFrame(int Width, int Height, byte[] Pixels);

/// <summary>
/// A sound or video file played by GStreamer (the system's own, as Linux desktops have it): playbin decodes it, its sound
/// goes to the speakers, its pictures to an appsink as BGRA frames, handed over as they come. Nothing of it is Karvon's
/// but this: where GStreamer is not (Windows, macOS without it), <see cref="IsAvailable"/> says so and the file is opened
/// with the system's own player instead.
/// </summary>
public sealed class GstPlayer : IDisposable
{
    const string Gst = "libgstreamer-1.0.so.0";
    const string GstApp = "libgstapp-1.0.so.0";
    const string GObject = "libgobject-2.0.so.0";
    const string GLib = "libglib-2.0.so.0";

    const int StateNull = 1, StatePaused = 3, StatePlaying = 4;
    const int FormatTime = 3;
    const int SeekFlush = 1, SeekAccurate = 2;
    const int MessageEos = 1, MessageError = 2;
    const int MapRead = 1;
    static readonly IntPtr DoubleType = 15 << 2;
    static readonly IntPtr StringType = 16 << 2;

    [DllImport(Gst)]
    static extern void gst_init(IntPtr argc, IntPtr argv);

    [DllImport(Gst)]
    static extern IntPtr gst_element_factory_make(string factory, string? name);

    [DllImport(Gst)]
    static extern int gst_element_set_state(IntPtr element, int state);

    [DllImport(Gst)]
    static extern int gst_element_query_position(IntPtr element, int format, out long position);

    [DllImport(Gst)]
    static extern int gst_element_query_duration(IntPtr element, int format, out long duration);

    [DllImport(Gst)]
    static extern int gst_element_seek_simple(IntPtr element, int format, int flags, long position);

    [DllImport(Gst)]
    static extern void gst_object_unref(IntPtr obj);

    [DllImport(Gst)]
    static extern IntPtr gst_element_get_bus(IntPtr element);

    [DllImport(Gst)]
    static extern IntPtr gst_bus_pop_filtered(IntPtr bus, int types);

    [DllImport(Gst)]
    static extern IntPtr gst_bus_timed_pop_filtered(IntPtr bus, ulong timeout, int types);

    [DllImport(Gst)]
    static extern void gst_mini_object_unref(IntPtr obj);

    [DllImport(Gst)]
    static extern IntPtr gst_parse_launch(string description, out IntPtr error);

    [DllImport(Gst)]
    static extern IntPtr gst_parse_bin_from_description(string description, int ghostUnlinkedPads, out IntPtr error);

    [DllImport(Gst)]
    static extern IntPtr gst_bin_get_by_name(IntPtr bin, string name);

    [DllImport(Gst)]
    static extern IntPtr gst_sample_get_buffer(IntPtr sample);

    [DllImport(Gst)]
    static extern IntPtr gst_sample_get_caps(IntPtr sample);

    [DllImport(Gst)]
    static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);

    [DllImport(Gst)]
    static extern int gst_structure_get_int(IntPtr structure, string field, out int value);

    [DllImport(Gst)]
    static extern int gst_buffer_map(IntPtr buffer, out MapInfo info, int flags);

    [DllImport(Gst)]
    static extern void gst_buffer_unmap(IntPtr buffer, ref MapInfo info);

    [DllImport(Gst)]
    static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);

    [DllImport(GstApp)]
    static extern IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeout);

    [DllImport(GObject)]
    static extern void g_object_set_property(IntPtr obj, string name, ref GValue value);

    [DllImport(GObject)]
    static extern IntPtr g_value_init(ref GValue value, IntPtr type);

    [DllImport(GObject)]
    static extern void g_value_set_double(ref GValue value, double v);

    [DllImport(GObject)]
    static extern void g_value_set_string(ref GValue value, string v);

    [DllImport(GObject)]
    static extern void g_value_set_object(ref GValue value, IntPtr v);

    [DllImport(GObject)]
    static extern void g_value_unset(ref GValue value);

    [DllImport(GLib)]
    static extern void g_error_free(IntPtr error);

    [DllImport(GLib)]
    static extern void g_free(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    struct GValue
    {
        public IntPtr Type;
        public long Data1;
        public long Data2;
    }

    /// <summary>GstMapInfo: the memory, its flags, the data and its size, and room GStreamer keeps for itself.</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct MapInfo
    {
        public IntPtr Memory;
        public int Flags;
        public IntPtr Data;
        public UIntPtr Size;
        public UIntPtr MaxSize;
        public IntPtr User0, User1, User2, User3;
        public IntPtr Reserved0, Reserved1, Reserved2, Reserved3;
    }

    static readonly Lazy<bool> Available = new(() =>
    {
        if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad(Gst, out _) || !NativeLibrary.TryLoad(GstApp, out _))
            return false;
        try
        {
            gst_init(IntPtr.Zero, IntPtr.Zero);
            var playbin = gst_element_factory_make("playbin", null);
            if (playbin == IntPtr.Zero)
                return false;
            gst_object_unref(playbin);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    });

    /// <summary>GStreamer is there to play with (with playbin and appsink).</summary>
    public static bool IsAvailable => Available.Value;

    readonly IntPtr _pipeline;
    readonly IntPtr _sink;
    readonly IntPtr _bus;
    readonly CancellationTokenSource _stop = new();
    readonly Thread _thread;
    bool _disposed;

    /// <summary>A picture came (on GStreamer's side: a thread of the player's own).</summary>
    public event Action<VideoFrame>? Frame;

    /// <summary>It played to its end.</summary>
    public event Action? Ended;

    /// <summary>It could not be played: GStreamer's words.</summary>
    public event Action<string>? Failed;

    /// <param name="audioSink">Where the sound goes (the tests' own: fakesink); the system's speakers otherwise.</param>
    public GstPlayer(string path, string? audioSink = null)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("GStreamer is not on this computer.");
        _pipeline = gst_element_factory_make("playbin", null);
        if (_pipeline == IntPtr.Zero)
            throw new InvalidOperationException("GStreamer could not make a player for it.");
        // Its pictures to Karvon: a bin of its own (made now, so its appsink is known from the start) as playbin's video sink.
        var video = Bin("videoconvert ! video/x-raw,format=BGRA ! appsink name=karvonsink max-buffers=2 drop=true");
        _sink = gst_bin_get_by_name(video, "karvonsink");
        SetObject(_pipeline, "video-sink", video);
        if (audioSink is not null)
            SetObject(_pipeline, "audio-sink", Bin(audioSink));
        var uri = new GValue();
        g_value_init(ref uri, StringType);
        g_value_set_string(ref uri, new Uri(Path.GetFullPath(path)).AbsoluteUri);
        g_object_set_property(_pipeline, "uri", ref uri);
        g_value_unset(ref uri);
        _bus = gst_element_get_bus(_pipeline);
        _thread = new Thread(Run) { IsBackground = true, Name = "Karvon media" };
        gst_element_set_state(_pipeline, StatePaused);
        _thread.Start();
    }

    public bool IsPlaying { get; private set; }

    public void Play()
    {
        if (_disposed)
            return;
        gst_element_set_state(_pipeline, StatePlaying);
        IsPlaying = true;
    }

    public void Pause()
    {
        if (_disposed)
            return;
        gst_element_set_state(_pipeline, StatePaused);
        IsPlaying = false;
    }

    /// <summary>Where it is; zero before it knows.</summary>
    public TimeSpan Position => !_disposed && gst_element_query_position(_pipeline, FormatTime, out var ns) != 0 && ns > 0 ? TimeSpan.FromTicks(ns / 100) : TimeSpan.Zero;

    /// <summary>How long it is; zero until it knows.</summary>
    public TimeSpan Duration => !_disposed && gst_element_query_duration(_pipeline, FormatTime, out var ns) != 0 && ns > 0 ? TimeSpan.FromTicks(ns / 100) : TimeSpan.Zero;

    public void Seek(TimeSpan position)
    {
        if (!_disposed)
            // Accurate: where it was asked (a key frame may be far before it).
            gst_element_seek_simple(_pipeline, FormatTime, SeekFlush | SeekAccurate, Math.Max(0, position.Ticks) * 100);
    }

    /// <summary>0 (silent) to 1 (as it is), up to 1.5.</summary>
    public double Volume
    {
        set
        {
            if (_disposed)
                return;
            var v = new GValue();
            g_value_init(ref v, DoubleType);
            g_value_set_double(ref v, Math.Clamp(value, 0, 1.5));
            g_object_set_property(_pipeline, "volume", ref v);
            g_value_unset(ref v);
        }
    }

    /// <summary>The player's own thread: pictures taken as they come, its end and its errors told.</summary>
    void Run()
    {
        try
        {
            Pump();
        }
        finally
        {
            // Only this thread lets GStreamer's objects go: nothing of it uses them after.
            gst_element_set_state(_pipeline, StateNull);
            if (_sink != IntPtr.Zero)
                gst_object_unref(_sink);
            gst_object_unref(_bus);
            gst_object_unref(_pipeline);
        }
    }

    void Pump()
    {
        // Three in turn: one shown, one on its way to be, one filled.
        var pool = new byte[3][];
        var next = 0;
        while (!_stop.IsCancellationRequested)
        {
            if (_sink != IntPtr.Zero)
            {
                var sample = gst_app_sink_try_pull_sample(_sink, 50_000_000);
                if (sample != IntPtr.Zero)
                {
                    try
                    {
                        if (Take(sample, pool, next) is { } frame)
                        {
                            next = (next + 1) % pool.Length;
                            Frame?.Invoke(frame);
                        }
                    }
                    finally
                    {
                        gst_mini_object_unref(sample);
                    }
                }
            }
            else
                Thread.Sleep(50);
            // One call for both: gst_bus_pop_filtered throws away what does not match, so two would lose each other's.
            while (gst_bus_pop_filtered(_bus, MessageEos | MessageError) is var message && message != IntPtr.Zero)
            {
                if (TypeOf(message) == MessageEos)
                {
                    IsPlaying = false;
                    // Off this thread: whoever is told may close the player, which this thread then lets go of.
                    if (Ended is { } ended)
                        ThreadPool.QueueUserWorkItem(_ => ended());
                }
                else
                {
                    gst_message_parse_error(message, out var error, out var debug);
                    var text = ErrorText(error);
                    if (error != IntPtr.Zero)
                        g_error_free(error);
                    if (debug != IntPtr.Zero)
                        g_free(debug);
                    IsPlaying = false;
                    if (Failed is { } failed)
                        ThreadPool.QueueUserWorkItem(_ => failed(text));
                }
                gst_mini_object_unref(message);
            }
        }
    }

    /// <summary>A sample's picture: its size from its caps, its pixels copied into the pool's array (a new one when the size changes).</summary>
    static VideoFrame? Take(IntPtr sample, byte[][] pool, int at)
    {
        var buffer = gst_sample_get_buffer(sample);
        var caps = gst_sample_get_caps(sample);
        if (buffer == IntPtr.Zero || caps == IntPtr.Zero)
            return null;
        var structure = gst_caps_get_structure(caps, 0);
        if (gst_structure_get_int(structure, "width", out var width) == 0 || gst_structure_get_int(structure, "height", out var height) == 0 || width <= 0 || height <= 0)
            return null;
        if (gst_buffer_map(buffer, out var map, MapRead) == 0)
            return null;
        try
        {
            var size = width * height * 4;
            if ((long)map.Size.ToUInt64() < size)
                return null;
            if (pool[at] is not { } pixels || pixels.Length != size)
                pool[at] = pixels = new byte[size];
            Marshal.Copy(map.Data, pixels, 0, size);
            return new VideoFrame(width, height, pixels);
        }
        finally
        {
            gst_buffer_unmap(buffer, ref map);
        }
    }

    /// <summary>Elements put together from GStreamer's own words ("videoconvert ! appsink"), as one bin.</summary>
    static IntPtr Bin(string description)
    {
        var bin = gst_parse_bin_from_description(description, 1, out var error);
        if (error != IntPtr.Zero)
        {
            var message = ErrorText(error);
            g_error_free(error);
            if (bin == IntPtr.Zero)
                throw new InvalidOperationException(message);
        }
        return bin != IntPtr.Zero ? bin : throw new InvalidOperationException("GStreamer could not make " + description);
    }

    /// <summary>An element given to a property of another (playbin's sinks): as the value of its own type.</summary>
    static void SetObject(IntPtr target, string property, IntPtr element)
    {
        var value = new GValue();
        // Its type is its class's first field (G_TYPE_FROM_INSTANCE).
        g_value_init(ref value, Marshal.ReadIntPtr(Marshal.ReadIntPtr(element)));
        g_value_set_object(ref value, element);
        g_object_set_property(target, property, ref value);
        g_value_unset(ref value);
    }

    /// <summary>A GstMessage's type: its first field after its GstMiniObject (64 bytes on a 64-bit machine).</summary>
    static int TypeOf(IntPtr message) => Marshal.ReadInt32(message, IntPtr.Size == 8 ? 64 : 36);

    /// <summary>A GError's message: its third field, after the domain and the code.</summary>
    static string ErrorText(IntPtr error) =>
        error == IntPtr.Zero ? "GStreamer could not play it." : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "GStreamer could not play it.";

    /// <summary>A pipeline run to its end (the tests make their sound and video files with it); false when it failed.</summary>
    internal static bool Render(string description, TimeSpan timeout)
    {
        if (!IsAvailable)
            return false;
        var pipeline = gst_parse_launch(description, out var error);
        if (error != IntPtr.Zero)
            g_error_free(error);
        if (pipeline == IntPtr.Zero)
            return false;
        var bus = gst_element_get_bus(pipeline);
        gst_element_set_state(pipeline, StatePlaying);
        var message = gst_bus_timed_pop_filtered(bus, (ulong)timeout.Ticks * 100, MessageEos | MessageError);
        var ended = message != IntPtr.Zero && TypeOf(message) == MessageEos;
        if (message != IntPtr.Zero)
            gst_mini_object_unref(message);
        gst_element_set_state(pipeline, StateNull);
        gst_object_unref(bus);
        gst_object_unref(pipeline);
        return ended;
    }

    /// <summary>It stops, and its thread lets GStreamer's objects go as it ends (waited for, unless this is that thread).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IsPlaying = false;
        _stop.Cancel();
        if (Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(5));
    }
}
