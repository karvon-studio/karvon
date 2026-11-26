using System.Diagnostics;
using System.Text.Json;

namespace Karvon.Machine;

/// <summary>A file system on a disk of this computer, mounted or not: what its card is named, where it is mounted.</summary>
public sealed record Volume(string Device, string? Label, string Format, long Size, IReadOnlyList<string> Mounts, string? PartitionType)
{
    /// <summary>Its label, or its size ("1.0 TB Volume"), as the system's file manager names it.</summary>
    public string Name => string.IsNullOrWhiteSpace(Label) ? ResourceSampler.Bytes(Size) + " Volume" : Label;

    public bool IsMounted => Mounts.Count > 0;
}

/// <summary>
/// The file systems of this computer's disks (Linux's lsblk): the label of each one mounted, and those not mounted that
/// may be (a Windows partition, a second disk), mounted for the user by udisks as the file manager mounts them.
/// </summary>
public static class Volumes
{
    /// <summary>What the file manager mounts: the file systems of data, not a swap, a RAID's or an encrypted one's member.</summary>
    static readonly HashSet<string> Mountable = new(StringComparer.OrdinalIgnoreCase)
    {
        "ext2", "ext3", "ext4", "btrfs", "xfs", "ntfs", "ntfs3", "vfat", "exfat", "hfsplus", "apfs", "f2fs", "jfs", "reiserfs", "udf", "iso9660",
    };

    /// <summary>The partitions the system keeps to itself: its boot, Windows's reserved and recovery ones.</summary>
    static readonly HashSet<string> SystemPartitions = new(StringComparer.OrdinalIgnoreCase)
    {
        "EFI System", "BIOS boot", "Microsoft reserved", "Windows recovery environment", "Linux swap",
    };

    /// <summary>The file systems lsblk lists (none elsewhere, or when it cannot be run).</summary>
    public static IReadOnlyList<Volume> Read()
    {
        if (!OperatingSystem.IsLinux())
            return [];
        // Older lsblk (before util-linux 2.37) has MOUNTPOINT alone, before 2.35 no partition type's name.
        var json = Run("PATH,LABEL,SIZE,FSTYPE,MOUNTPOINTS,TYPE,PARTTYPENAME") ?? Run("PATH,LABEL,SIZE,FSTYPE,MOUNTPOINT,TYPE");
        return json is null ? [] : Parse(json);
    }

    static string? Run(string columns)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("lsblk", ["-J", "-b", "-o", columns])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null)
                return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>lsblk's JSON (<c>-J -b</c>): each file system of its devices and their partitions (a snap's loops left out).</summary>
    public static IReadOnlyList<Volume> Parse(string json)
    {
        List<Volume> volumes = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("blockdevices", out var devices))
                Add(devices, volumes);
        }
        catch (JsonException)
        {
        }
        return volumes;
    }

    static void Add(JsonElement devices, List<Volume> volumes)
    {
        foreach (var device in devices.EnumerateArray())
        {
            if (Text(device, "type") is not "loop" && Text(device, "path") is { } path && Text(device, "fstype") is { } format)
            {
                List<string> mounts = [];
                if (device.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array)
                    mounts.AddRange(points.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!));
                else if (Text(device, "mountpoint") is { } point)
                    mounts.Add(point);
                volumes.Add(new Volume(path, Text(device, "label"), format, Number(device, "size"), mounts, Text(device, "parttypename")));
            }
            if (device.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                Add(children, volumes);
        }
    }

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    /// <summary>A size: a number, or (older lsblk) a string of one.</summary>
    static long Number(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind switch
    {
        JsonValueKind.Number => value.GetInt64(),
        JsonValueKind.String when long.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var n) => n,
        _ => 0,
    } : 0;

    /// <summary>The volumes not mounted that the user may mount: data's file systems, not the system's own partitions.</summary>
    public static IReadOnlyList<Volume> Unmounted(IEnumerable<Volume> volumes) =>
        [.. volumes.Where(v => !v.IsMounted && Mountable.Contains(v.Format) && (v.PartitionType is null || !SystemPartitions.Contains(v.PartitionType)))];

    /// <summary>A mounted disk's name: its file system's label, the computer's name for its root, else its size ("120 GB Volume").</summary>
    public static string NameOf(DiskUsage disk, IEnumerable<Volume> volumes)
    {
        if (disk.Label is { Length: > 0 } label)
            return label;
        if (volumes.FirstOrDefault(v => v.Mounts.Contains(disk.Mount)) is { Label: { Length: > 0 } volumeLabel })
            return volumeLabel;
        if (disk.Mount == "/")
            return Environment.MachineName;
        // macOS mounts a disk at /Volumes/its name.
        if (OperatingSystem.IsMacOS() && disk.Mount.StartsWith("/Volumes/", StringComparison.Ordinal))
            return disk.Mount["/Volumes/".Length..];
        return ResourceSampler.Bytes(disk.Total) + " Volume";
    }

    /// <summary>
    /// A volume mounted for the user (udisks, at /media/user/its name), as the file manager mounts it: the system asks for
    /// the user's password when it wants it. Why not, when it was not.
    /// </summary>
    public static async Task<string?> MountAsync(string device, CancellationToken ct)
    {
        try
        {
            // Not a terminal's: udisksctl asks through the desktop's own password window, not in a terminal.
            using var process = Process.Start(new ProcessStartInfo("udisksctl", ["mount", "-b", device])
            { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null)
                return "udisksctl could not be started.";
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0 ? null : Why(await error, await output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "Mounting needs udisksctl (the udisks2 package), which is not installed.";
        }
    }

    /// <summary>udisksctl's error, without its D-Bus name: "Not authorized to perform operation".</summary>
    internal static string Why(string error, string output)
    {
        var text = (error.Length > 0 ? error : output).Trim();
        var dbus = text.IndexOf("GDBus.Error:", StringComparison.Ordinal);
        if (dbus >= 0 && text.IndexOf(": ", dbus, StringComparison.Ordinal) is var colon and > 0)
            text = text[..dbus] + text[(colon + 2)..];
        return text.Length > 0 ? text : "It could not be mounted.";
    }
}
