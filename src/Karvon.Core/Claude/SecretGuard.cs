using System.Text.Json;
using System.Text.RegularExpressions;

namespace Karvon.Claude;

/// <summary>
/// What keeps Claude Code's own tools away from the secrets on the machine, as Karvon's PreToolUse hook answers: a file
/// that holds secrets is not read, edited or written by Read, Edit, Write, NotebookEdit or Grep, and a shell command (Bash's,
/// or PowerShell's, which is Claude Code's shell on Windows) that reads one, or asks a secret store for what it keeps, does
/// not run. A guard against what slips, not a boundary of its own: the shell can reach them by ways no list has.
/// </summary>
public static partial class SecretGuard
{
    /// <summary>The tools whose calls the hook is asked about.</summary>
    public const string Tools = "Read|Edit|MultiEdit|Write|NotebookEdit|Grep|Bash|PowerShell";

    const string Advice = "Ask the user for what you need from it (never the secret itself), or use Karvon's tools, which sign in for you.";

    /// <summary>Why the call may not run, or null when it may.</summary>
    /// <param name="sealedOnly">The user turned the guard off: only what no agent gets whatever the settings (<see cref="SealedFiles"/>) is kept.</param>
    public static string? Check(string tool, JsonElement input, string? workspace, bool sealedOnly = false)
    {
        if (CheckSealed(tool, input, workspace) is { } kept)
            return kept;
        if (sealedOnly)
            return null;
        if (tool == "Bash")
            return Str(input, "command") is { } command ? CheckCommand(command, workspace) : null;
        if (tool == "PowerShell")
            return Str(input, "command") is { } command ? CheckPowerShell(command, workspace) : null;
        var path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? (tool == "Grep" ? Str(input, "path") : null);
        return path is not null && Full(path, workspace) is { } full && SecretFiles.IsSecret(full)
            ? $"Karvon keeps secrets out of Claude's reach: {path} holds secrets, so Claude may not read or change it. {Advice}"
            : null;
    }

    // ---- What no agent gets -----------------------------------------------------------------------------------

    /// <summary>A file that is sealed, or the store its key could be asked of, whatever the settings say.</summary>
    static string? CheckSealed(string tool, JsonElement input, string? workspace)
    {
        // A hidden value's mark, written to a file or run: nothing takes its place. (What an edit replaces may have one: it is not found.)
        if (Written(tool, input).Any(Karvon.Claude.Tools.SecretMasks.HasSealedMarks))
            return Karvon.Claude.Tools.SecretMasks.SealedReason;
        if (tool is "Bash" or "PowerShell")
            return Str(input, "command") is { } command ? CheckSealed(command, workspace, tool == "PowerShell") : null;
        var path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? (tool is "Grep" or "Glob" ? Str(input, "path") : null);
        return path is not null && Full(path, workspace) is { } full && SealedFiles.Is(full) ? $"Karvon keeps secrets out of Claude's reach: {path} is not read or changed. {SealedFiles.Reason}" : null;
    }

