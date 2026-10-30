using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Model;
using Bengine.Introspection.Plans;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Introspection.Tree;
using Karvon.Claude;
using Karvon.Claude.Tools;
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Database;

/// <summary>What the database tools use of Karvon: the Database Explorer's data sources and passwords.</summary>
public interface IDatabaseToolHost
{
    /// <summary>The Database Explorer's data sources: the solution's own, or Bengine Data Studio's list with no solution open.</summary>
    IReadOnlyList<ServerProfile> DataSources();

    /// <summary>Whether the open solution's folder is trusted: its data sources (in its .vs folder) are used only then.</summary>
    bool Trusted => true;

    /// <summary>The password the Database Explorer would use: typed there this session, else the saved one.</summary>
    Task<string?> PasswordForAsync(ServerProfile server);

    /// <summary>The open solution's folder (where a relative export path is); null for none.</summary>
    string? Workspace => null;

    /// <summary>Adds a data source to the Database Explorer (its password kept by Karvon); the one added.</summary>
    ServerProfile AddDataSource(ServerProfile profile) => throw new NotSupportedException("Karvon cannot add a data source here.");
}

/// <summary>Claude Code's database tools.</summary>
public static class DatabaseClaudeTools
{
    public static IEnumerable<IClaudeTool> Create(IDatabaseToolHost host) =>
        [new DbConnectionsTool(host), new DbAddConnectionTool(host), new DbSchemaTool(host), new DbQueryTool(host), new DbExportTool(host), new DbExplainTool(host)];
}

/// <summary>What <c>db_query</c> ran, and its result sets: Karvon shows them in a Query Tool of the data source.</summary>
public sealed record DbQueryRun(ServerProfile Server, string? Database, string Sql, IReadOnlyList<QueryResult> Results);

/// <summary>What <c>db_explain</c> explained, and the plan: Karvon shows it in a Query Tool of the data source.</summary>
public sealed record DbPlanRun(ServerProfile Server, string? Database, string Sql, QueryPlan Plan);

/// <summary>
/// The Database Explorer's data sources, by name, for Claude (through Bengine, as the explorer itself). The user's own
/// connections and passwords are used and never shown; a read-only data source is read-only on the server too, and
/// its changes are refused before they run.
/// </summary>
abstract class DatabaseTool(IDatabaseToolHost host) : IClaudeTool
{
    const int MaxCell = 200;

    protected IDatabaseToolHost Host { get; } = host;

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract JsonObject InputSchema { get; }

    public abstract ClaudeToolPlan Plan(JsonElement input);

    /// <summary>The data source a call names.</summary>
    public string? Target(JsonElement input) => Str(input, "connection");

    /// <summary>A data source of a folder not trusted yet: refused before anyone is asked about it.</summary>
    public virtual string? Refusal(JsonElement input) => Host.Trusted ? null : NotTrusted;

