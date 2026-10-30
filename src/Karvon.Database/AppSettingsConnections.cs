using System.Data.Common;
using System.Text.Json;
using Bengine.Enums;
using Bengine.Introspection.Servers;

namespace Karvon.Database;

/// <summary>A connection string of a .NET project's settings (appsettings*.json, ConnectionStrings).</summary>
/// <param name="File">The settings file, full.</param>
/// <param name="Name">Its name under ConnectionStrings.</param>
/// <param name="Value">The connection string, its password with it: Karvon's to keep, never Claude's to see.</param>
public sealed record AppSettingsConnection(string File, string Name, string Value);

/// <summary>
/// The connection strings of a solution's appsettings*.json, as Karvon reads them for a data source of the Database
/// Explorer: what a project connects to, added without its password going through Claude.
/// </summary>
public static class AppSettingsConnections
{
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages" };

    static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Every connection string of the solution's appsettings*.json files (four folders deep at most).</summary>
    public static IReadOnlyList<AppSettingsConnection> Find(string workspace)
    {
        List<AppSettingsConnection> found = [];
        void Walk(string folder, int depth)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "appsettings*.json").Order(StringComparer.Ordinal))
                    found.AddRange(Read(file));
                if (depth < 4)
                    foreach (var sub in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
                        if (!Skipped.Contains(Path.GetFileName(sub)))
                            Walk(sub, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        Walk(workspace, 0);
        return found;
    }

    /// <summary>A settings file's connection strings; none when it cannot be read.</summary>
    public static IReadOnlyList<AppSettingsConnection> Read(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file), Lenient);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];
            var strings = document.RootElement.EnumerateObject().FirstOrDefault(p => p.Name.Equals("ConnectionStrings", StringComparison.OrdinalIgnoreCase)).Value;
            return strings.ValueKind != JsonValueKind.Object
                ? []
                : [.. strings.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 })
                    .Select(p => new AppSettingsConnection(Path.GetFullPath(file), p.Name, p.Value.GetString()!))];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// A data source for <paramref name="connection"/>: its provider the one given, else told by its keywords (Host:
    /// PostgreSQL; Initial Catalog, Integrated Security, TrustServerCertificate: SQL Server; a .db file: SQLite).
    /// </summary>
    public static ServerProfile Profile(AppSettingsConnection connection, string name, DatabaseProvider? provider = null)
    {
        var values = new DbConnectionStringBuilder();
        try
        {
            values.ConnectionString = connection.Value;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{connection.Name} in {Path.GetFileName(connection.File)} is not a connection string Karvon can read: {ex.Message}");
        }
        string? Get(params string[] keys) => keys.Select(k => values.TryGetValue(k, out var v) ? v?.ToString() : null).FirstOrDefault(v => v is { Length: > 0 });
        var source = Get("Data Source", "DataSource", "Filename");
        var kind = provider ?? Guess(values, source)
            ?? throw new InvalidOperationException($"Which database {connection.Name} connects to is not told by its keywords: give its provider "
                + "(postgresql, sqlserver, mysql, sqlite, oracle).");
        if (kind == DatabaseProvider.Sqlite)
        {
            var file = source ?? throw new InvalidOperationException($"{connection.Name} names no SQLite file (Data Source).");
            return new ServerProfile
            {
                Name = name, Provider = kind, FilePath = Path.GetFullPath(file, Path.GetDirectoryName(connection.File)!),
                Comment = $"From {Path.GetFileName(connection.File)} ({connection.Name})",
            };
        }
        var server = Get("Host", "Server", "Data Source", "Address", "Addr");
        int? port = int.TryParse(Get("Port"), out var p) ? p : null;
        // SQL Server's "host,1433", MySQL's "host:3306".
        if (server is not null && port is null && server.LastIndexOfAny([',', ':']) is var at and > 0 && int.TryParse(server[(at + 1)..], out var given))
        {
            port = given;
            server = server[..at];
        }
        var password = Get("Password", "Pwd");
        return new ServerProfile
        {
            Name = name,
            Provider = kind,
            Host = server ?? throw new InvalidOperationException($"{connection.Name} names no server (Host or Server)."),
            Port = port,
            MaintenanceDatabase = Get("Database", "Initial Catalog"),
            Username = Get("Username", "User Id", "User ID", "UserID", "User", "Uid", "User Name"),
            Password = password,
            SavePassword = password is not null,
            SslMode = Get("SSL Mode", "SslMode"),
            Comment = $"From {Path.GetFileName(connection.File)} ({connection.Name})",
        };
    }

    static DatabaseProvider? Guess(DbConnectionStringBuilder values, string? source)
    {
        bool Has(string key) => values.ContainsKey(key);
        if (source is not null && (source.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) || source == ":memory:"))
            return DatabaseProvider.Sqlite;
        if (Has("Initial Catalog") || Has("Integrated Security") || Has("Trusted_Connection") || Has("TrustServerCertificate") || Has("MultipleActiveResultSets"))
            return DatabaseProvider.SqlServer;
        if (Has("Host"))
            return DatabaseProvider.PostgreSql;
        return null;
    }

    /// <summary>A provider by the name Claude gives it.</summary>
    public static DatabaseProvider? Provider(string? name) => name?.Trim().ToLowerInvariant().Replace(" ", "") switch
    {
        "postgresql" or "postgres" or "pg" or "npgsql" => DatabaseProvider.PostgreSql,
        "sqlserver" or "mssql" or "sqlclient" => DatabaseProvider.SqlServer,
        "mysql" or "mariadb" => DatabaseProvider.MySql,
        "sqlite" => DatabaseProvider.Sqlite,
        "oracle" => DatabaseProvider.Oracle,
        _ => null,
    };
}
