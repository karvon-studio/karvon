using System.Text.Json;
using System.Text.RegularExpressions;

namespace Karvon.Claude;

/// <summary>
/// What keeps Claude Code's own tools away from the secrets on the machine, as Karvon's PreToolUse hook answers: a file
/// that holds secrets is not read, edited or written by Read, Edit, Write, NotebookEdit or Grep, and a shell command that
/// reads one, or asks a secret store for what it keeps, does not run. A guard against what slips, not a boundary of its
/// own: the shell can reach them by ways no list has.
/// </summary>
public static partial class SecretGuard
{
    /// <summary>The tools whose calls the hook is asked about.</summary>
    public const string Tools = "Read|Edit|MultiEdit|Write|NotebookEdit|Grep|Bash";

    const string Advice = "Ask the user for what you need from it (never the secret itself), or use Karvon's tools, which sign in for you.";

    /// <summary>Why the call may not run, or null when it may.</summary>
    public static string? Check(string tool, JsonElement input, string? workspace)
    {
        if (tool == "Bash")
            return Str(input, "command") is { } command ? CheckCommand(command, workspace) : null;
        var path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? (tool == "Grep" ? Str(input, "path") : null);
        return path is not null && Full(path, workspace) is { } full && SecretFiles.IsSecret(full)
            ? $"Karvon keeps secrets out of Claude's reach: {path} holds secrets, so Claude may not read or change it. {Advice}"
            : null;
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
    public static IEnumerable<string> UserPlaces() =>
        Folders.Concat(Files).Append(".ssh").Select(p => Path.GetFullPath(Path.Combine(Home, p)))
            .Append(Path.GetFullPath(Karvon.Services.Settings.ConfigDirectory))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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
        return IsSecretName(Path.GetFileName(full));
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