    public async Task<ClaudeToolOutput> CallAsync(JsonElement input, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(input, cancellationToken);
        }
        catch (Exception ex) when (ex is IntrospectionException or InvalidOperationException)
        {
            return ClaudeToolOutput.Error(Redaction.Text(ex.Message));
        }
    }

    protected abstract Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken);

    /// <summary>The data source the call names, or an error that lists the names.</summary>
    protected ServerProfile Server(JsonElement input)
    {
        var name = Str(input, "connection") ?? "";
        if (!Host.Trusted)
            throw new InvalidOperationException(NotTrusted);
        var servers = Host.DataSources();
        return servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(servers.Count == 0
                ? "There are no data sources. The user adds them in Karvon's Database Explorer."
                : $"There is no data source named '{name}'. Data sources: {string.Join(", ", servers.Select(s => s.Name))}.");
    }

    /// <summary>What Claude is told of the data sources of a folder not trusted yet.</summary>
    protected const string NotTrusted = "This folder is not trusted yet: Karvon does not use its data sources (they are in its .vs "
        + "folder, which a repository can bring) until the user trusts it in the Claude Code window.";

    /// <summary>What a data source added from a solution's settings says it came from (and how it is known again).</summary>
    protected static string Comment(AppSettingsConnection connection) => $"From {Path.GetFileName(connection.File)} ({connection.Name})";

    protected DatabaseProvider ProviderOf(JsonElement input) =>
        (Host.Trusted ? Host.DataSources() : []).FirstOrDefault(s => s.Name.Equals(Str(input, "connection"), StringComparison.OrdinalIgnoreCase))?.Provider
        ?? DatabaseProvider.PostgreSql;

    protected async Task<DatabaseSession> OpenAsync(JsonElement input, ServerProfile server, CancellationToken cancellationToken)
    {
        try
        {
            return await ObjectExplorer.OpenSessionAsync(server, await Host.PasswordForAsync(server), Str(input, "database"), cancellationToken);
        }
        catch (IntrospectionException ex) when (ObjectExplorer.IsAuthenticationError(ex))
        {
            throw new InvalidOperationException($"{server.Name} needs a password: ask the user to connect to it in Karvon's Database Explorer first.");
        }
    }

    /// <summary>A change on a read-only data source stops here (the session is read-only on the server as well).</summary>
    protected static void RefuseOnReadOnly(ServerProfile server, ToolAccess access)
    {
        if (server.ReadOnly && access != ToolAccess.Read)
            throw new InvalidOperationException($"{server.Name} is a read-only data source: Karvon does not change it. Tell the user what you wanted to change.");
    }

    /// <summary>
    /// The most any statement of the script may do: a query reads; a change writes; DROP, TRUNCATE and UPDATE or DELETE
    /// without WHERE destroy. Every statement counts, so "SELECT 1; DROP TABLE t" is not a read.
    /// </summary>
    public static ToolAccess SqlAccess(string sql, DatabaseProvider provider)
    {
        var highest = ToolAccess.Read;
        foreach (var statement in SqlScript.Split(sql, provider))
        {
            var info = SqlStatementInfo.Classify(statement.Text, provider);
            var access = info switch
            {
                { CanModify: false } => ToolAccess.Read,
                { IsUnfilteredDml: true } => ToolAccess.Destructive,
                { Kind: SqlStatementKind.Ddl } when info.Verb.Equals("DROP", StringComparison.OrdinalIgnoreCase)
                    || info.Verb.Equals("TRUNCATE", StringComparison.OrdinalIgnoreCase) => ToolAccess.Destructive,
                _ => ToolAccess.Write,
            };
            if (access > highest)
                highest = access;
        }
        return highest;
    }

    /// <summary>The card for a statement that changes something.</summary>
    protected static ClaudeToolPlan SqlPlan(ToolAccess access, string title, string connection, string sql) =>
        new(access, title, $"{connection}: {OneLine(sql)}", access == ToolAccess.Destructive
            ? $"Allow Claude to run destructive SQL on {connection}?"
            : $"Allow Claude to change data in {connection}?")
        {
            Code = sql.Trim(),
            Warning = access == ToolAccess.Destructive
                ? "DROP, TRUNCATE, or UPDATE / DELETE without WHERE: it cannot be undone."
                : access == ToolAccess.Write ? $"It changes {connection}." : null,
        };

    protected static string Cell(object? value)
    {
        var text = value switch
        {
            null or DBNull => "NULL",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            byte[] bytes => $"<{bytes.Length} bytes>",
            _ => value.ToString() ?? "",
        };
        text = text.Replace("\r", "").Replace("\n", "\\n");
        return text.Length > MaxCell ? text[..MaxCell] + "…" : text;
    }
}

/// <summary><c>db_connections</c>: the data sources, what each is, and never their credentials.</summary>
sealed class DbConnectionsTool(IDatabaseToolHost host) : DatabaseTool(host)
{
    public override string Name => "db_connections";

    public override string Description =>
        "Lists the user's database connections (the data sources of Karvon's Database Explorer): name, engine, database, and whether "
        + "the connection is read-only. Use these names with db_schema, db_query and db_explain. Credentials are never shown.";

