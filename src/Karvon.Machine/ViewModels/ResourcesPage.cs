using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Karvon.Machine.ViewModels;

/// <summary>A bar: what it is, how full (0 to 100), and the words beside it.</summary>
public sealed partial class UsageBar(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHigh))]
    public partial double Share { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial string Detail { get; set; } = "";

    /// <summary>Nearly full: shown in warning colors.</summary>
    public bool IsHigh => Share >= 90;
}

/// <summary>
/// The Resources page: the CPU (in all, and each core), memory and swap, each disk's space and each network interface's
/// traffic, and how long the computer has been up; the last minutes' CPU and memory as a line.
/// </summary>
public sealed partial class ResourcesPage : ObservableObject
{
    const int HistoryLength = 120;

    public ObservableCollection<UsageBar> Cores { get; } = [];

    public ObservableCollection<UsageBar> Disks { get; } = [];

    public ObservableCollection<UsageBar> Network { get; } = [];

    /// <summary>The last looks' CPU and memory shares (0 to 100), the oldest first.</summary>
    public ObservableCollection<double> CpuHistory { get; } = [];

    public ObservableCollection<double> MemoryHistory { get; } = [];

    public UsageBar Memory { get; } = new("Memory");

    public UsageBar Swap { get; } = new("Swap");

    [ObservableProperty]
    public partial string CpuText { get; set; } = "";

    [ObservableProperty]
    public partial string CpuDetail { get; set; } = "";

    [ObservableProperty]
    public partial string UptimeText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasSwap { get; set; }

    /// <summary>Each core's share is known (Linux's /proc/stat).</summary>
    [ObservableProperty]
    public partial bool HasCores { get; set; }

    internal void Update(ResourceUsage usage)
    {
        CpuText = $"{usage.Cpu.Total:0.0}%";
        CpuDetail = $"of {Environment.ProcessorCount} CPUs";
        UptimeText = Took(usage.Uptime);
        while (Cores.Count > usage.Cpu.Cores.Count)
            Cores.RemoveAt(Cores.Count - 1);
        for (var i = 0; i < usage.Cpu.Cores.Count; i++)
        {
            if (i == Cores.Count)
                Cores.Add(new UsageBar($"CPU {i}"));
            Cores[i].Share = usage.Cpu.Cores[i];
            Cores[i].Text = $"{usage.Cpu.Cores[i]:0}%";
        }
        HasCores = Cores.Count > 0;
        var memory = usage.Memory;
        Memory.Share = memory.Total > 0 ? 100.0 * memory.Used / memory.Total : 0;
        Memory.Text = $"{ResourceSampler.Bytes(memory.Used)} of {ResourceSampler.Bytes(memory.Total)}";
        Memory.Detail = $"{Memory.Share:0}%";
        HasSwap = memory.SwapTotal > 0;
        Swap.Share = memory.SwapTotal > 0 ? 100.0 * memory.SwapUsed / memory.SwapTotal : 0;
        Swap.Text = $"{ResourceSampler.Bytes(memory.SwapUsed)} of {ResourceSampler.Bytes(memory.SwapTotal)}";
        Swap.Detail = $"{Swap.Share:0}%";
        Fill(Disks, usage.Disks, d => d.Mount, (bar, d) =>
        {
            bar.Share = d.Share;
            bar.Text = $"{ResourceSampler.Bytes(d.Used)} of {ResourceSampler.Bytes(d.Total)} used, {ResourceSampler.Bytes(d.Free)} free";
            bar.Detail = $"{d.Format} · {d.Share:0}%";
        });
        var busiest = Math.Max(1, usage.Network.Select(n => n.ReceivedPerSecond + n.SentPerSecond).DefaultIfEmpty(0).Max());
        Fill(Network, usage.Network, n => n.Name, (bar, n) =>
        {
            bar.Share = 100.0 * (n.ReceivedPerSecond + n.SentPerSecond) / busiest;
            bar.Text = $"↓ {ResourceSampler.Bytes(n.ReceivedPerSecond)}/s   ↑ {ResourceSampler.Bytes(n.SentPerSecond)}/s";
            bar.Detail = $"{ResourceSampler.Bytes(n.Received)} in, {ResourceSampler.Bytes(n.Sent)} out";
        });
        Add(CpuHistory, usage.Cpu.Total);
        Add(MemoryHistory, Memory.Share);
    }

    static void Add(ObservableCollection<double> history, double value)
    {
        history.Add(value);
        while (history.Count > HistoryLength)
            history.RemoveAt(0);
    }

    /// <summary>The bars, one for each of <paramref name="items"/> by its name, kept from one look to the next.</summary>
    static void Fill<T>(ObservableCollection<UsageBar> bars, IReadOnlyList<T> items, Func<T, string> name, Action<UsageBar, T> set)
    {
        var names = items.Select(name).ToList();
        for (var i = bars.Count - 1; i >= 0; i--)
            if (!names.Contains(bars[i].Name))
                bars.RemoveAt(i);
        for (var i = 0; i < items.Count; i++)
        {
            var bar = bars.FirstOrDefault(b => b.Name == names[i]);
            if (bar is null)
                bars.Insert(Math.Min(i, bars.Count), bar = new UsageBar(names[i]));
            else if (bars.IndexOf(bar) != i && i < bars.Count)
                bars.Move(bars.IndexOf(bar), i);
            set(bar, items[i]);
        }
    }

    /// <summary>"3d 4h", "2h 5m", "4m".</summary>
    internal static string Took(TimeSpan time) => time.TotalDays >= 1 ? $"{(int)time.TotalDays}d {time.Hours}h"
        : time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : $"{Math.Max(0, (int)time.TotalMinutes)}m";
}
