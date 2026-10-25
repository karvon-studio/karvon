namespace Karvon.Servers;

/// <summary>The system's ssh to a machine of the Servers: its config, agent and jump hosts apply.</summary>
public static class SshCommand
{
    /// <summary>ssh's arguments: the alias for a ~/.ssh/config host (its config applies), else user@host with its port and key; the destination last.</summary>
    public static IReadOnlyList<string> Arguments(ServerHost host)
    {
        var args = new List<string>();
        if (host.FromSshConfig)
        {
            if (Environment.GetEnvironmentVariable("KARVON_SSH_HOME") is { Length: > 0 })
                args.AddRange(["-F", SshConfig.ConfigFile]);
            args.Add(host.Name);
            return args;
        }
        if (host.Port != 22)
            args.AddRange(["-p", host.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (host.IdentityFiles.FirstOrDefault() is { } key)
            args.AddRange(["-i", key]);
        args.Add($"{host.User}@{host.Address}");
        return args;
    }

    /// <summary>A command run on the machine with a terminal of its own (ssh -tt: stopped here, it ends there too).</summary>
    public static IReadOnlyList<string> Running(ServerHost host, string command) => ["-tt", .. Arguments(host), command];
}