    public override JsonObject InputSchema => Schema("""{"type":"object","properties":{}}""");

    public override ClaudeToolPlan Plan(JsonElement input) => new(ToolAccess.Read, "Data sources", null, "");

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!Host.Trusted)
            return Task.FromResult(new ClaudeToolOutput(NotTrusted));
        var servers = Host.DataSources();
        // What the solution's settings connect to and the explorer has not: added with db_add_connection, not read by Claude.
        var unknown = Host.Workspace is { } workspace
            ? AppSettingsConnections.Find(workspace).Where(c => !servers.Any(s => s.Comment == Comment(c))).ToList()
            : [];
        var hint = unknown.Count == 0 ? "" : "\nNot added yet, from the solution's settings (add one with db_add_connection; do not read its password): "
            + string.Join(", ", unknown.Select(c => $"{c.Name} in {Path.GetRelativePath(Host.Workspace!, c.File)}")) + ".";
        if (servers.Count == 0)
            return Task.FromResult(new ClaudeToolOutput("There are no data sources. The user adds them in Karvon's Database Explorer." + hint));
        var text = new StringBuilder();
        foreach (var s in servers)
        {
            var where = s.Provider == DatabaseProvider.Sqlite ? s.FilePath : $"{s.MaintenanceDatabase} on {s.Host}{(s.Port is { } port ? ":" + port : "")}";
            text.AppendLine($"{s.Name}: {s.Provider}, {where}{(s.ReadOnly ? ", read-only" : ", writable")}"
                + (s.Comment is { Length: > 0 } comment ? $" ({Redaction.Text(comment)})" : ""));
        }
        return Task.FromResult(new ClaudeToolOutput(text.ToString().TrimEnd() + hint));
    }
}

/// <summary>
/// <c>db_add_connection</c>: a connection string of the solution's appsettings*.json made a data source of the Database
/// Explorer, read by Karvon: its password goes to Karvon's vault, never through Claude. Asks the user.
/// </summary>
sealed class DbAddConnectionTool(IDatabaseToolHost host) : DatabaseTool(host)
{
    public override string Name => "db_add_connection";