    /// <summary>What a call would write or run.</summary>
    static IEnumerable<string> Written(string tool, JsonElement input)
    {
        foreach (var name in tool switch { "Bash" or "PowerShell" => ["command"], "Write" => ["content"], "Edit" => ["new_string"], "NotebookEdit" => ["new_source"], _ => Array.Empty<string>() })
            if (Str(input, name) is { } text)
                yield return text;
        if (tool == "MultiEdit" && input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            foreach (var edit in edits.EnumerateArray())
                if (Str(edit, "new_string") is { } text)
                    yield return text;
    }

    static string? CheckSealed(string command, string? workspace, bool powerShell)
    {
        foreach (var words in powerShell ? ShellCommand.PowerShellWords(command) : ShellCommand.Words(command))
        {
            var program = ProgramOf(words[0]);
            if (AsksKeyring(program.ToLowerInvariant(), words))
                return $"Karvon keeps secrets out of Claude's reach: this command asks the system keyring for what it keeps, so it does not run. {Advice}";
            if (OpensKarvon(program, words))
                return "Karvon keeps secrets out of Claude's reach: this command opens Karvon's own process (a debugger, a dump, a trace), so it does not run.";
            if (Harmless(program, words))
                continue;
            if (words.Skip(1).Append(program).FirstOrDefault(w => NamesSealed(w, workspace, powerShell)) is { } word)
                return $"Karvon keeps secrets out of Claude's reach: this command reads {word}. {SealedFiles.Reason}";
        }
        foreach (Match m in InputRedirection().Matches(command))
            if (NamesSealed(m.Groups[1].Value, workspace, powerShell))
                return $"Karvon keeps secrets out of Claude's reach: this command reads {m.Groups[1].Value}. {SealedFiles.Reason}";
        // Names listed, then handed to what reads them.
        return HandsOn().IsMatch(command) && SealedText().IsMatch(command) ? $"Karvon keeps secrets out of Claude's reach: this command reads a Postman environment. {SealedFiles.Reason}" : null;
    }

    [GeneratedRegex(@"\b(?:xargs|parallel|ForEach-Object)\b|-exec(?:dir)?\b|\bwhile\s+read\b|\bfor\s+\w+\s+in\b|%\s*\{")]
    private static partial Regex HandsOn();

    [GeneratedRegex(@"postman_(?:environment|globals)", RegexOptions.IgnoreCase)]
    private static partial Regex SealedText();

    /// <summary>What only names files, or moves them into git's index: nothing of what they hold is shown.</summary>
    static bool Harmless(string program, List<string> words) => program switch
    {
        "ls" or "stat" or "test" or "[" or "echo" or "printf" or "rm" or "touch" or "mkdir" or "find" => !words.Any(w => w is "-exec" or "-execdir" or "-ok" or "-fprint"),
        "git" => words.Skip(1).FirstOrDefault(w => !w.StartsWith('-')) is "add" or "rm" or "status" or "ls-files" or "check-ignore" or "update-index",
        _ => false,
    };

    static bool NamesSealed(string word, string? workspace, bool powerShell)
    {
        // The word itself, what follows an option's = or a colon (HEAD:file, -Path:file, source:target), curl's @file.
        foreach (var part in word.Split('=', ':').Prepend(word).Select(w => w.Trim('"', '\'', '@', '<')).Where(w => w.Length > 1))
        {
            if (SealedFiles.IsName(part))
                return true;
            var path = powerShell ? ShellCommand.PowerShellPathOf(part, workspace) : ShellCommand.PathOf(part, workspace);
            if (path is null)
                continue;
            if (part.IndexOfAny(['*', '?', '[']) < 0)
            {
                if (SealedFiles.Is(path))
                    return true;
                continue;
            }
            // A glob: whether it takes one in.
            try
            {
                var (folder, pattern) = (Path.GetDirectoryName(path), Path.GetFileName(path));
                if (folder is not null && folder.IndexOfAny(['*', '?']) < 0 && Directory.Exists(folder)
                    && Directory.EnumerateFiles(folder).Any(f => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, Path.GetFileName(f)) && SealedFiles.Is(f)))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }
        return false;
    }

    /// <summary>Asked over the bus, by the desktop's own programs, or by a script's library.</summary>
    static bool AsksKeyring(string program, List<string> words) => program switch
    {
        "secret-tool" => words.Any(w => w is "lookup" or "search"),
        "kwallet-query" or "kwalletcli" or "gnome-keyring" or "gnome-keyring-daemon" or "seahorse" => true,
        "keyring" => words.Contains("get"),
        "gdbus" or "busctl" or "dbus-send" or "dbus-monitor" or "qdbus" or "qdbus6" or "qdbus-qt5" or "qdbus-qt6" =>
            words.Any(w => w.Contains("org.freedesktop.secrets", StringComparison.OrdinalIgnoreCase) || w.Contains("org.freedesktop.Secret", StringComparison.OrdinalIgnoreCase) || w.Contains("kwallet", StringComparison.OrdinalIgnoreCase)),
        "security" => words.Any(w => w.StartsWith("find-", StringComparison.Ordinal) || w is "dump-keychain" or "export"),
        _ when program.StartsWith("python", StringComparison.Ordinal) || program is "node" or "perl" or "ruby" or "php" or "pwsh" or "powershell" or "bun" or "deno" =>
            words.Skip(1).Any(w => w.Contains("secretstorage", StringComparison.OrdinalIgnoreCase) || w.Contains("org.freedesktop.secrets", StringComparison.OrdinalIgnoreCase)
                || w.Contains("import keyring", StringComparison.Ordinal) || w.Contains("from keyring", StringComparison.Ordinal) || w.Contains(".postman_environment", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>A debugger, a dump or a trace of Karvon's own process, or its memory.</summary>
    static bool OpensKarvon(string program, List<string> words)
    {
        var id = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (words.Any(w => w.StartsWith($"/proc/{id}/", StringComparison.Ordinal) && (w.EndsWith("/mem", StringComparison.Ordinal) || w.EndsWith("/environ", StringComparison.Ordinal) || w.EndsWith("/maps", StringComparison.Ordinal))))
            return true;
        return program.ToLowerInvariant() is "gdb" or "lldb" or "gcore" or "strace" or "ltrace" or "perf" or "netcoredbg" or "createdump" or "dotnet-dump" or "dotnet-gcdump" or "dotnet-trace" or "dotnet-counters" or "dotnet-stack" or "procdump"
            or "dotnet" && words.Skip(1).Any(w => w == id || w.EndsWith("=" + id, StringComparison.Ordinal) || w.Equals("karvon", StringComparison.OrdinalIgnoreCase) || w.Contains("pgrep", StringComparison.Ordinal) || w.Contains("pidof", StringComparison.Ordinal));
    }

    static string? CheckCommand(string command, string? workspace)
    {
        foreach (var words in ShellCommand.Words(command))
        {
            var program = Path.GetFileName(words[0]);
            if (AsksSecretStore(program, words) is { } store)
                return $"Karvon keeps secrets out of Claude's reach: this command asks {store} for what it keeps, so it does not run. {Advice}";
            if (Readers.Contains(program) && words.Skip(1).Select(w => ShellCommand.PathOf(w, workspace)).FirstOrDefault(p => p is not null && SecretFiles.IsSecret(p)) is { } path)
                return $"Karvon keeps secrets out of Claude's reach: this command reads {path}, which holds secrets, so it does not run. {Advice}";
        }
        foreach (Match m in InputRedirection().Matches(command))
            if (ShellCommand.PathOf(m.Groups[1].Value, workspace) is { } path && SecretFiles.IsSecret(path))
                return $"Karvon keeps secrets out of Claude's reach: this command reads {path}, which holds secrets, so it does not run. {Advice}";
        return null;
    }

    /// <summary>The commands that show or copy what a file holds.</summary>
    static readonly HashSet<string> Readers =
    [
        "cat", "tac", "less", "more", "head", "tail", "bat", "batcat", "nl", "xxd", "od", "hexdump", "base64", "base32", "strings",
        "cp", "scp", "rsync", "tar", "zip", "7z", "grep", "egrep", "fgrep", "rg", "ag", "awk", "gawk", "sed", "cut", "sort", "uniq",
        "diff", "cmp", "jq", "yq", "openssl", "gpg", "curl", "wget", "nc", "python", "python3", "node", "perl", "ruby", "php",
        "type", "Get-Content", "gc", "sqlite3", "sqlitebrowser",
    ];

    /// <summary>
    /// The same of a PowerShell command, read as PowerShell reads it (<see cref="ShellCommand.PowerShellWords"/>): its
    /// cmdlets and their aliases whatever their case, a program with or without its .exe, a parameter's value (-Path:file),
    /// and .NET's own file calls.
    /// </summary>
    static string? CheckPowerShell(string command, string? workspace)
    {
        foreach (var words in ShellCommand.PowerShellWords(command))
        {
            var program = ProgramOf(words[0]);
            if ((AsksSecretStore(program.ToLowerInvariant(), words) ?? AsksWindowsStore(program)) is { } store)
                return $"Karvon keeps secrets out of Claude's reach: this command asks {store} for what it keeps, so it does not run. {Advice}";
            // What reads: the command itself, or a .NET file call among its words ([Convert]::ToBase64String([IO.File]::ReadAllBytes(path))).
            var reads = PowerShellReaders.Contains(program) ? 0 : words.FindIndex(w => DotNetFileCall().IsMatch(w));
            if (reads >= 0
                && words.Skip(reads + 1).Select(w => ShellCommand.PowerShellPathOf(ValueOf(w), workspace)).FirstOrDefault(p => p is not null && SecretFiles.IsSecret(p)) is { } path)
                return $"Karvon keeps secrets out of Claude's reach: this command reads {path}, which holds secrets, so it does not run. {Advice}";
        }
        return null;
    }

    /// <summary>The commands that show or copy what a file holds, in PowerShell: Bash's (Git's are on a Windows PATH), its own cmdlets and Windows' programs.</summary>
    static readonly HashSet<string> PowerShellReaders = new(Readers, StringComparer.OrdinalIgnoreCase)
    {
        "Select-String", "sls", "Copy-Item", "copy", "cpi", "Import-Csv", "ipcsv", "Import-Clixml", "Format-Hex", "fhx", "Compress-Archive",
        "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm", "findstr", "certutil", "xcopy", "robocopy",
    };

    /// <summary>A command's name as the lists have it: without its folder and without the .exe (or .cmd, .bat) Windows lets it go by.</summary>
    static string ProgramOf(string word)
    {
        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..];
        var extension = Path.GetExtension(name);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ? name[..^extension.Length] : name;
    }

    /// <summary>A word's path, when it is a parameter with its value (-Path:file); nothing for a parameter's name alone.</summary>
    static string ValueOf(string word) =>
        !word.StartsWith('-') || word.Length == 1 ? word : word.IndexOf(':') is var colon and > 1 ? word[(colon + 1)..] : "";

    /// <summary>Windows' secret stores, as PowerShell's modules ask them (the Credential Manager, a SecretManagement vault).</summary>
    static string? AsksWindowsStore(string program) =>
        program.Equals("Get-StoredCredential", StringComparison.OrdinalIgnoreCase) ? "the Credential Manager"
        : program.Equals("Get-Secret", StringComparison.OrdinalIgnoreCase) ? "PowerShell's secret vault"
        : null;

    /// <summary>.NET's file calls, as PowerShell makes them: [IO.File]::ReadAllText(path), [System.IO.StreamReader]::new(path).</summary>
    [GeneratedRegex(@"^\[(?:System\.)?IO\.(?:File|StreamReader|FileStream)\]::\w+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetFileCall();

    /// <summary>A secret store's name, when the command asks it for a secret.</summary>
    static string? AsksSecretStore(string program, List<string> words)
    {
        string Arg(int i) => i < words.Count ? words[i] : "";
        bool Has(string word) => words.Contains(word, StringComparer.Ordinal);
        return program switch
        {
            "secret-tool" when Arg(1) is "lookup" or "search" => "the system keyring",
            "security" when Arg(1).StartsWith("find-", StringComparison.Ordinal) || Arg(1) is "dump-keychain" or "export" => "the Keychain",
            "gh" when Arg(1) == "auth" && (Arg(2) == "token" || Has("--show-token") || Has("-t")) => "GitHub CLI's sign-in",
            "glab" when Arg(1) == "auth" && Has("--show-token") => "GitLab CLI's sign-in",
            "kubectl" when Arg(1) == "config" && Arg(2) == "view" && Has("--raw") => "the kubeconfig",
            "git" when Arg(1) == "credential" && Arg(2) == "fill" || Arg(1) == "config" && words.Any(w => w.Contains("credential", StringComparison.OrdinalIgnoreCase)) && Has("--get") => "Git's credentials",
            "gcloud" when Arg(1) == "auth" && Arg(2).StartsWith("print-", StringComparison.Ordinal) => "gcloud's sign-in",
            "az" when Arg(1) == "account" && Arg(2) == "get-access-token" => "the Azure CLI's sign-in",
            "aws" when Arg(1) == "configure" && (Arg(2) == "export-credentials" || Arg(2) == "get" && Arg(3).Contains("secret", StringComparison.OrdinalIgnoreCase)) => "the AWS CLI's credentials",
            _ when program.StartsWith("docker-credential-", StringComparison.Ordinal) => "Docker's credential helper",
            _ => null,
        };
    }

    static string? Full(string path, string? workspace)
    {
        try
        {
            return ShellCommand.PathOf(path, workspace) ?? Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"(?<![<>&\d])<\s*([^\s;|&()<>]+)")]
    private static partial Regex InputRedirection();
}

/// <summary>
/// The files that hold secrets: a project's (.env, keys, certificates, credentials, secrets.json) and the user's (SSH's
/// private keys, the cloud CLIs' credentials, the kubeconfig, the keyrings, Claude Code's own sign-in, Karvon's settings
/// and its vault).
/// </summary>
public static class SecretFiles
{
    static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Folders of the user's whose every file holds secrets (relative to the home folder).</summary>
    static readonly string[] Folders =
    [
        ".gnupg", ".kube", ".password-store", ".local/share/keyrings", ".config/karvon", ".microsoft/usersecrets", ".aws/sso/cache",
        "AppData/Roaming/Microsoft/UserSecrets", "AppData/Roaming/Microsoft/Credentials", "AppData/Local/Microsoft/Credentials", "AppData/Roaming/karvon",
        "Library/Keychains",
    ];

    /// <summary>The user's files that hold secrets (relative to the home folder).</summary>
    static readonly string[] Files =
    [
        ".aws/credentials", ".config/gh/hosts.yml", ".config/glab-cli/config.yml", ".docker/config.json", ".git-credentials",
        ".netrc", "_netrc", ".pgpass", ".npmrc", ".pypirc", ".claude/.credentials.json", ".config/gcloud/credentials.db",
        ".config/gcloud/access_tokens.db", ".azure/msal_token_cache.json", ".azure/accessTokens.json",
    ];

    /// <summary>
    /// The user's places that hold secrets, as full paths: the folders and files above, the SSH folder, Karvon's settings
    /// (its vault); what a sandbox denies reading.
    /// </summary>
    public static IEnumerable<string> UserPlaces() => UserPlaces(Home);

    /// <summary>The same, of the home folder <paramref name="home"/> (Karvon's settings wherever they are).</summary>
    public static IEnumerable<string> UserPlaces(string home) =>
        Folders.Concat(Files).Append(".ssh").Select(p => Path.GetFullPath(Path.Combine(home, p)))
            .Append(Path.GetFullPath(Karvon.Services.Settings.ConfigDirectory))
            .Concat(ClaudeSignIns(home))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>The sign-ins of Claude Code's other accounts (~/.claude-NAME).</summary>
    static IEnumerable<string> ClaudeSignIns(string home)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(home, ".claude-*").Select(folder => Path.GetFullPath(Path.Combine(folder, ".credentials.json")))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool IsSecret(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        if (Under(full, Path.Combine(Home, ".ssh")))
            return !IsPublicSshFile(Path.GetFileName(full));
        if (Folders.Any(f => Under(full, Path.Combine(Home, f))) || Files.Any(f => full.Equals(Path.GetFullPath(Path.Combine(Home, f)), Comparison)))
            return true;
        // Karvon's settings, wherever they are: its vault is there.
        if (Under(full, Karvon.Services.Settings.ConfigDirectory))
            return true;
        // Another Claude Code account's sign-in (CLAUDE_CONFIG_DIR, ~/.claude-NAME).
        if (Path.GetFileName(full) == ".credentials.json" && Path.GetFileName(Path.GetDirectoryName(full) ?? "").StartsWith(".claude", Comparison))
            return true;
        return SealedFiles.Is(full) || IsSecretName(Path.GetFileName(full));
    }

    static bool IsPublicSshFile(string name) => name.EndsWith(".pub", StringComparison.Ordinal) || name.StartsWith("known_hosts", StringComparison.Ordinal)
        || name is "config" or "authorized_keys";

    /// <summary>A file of a project's that holds secrets, by its name.</summary>
    public static bool IsSecretName(string fileName)
    {
        var name = fileName.ToLowerInvariant();
        var extension = Path.GetExtension(name);
        if (name == ".env" || name.EndsWith(".env", StringComparison.Ordinal))
            return true;
        // .env.local, .env.production; not its example (.env.example, .env.sample, .env.template, .env.dist).
        if (name.StartsWith(".env.", StringComparison.Ordinal))
            return !(name.EndsWith(".example", StringComparison.Ordinal) || name.EndsWith(".sample", StringComparison.Ordinal)
                || name.EndsWith(".template", StringComparison.Ordinal) || name.EndsWith(".dist", StringComparison.Ordinal));
        if (name is ".npmrc" or ".netrc" or ".pgpass" or ".pypirc" or ".git-credentials" or "credentials" or "credentials.json"
            or "secrets.json" or "secrets.yaml" or "secrets.yml" or "secrets.toml" or ".htpasswd")
            return true;
        if (SealedFiles.IsName(name))
            return true;
        if (name.StartsWith("client_secret", StringComparison.Ordinal) && extension == ".json")
            return true;
        if (name is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" or "id_ecdsa_sk" or "id_ed25519_sk")
            return true;
        return extension is ".pem" or ".key" or ".pfx" or ".p12" or ".jks" or ".keystore" or ".ppk" or ".kdbx";
    }

    static bool Under(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return path.Equals(root, Comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, Comparison);
    }
}
