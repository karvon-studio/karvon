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
using static Karvon.Claude.Tools.ToolInput;

namespace Karvon.Claude.Tools;

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
        var servers = Host.DataSources();
        return servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(servers.Count == 0
                ? "There are no data sources. The user adds them in Karvon's Database Explorer."
                : $"There is no data source named '{name}'. Data sources: {string.Join(", ", servers.Select(s => s.Name))}.");
    }

    protected DatabaseProvider ProviderOf(JsonElement input) =>
        Host.DataSources().FirstOrDefault(s => s.Name.Equals(Str(input, "connection"), StringComparison.OrdinalIgnoreCase))?.Provider
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
        var servers = Host.DataSources();
        if (servers.Count == 0)
            return Task.FromResult(new ClaudeToolOutput("There are no data sources. The user adds them in Karvon's Database Explorer."));
        var text = new StringBuilder();
        foreach (var s in servers)
        {
            var where = s.Provider == DatabaseProvider.Sqlite ? s.FilePath : $"{s.MaintenanceDatabase} on {s.Host}{(s.Port is { } port ? ":" + port : "")}";
            text.AppendLine($"{s.Name}: {s.Provider}, {where}{(s.ReadOnly ? ", read-only" : ", writable")}"
                + (s.Comment is { Length: > 0 } comment ? $" ({Redaction.Text(comment)})" : ""));
        }
        return Task.FromResult(new ClaudeToolOutput(text.ToString().TrimEnd()));
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
        return new ClaudeToolOutput(text.ToString().TrimEnd()) { Detail = new DbQueryRun(server, Str(input, "database"), sql, results) };
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