    public override string Description =>
        "Adds a connection string of the solution's settings (appsettings*.json, ConnectionStrings) to Karvon's data sources, so "
        + "db_query, db_export and db_schema can use it. Karvon reads the file and keeps the password itself: never read the "
        + "password, nor pass it to a command. db_connections names those not added yet. The user approves it.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "file":{"type":"string","description":"The settings file, relative to the solution's folder (as db_connections names it)."},
          "name":{"type":"string","description":"The connection string's name under ConnectionStrings."},
          "as":{"type":"string","description":"The data source's name (default the connection string's)."},
          "provider":{"type":"string","description":"postgresql, sqlserver, mysql, sqlite or oracle, when its keywords do not tell."}},
         "required":["file","name"]}
        """);

    AppSettingsConnection Find(JsonElement input)
    {
        var workspace = Host.Workspace ?? throw new InvalidOperationException("No solution is open: its settings are where the connection strings are.");
        var file = Path.GetFullPath(Str(input, "file") ?? "", workspace);
        if (!file.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !Path.GetFileName(file).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only an appsettings*.json of the open solution is read for a connection.");
        var connections = AppSettingsConnections.Read(file);
        return connections.FirstOrDefault(c => c.Name.Equals(Str(input, "name"), StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(connections.Count == 0
                ? $"{Path.GetFileName(file)} has no connection strings."
                : $"{Path.GetFileName(file)} has no connection string named {Str(input, "name")}: it has {string.Join(", ", connections.Select(c => c.Name))}.");
    }

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var name = Str(input, "as") ?? Str(input, "name") ?? "";
        string where;
        try
        {
            var profile = AppSettingsConnections.Profile(Find(input), name, AppSettingsConnections.Provider(Str(input, "provider")));
            where = profile.Provider == DatabaseProvider.Sqlite ? profile.FilePath ?? "" : $"{profile.Provider}, {profile.MaintenanceDatabase} on {profile.Host}{(profile.Port is { } port ? ":" + port : "")} as {profile.Username}";
        }
        catch (InvalidOperationException ex)
        {
            where = ex.Message;
        }
        return new(ToolAccess.Write, "Add Data Source", $"{name} from {Str(input, "file")}",
            $"Allow Claude to add the data source {name} from {Path.GetFileName(Str(input, "file") ?? "")}?")
        {
            Code = where,
            Warning = "Karvon reads the connection string and keeps its password in its vault: Claude does not see it.",
        };
    }

    protected override Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        if (!Host.Trusted)
            throw new InvalidOperationException(NotTrusted);
        var connection = Find(input);
        var name = Str(input, "as") ?? connection.Name;
        if (Host.DataSources().FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } taken)
            throw new InvalidOperationException($"There is a data source named {taken.Name} already: give another with 'as'.");
        var added = Host.AddDataSource(AppSettingsConnections.Profile(connection, name, AppSettingsConnections.Provider(Str(input, "provider"))) with
        {
            Comment = Comment(connection),
        });
        var where = added.Provider == DatabaseProvider.Sqlite ? added.FilePath : $"{added.MaintenanceDatabase} on {added.Host}{(added.Port is { } port ? ":" + port : "")}";
        return Task.FromResult(new ClaudeToolOutput($"Added the data source {added.Name} ({added.Provider}, {where}); its password is Karvon's. "
            + $"Use connection \"{added.Name}\" with db_query, db_export and db_schema."));
    }
}

/// <summary><c>db_schema</c>: the live schema as DDL, with approximate row counts.</summary>
sealed class DbSchemaTool(IDatabaseToolHost host) : DatabaseTool(host)
{
    public override string Name => "db_schema";

    public override string Description =>
        "Returns the live schema of a database as DDL (tables, columns, keys, indexes) with approximate row counts. Give a table to "
        + "get only that table. This is the database as it is, not as the code assumes it is.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "connection":{"type":"string","description":"Connection name from db_connections."},
          "database":{"type":"string","description":"A database on the server, if not the connection's own."},
          "schema":{"type":"string","description":"A schema, such as public."},
          "table":{"type":"string","description":"A table to describe on its own."}},
         "required":["connection"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input) =>
        new(ToolAccess.Read, "Schema", Str(input, "connection") + (Str(input, "table") is { } table ? " · " + table : ""), "");

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var server = Server(input);
        await using var session = await OpenAsync(input, server, cancellationToken);
        var schema = Str(input, "schema");
        var tables = await session.Explorer.GetTablesAsync(schema, cancellationToken);
        var text = new StringBuilder();
        if (Str(input, "table") is { } name)
        {
            var match = tables.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"No table '{name}' in {server.Name}. Tables: {string.Join(", ", tables.Select(t => t.QualifiedName))}.");
            RowCounts(text, [match]);
            text.Append(SchemaDdlRenderer.RenderTable(server.Provider, await session.Introspector.IntrospectTableAsync(match, cancellationToken)));
        }
        else
        {
            RowCounts(text, tables);
            text.Append(SchemaDdlRenderer.Render(server.Provider, await session.Introspector.IntrospectAsync(schema, cancellationToken)));
        }
        return new ClaudeToolOutput(text.ToString().TrimEnd());
    }

    static void RowCounts(StringBuilder text, IEnumerable<TableRef> tables)
    {
        foreach (var t in tables.Where(t => t.EstimatedRowCount is not null))
            text.AppendLine($"-- {t.QualifiedName}: ~{t.EstimatedRowCount!.Value.ToString("N0", CultureInfo.InvariantCulture)} rows");
        if (text.Length > 0)
            text.AppendLine();
    }
}

/// <summary><c>db_query</c>: runs SQL and returns the rows; a query reads, a change asks the user first.</summary>
sealed class DbQueryTool(IDatabaseToolHost host) : DatabaseTool(host)
{
    public override string Name => "db_query";

    public override string Description =>
        "Runs SQL on a connection and returns the rows. A SELECT is a read; INSERT, UPDATE, DELETE and DDL are changes the user "
        + "approves first, and read-only connections refuse them. Several statements may be separated by semicolons.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "connection":{"type":"string","description":"Connection name from db_connections."},
          "database":{"type":"string","description":"A database on the server, if not the connection's own."},
          "sql":{"type":"string","description":"The SQL to run."},
          "maxRows":{"type":"integer","description":"Maximum rows to return per statement (default 100, at most 1000)."}},
         "required":["connection","sql"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var sql = Str(input, "sql") ?? "";
        return SqlPlan(SqlAccess(sql, ProviderOf(input)), "Query", Str(input, "connection") ?? "", sql);
    }

    /// <summary>A change to a read-only data source: refused before anyone is asked about it.</summary>
    public override string? Refusal(JsonElement input) =>
        base.Refusal(input) ?? (Host.DataSources().FirstOrDefault(s => s.Name.Equals(Str(input, "connection"), StringComparison.OrdinalIgnoreCase)) is { ReadOnly: true } server
            && Str(input, "sql") is { } sql && SqlAccess(sql, server.Provider) != ToolAccess.Read
                ? $"{server.Name} is a read-only data source: Karvon does not change it. Tell the user what you wanted to change."
                : null);

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var server = Server(input);
        var sql = Str(input, "sql") ?? throw new InvalidOperationException("Give the SQL to run.");
        RefuseOnReadOnly(server, SqlAccess(sql, server.Provider));
        var max = Int(input, "maxRows", 100, 1, 1000);
        await using var session = await OpenAsync(input, server, cancellationToken);
        var results = await session.Query.ExecuteAllAsync(sql, max, cancellationToken);
        var text = new StringBuilder();
        foreach (var result in results)
        {
            if (text.Length > 0)
                text.AppendLine();
            if (!result.IsQuery)
            {
                text.AppendLine($"{(result.RowsAffected >= 0 ? result.RowsAffected + " row(s) affected" : "Done")} ({result.ElapsedMilliseconds:0} ms).");
                continue;
            }
            text.AppendLine($"{result.Rows.Count}{(result.Truncated ? "+" : "")} row(s) ({result.ElapsedMilliseconds:0} ms)"
                + $"{(result.Truncated ? "; more rows exist, raise maxRows or narrow the query" : "")}.");
            text.AppendLine(string.Join(" | ", result.Columns));
            foreach (var row in result.Rows)
                text.AppendLine(string.Join(" | ", row.Select(Cell)));
        }
        return new ClaudeToolOutput(text.ToString().TrimEnd())
        {
            Detail = new DbQueryRun(server, Str(input, "database"), sql, results),
            Outside = results.Any(r => r.IsQuery && r.Rows.Count > 0) ? $"the rows of {server.Name}" : null,
        };
    }
}

/// <summary>
/// <c>db_export</c>: a query's rows, all of them, written by Karvon to a file (Excel, CSV, TSV, JSON) through its own
/// connection; Claude gets where the file is and how many rows it has, never the rows' text to copy, nor a password.
/// </summary>
sealed class DbExportTool(IDatabaseToolHost host) : DatabaseTool(host)
{
    /// <summary>Excel's own limit, less its header.</summary>
    const int MaxRows = 1_048_575;

    static readonly string[] Formats = ["xlsx", "csv", "tsv", "json"];

    public override string Name => "db_export";

    public override string Description =>
        "Writes all the rows a query reads to a file, through Karvon's connection: Excel (.xlsx), CSV, TSV or JSON. Use it to export "
        + "data (db_query returns at most 1000 rows as text). Returns the file's path and its row count. A new file is written at once; "
        + "one that exists is replaced only when the user says so.";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "connection":{"type":"string","description":"Connection name from db_connections."},
          "database":{"type":"string","description":"A database on the server, if not the connection's own."},
          "sql":{"type":"string","description":"The query whose rows to export (one that only reads)."},
          "format":{"type":"string","enum":["xlsx","csv","tsv","json"],"description":"The file's format (default xlsx)."},
          "path":{"type":"string","description":"Where to write it; relative to the solution's folder. Default: a new file in the Downloads folder."},
          "sheet":{"type":"string","description":"The Excel sheet's name (default the connection's)."}},
         "required":["connection","sql"]}
        """);

    string Format(JsonElement input) =>
        Str(input, "format")?.Trim().TrimStart('.').ToLowerInvariant() is { } format && Formats.Contains(format) ? format
            : Path.GetExtension(Str(input, "path") ?? "").TrimStart('.').ToLowerInvariant() is var extension && Formats.Contains(extension) ? extension
            : "xlsx";

    /// <summary>The file it writes: the path given (relative to the solution's folder), else a new one in Downloads.</summary>
    string PathOf(JsonElement input)
    {
        var format = Format(input);
        if (Str(input, "path") is { Length: > 0 } given)
        {
            var full = Path.GetFullPath(given.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + given[1..] : given,
                Host.Workspace ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            return Path.HasExtension(full) ? full : full + "." + format;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Directory.Exists(Path.Combine(home, "Downloads")) ? Path.Combine(home, "Downloads") : home;
        var name = string.Concat((Str(input, "connection") ?? "export").Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
        return Path.Combine(folder, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.{format}");
    }

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var path = PathOf(input);
        var replaces = File.Exists(path);
        var subject = $"{Str(input, "connection")}: {OneLine(Str(input, "sql") ?? "")} → {path}";
        return replaces
            ? new(ToolAccess.Write, "Export", subject, $"Allow Claude to replace {Path.GetFileName(path)} with the rows of {Str(input, "connection")}?")
            {
                Code = Str(input, "sql"),
                Warning = $"{path} exists: it is written over.",
            }
            : new(ToolAccess.Run, "Export", subject, "") { Code = Str(input, "sql") };
    }

    /// <summary>A change is no export: refused before anyone is asked.</summary>
    public override string? Refusal(JsonElement input) =>
        base.Refusal(input) ?? (Str(input, "sql") is { } sql && SqlAccess(sql, ProviderOf(input)) != ToolAccess.Read
            ? "db_export writes what a query reads; it runs no change. Run changes with db_query, which asks the user."
            : null);

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var server = Server(input);
        var sql = Str(input, "sql") ?? throw new InvalidOperationException("Give the query whose rows to export.");
        if (SqlAccess(sql, server.Provider) != ToolAccess.Read)
            throw new InvalidOperationException("db_export writes what a query reads; it runs no change.");
        var path = PathOf(input);
        var format = Format(input);
        await using var session = await OpenAsync(input, server, cancellationToken);
        var result = (await session.Query.ExecuteAllAsync(sql, MaxRows, cancellationToken)).LastOrDefault(r => r.IsQuery)
            ?? throw new InvalidOperationException("The query returned no rows to export (no result set).");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            if (format == "xlsx")
                await File.WriteAllBytesAsync(path, Bengine.Introspection.Export.XlsxWriter.Write(SheetName(Str(input, "sheet") ?? server.Name), result.Columns, result.Rows), cancellationToken);
            else
                await File.WriteAllTextAsync(path, format switch
                {
                    "csv" => Bengine.Introspection.Export.DataExporter.ToCsv(result.Columns, result.Rows),
                    "tsv" => Bengine.Introspection.Export.DataExporter.ToTsv(result.Columns, result.Rows),
                    _ => Bengine.Introspection.Export.DataExporter.ToJson(result.Columns, result.Rows),
                }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{path} could not be written: {ex.Message}");
        }
        return new ClaudeToolOutput($"Exported {result.Rows.Count} row(s){(result.Truncated ? $" (the first {MaxRows}: Excel holds no more)" : "")} "
            + $"of {result.Columns.Count} column(s) ({string.Join(", ", result.Columns)}) to {path} ({result.ElapsedMilliseconds:0} ms).");
    }

    /// <summary>An Excel sheet's name: at most 31 characters, none of : \ / ? * [ ].</summary>
    static string SheetName(string name)
    {
        var clean = string.Concat(name.Select(c => c is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '-' : c)).Trim();
        return clean.Length == 0 ? "Export" : clean.Length > 31 ? clean[..31] : clean;
    }
}

/// <summary><c>db_explain</c>: the execution plan as a tree, with warnings; with analyze it runs the statement.</summary>
sealed class DbExplainTool(IDatabaseToolHost host) : DatabaseTool(host)
{
    public override string Name => "db_explain";

    public override string Description =>
        "Shows the execution plan of a statement as a tree with costs and row counts, and flags full scans of large tables. With "
        + "analyze it executes the statement to report actual times (a modifying statement is rolled back, and counts as a change).";

    public override JsonObject InputSchema => Schema("""
        {"type":"object","properties":{
          "connection":{"type":"string","description":"Connection name from db_connections."},
          "database":{"type":"string","description":"A database on the server, if not the connection's own."},
          "sql":{"type":"string","description":"The statement to explain."},
          "analyze":{"type":"boolean","description":"Execute it and report actual rows and times."}},
         "required":["connection","sql"]}
        """);

    public override ClaudeToolPlan Plan(JsonElement input)
    {
        var sql = Str(input, "sql") ?? "";
        // EXPLAIN ANALYZE runs the statement: Bengine rolls a change back, but it still runs.
        var access = Bool(input, "analyze") ? SqlAccess(sql, ProviderOf(input)) : ToolAccess.Read;
        return SqlPlan(access, Bool(input, "analyze") ? "Explain Analyze" : "Explain", Str(input, "connection") ?? "", sql);
    }

    protected override async Task<ClaudeToolOutput> RunAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var server = Server(input);
        var sql = Str(input, "sql") ?? throw new InvalidOperationException("Give the statement to explain.");
        var analyze = Bool(input, "analyze");
        RefuseOnReadOnly(server, analyze ? SqlAccess(sql, server.Provider) : ToolAccess.Read);
        await using var session = await OpenAsync(input, server, cancellationToken);
        var plan = await PlanReader.ExplainAsync(session, sql, analyze, cancellationToken);
        var text = new StringBuilder(plan.Analyzed ? "Plan (actual)" : "Plan (estimated)");
        if (plan.PlanningTimeMs is { } planning)
            text.Append($", planning {planning:0.###} ms");
        if (plan.ExecutionTimeMs is { } execution)
            text.Append($", execution {execution:0.###} ms");
        text.AppendLine(":");
        foreach (var (node, depth) in plan.Flatten())
        {
            var indent = new string(' ', depth * 2);
            var line = new StringBuilder(indent).Append("-> ").Append(node.Operation);
            if (node.Object is not null)
                line.Append(" on ").Append(node.Object);
            var numbers = new List<string>();
            if (node.TotalCost is { } cost)
                numbers.Add($"cost {node.StartupCost ?? 0:0.##}..{cost:0.##}");
            if (node.EstimatedRows is { } estimated)
                numbers.Add($"rows ~{estimated:0}");
            if (node.ActualRows is { } actual)
                numbers.Add($"actual rows {actual:0}");
            if (node.ActualTimeMs is { } time)
                numbers.Add($"time {time:0.###} ms");
            if (numbers.Count > 0)
                line.Append("  (").Append(string.Join(", ", numbers)).Append(')');
            text.AppendLine(line.ToString());
            foreach (var (key, value) in node.Details)
                text.AppendLine($"{indent}     {key}: {value}");
            foreach (var warning in node.Warnings)
                text.AppendLine($"{indent}     ! {warning}");
        }
        return new ClaudeToolOutput(text.ToString().TrimEnd()) { Detail = new DbPlanRun(server, Str(input, "database"), sql, plan) };
    }
}
