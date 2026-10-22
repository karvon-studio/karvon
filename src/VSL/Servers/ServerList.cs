using System.Text.Json;
using System.Text.Json.Serialization;

namespace VSL.Servers;

/// <summary>What VSL keeps of a machine: one added in VSL (with its address), or the group and production flag of a ~/.ssh/config host.</summary>
public sealed class ServerSettings
{
    public string? Address { get; set; }

    public int? Port { get; set; }

    public string? User { get; set; }

    public string? IdentityFile { get; set; }

    public string? Group { get; set; }

    public bool Production { get; set; }
}

/// <summary>
/// The Servers explorer's machines: ~/.ssh/config's hosts first, then those added in VSL, kept in &lt;config&gt;/servers.json
/// (owner-only) with the groups and production flags of either. No secret is in it: passwords and passphrases are asked,
/// kept for the session, and in the system keyring when the user saves them.
/// </summary>
public sealed class ServerList(string file)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string File { get; } = file;

    public Dictionary<string, ServerSettings> Load()
    {
        try
        {
            return System.IO.File.Exists(File)
                ? JsonSerializer.Deserialize<Dictionary<string, ServerSettings>>(System.IO.File.ReadAllText(File), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(Dictionary<string, ServerSettings> settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(settings, Json));
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(File, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Every machine: ssh config's (with VSL's flags), then VSL's own that ssh config does not have.</summary>
    public IReadOnlyList<ServerHost> Hosts()
    {
        var settings = Load();
        var hosts = SshConfig.Read().Select(h => settings.TryGetValue(h.Name, out var s) ? h with { Group = s.Group, Production = s.Production } : h).ToList();
        foreach (var (name, s) in settings)
            if (s.Address is { Length: > 0 } address && hosts.All(h => !h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                hosts.Add(new ServerHost(name, address, s.Port ?? 22, s.User ?? Environment.UserName)
                {
                    IdentityFiles = s.IdentityFile is { Length: > 0 } key ? [key] : [],
                    Source = "VSL",
                    Group = s.Group,
                    Production = s.Production,
                });
        return hosts;
    }

    public void Update(string name, Action<ServerSettings> change)
    {
        var settings = Load();
        if (!settings.TryGetValue(name, out var entry))
            settings[name] = entry = new ServerSettings();
        change(entry);
        Save(settings);
    }

    public void Remove(string name)
    {
        var settings = Load();
        if (settings.Remove(name))
            Save(settings);
    }
}
