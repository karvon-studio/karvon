using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Karvon.CodeMap;

/// <summary>A file of the map: where it is (relative to the workspace, with /), what it was when read, and its summary.</summary>
public sealed record MapFile(string Path, long Size, long Modified, string Hash, string? Language, int Lines, bool Outlined)
{
    public string? Summary { get; init; }

    /// <summary>Whether the file was read (text, not too large nor generated), not only listed.</summary>
    public bool IsRead => Hash.Length > 0;

    /// <summary>The hash of the file the summary was written for: another one, and it is to be written again.</summary>
    public string? SummaryHash { get; init; }
}

/// <summary>A declaration of a file: its kind, name, what it is in, its signature, its lines (from 1) and its doc comment's first sentence.</summary>
public sealed record MapSymbol(string Kind, string Name, string? Container, string? Detail, int Line, int EndLine, string? Doc, int Depth);

/// <summary>A folder's summary, and the hash of what it was written from (its files' and folders' summaries).</summary>
public sealed record MapFolder(string Path, string? Summary, string? SummaryHash);

/// <summary>What a search found: a declaration (its kind), a file or a folder, and the text that goes with it.</summary>
public sealed record MapHit(string Kind, string Path, int Line, string Name, string? Container, string? Detail, string? Text)
{
    public bool IsFile => Kind == CodeMapStore.FileKind;

    public bool IsFolder => Kind == CodeMapStore.FolderKind;
}

/// <summary>
/// The map of a workspace, in a SQLite file of the solution's own folder (.vs/…/Karvon): its files, their declarations,
/// the summaries, and a full-text index (FTS5) of all of them that a search ranks by name first, then by the words of the
/// name (OrderTotals is found by "totals"), the path, the signature, the doc comment and the summary. One file's rows
/// are replaced together, so the map follows the workspace a file at a time. Safe to use from any thread.
/// </summary>
public sealed class CodeMapStore : IDisposable
{
    public const string FileKind = "file", FolderKind = "folder";

    /// <summary>The schema's version: a store of another is made anew (it is only a cache of the workspace).</summary>
    const int Version = 2;

    /// <summary>Where the files' and the folders' search rows are among the declarations' (whose ids are the rows' own).</summary>
    const long FileRows = -1, FolderRows = -1_000_000_000_000;

    readonly Lock _lock = new();
    readonly string? _file;
    SqliteConnection _db;
    bool _disposed;

    /// <param name="file">The map's file, made anew when it is damaged (it is only a cache of the workspace); null for a map in memory.</param>
    public CodeMapStore(string? file)
    {
        _file = file;
        _db = Open();
    }

    /// <summary>The store in the first of <paramref name="files"/> that can be written, or in memory (made again the next time) when none can.</summary>
    public static CodeMapStore OpenFirst(params IEnumerable<string> files)
    {
        foreach (var file in files)
            try
            {
                return new CodeMapStore(file);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
            {
            }
        return new CodeMapStore(null);
    }

    /// <summary>Whether the map is kept in a file (not in memory, as when no folder for it can be written).</summary>
    public bool IsKept => _file is not null;

    SqliteConnection Open()
    {
        if (_file is null)
            return Connect(":memory:");
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        try
        {
            return Connect(_file);
        }
        catch (SqliteException ex) when (IsDamaged(ex))
        {
            DeleteFile();
            return Connect(_file);
        }
    }

    static SqliteConnection Connect(string file)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
        try
        {
            db.Open();
            // SQLite's own lower() knows only ASCII.
            db.CreateFunction("lower_all", (string? text) => text?.ToLowerInvariant(), isDeterministic: true);
            Execute(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            Execute(db, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT)");
            if (Scalar(db, "SELECT value FROM meta WHERE key = 'version'") is not string version || version != Version.ToString())
                Execute(db, """
                    DROP TABLE IF EXISTS files; DROP TABLE IF EXISTS symbols; DROP TABLE IF EXISTS folders; DROP TABLE IF EXISTS search;
                    CREATE TABLE files(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, size INTEGER, modified INTEGER, hash TEXT,
                        language TEXT, lines INTEGER, outlined INTEGER, summary TEXT, summary_hash TEXT);
                    CREATE TABLE symbols(id INTEGER PRIMARY KEY, path TEXT NOT NULL, kind TEXT, name TEXT, container TEXT, detail TEXT,
                        line INTEGER, end_line INTEGER, doc TEXT, depth INTEGER);
                    CREATE INDEX symbols_path ON symbols(path);
                    CREATE TABLE folders(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, summary TEXT, summary_hash TEXT);
                    CREATE VIRTUAL TABLE search USING fts5(kind UNINDEXED, path, line UNINDEXED, name, words, container, detail, text,
                        owner UNINDEXED, tokenize = 'unicode61');
                    """);
            // Written at each opening: a file that can only be read is known here, not at the first scan.
            Execute(db, "INSERT OR REPLACE INTO meta(key, value) VALUES ('version', $v), ('opened', $t)",
                ("$v", Version.ToString()), ("$t", DateTime.UtcNow.ToString("O")));
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>The file is not a database, or a part of it cannot be read.</summary>
    static bool IsDamaged(SqliteException ex) => ex.SqliteErrorCode is 11 or 26;

    void DeleteFile()
    {
        foreach (var file in new[] { _file!, _file + "-wal", _file + "-shm" })
            File.Delete(file);
    }

    /// <summary>What a call does with the database, one at a time; a damaged one is made anew first (the next scan reads the workspace into it).</summary>
    T Use<T>(Func<T> body)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                return body();
            }
            catch (SqliteException ex) when (IsDamaged(ex) && _file is not null)
            {
                _db.Dispose();
                DeleteFile();
                _db = Open();
                return body();
            }
        }
    }

    void Use(Action body) => Use(() =>
    {
        body();
        return true;
    });

    // ---- Files ------------------------------------------------------------------------------------------------

    /// <summary>The files of the map, by their paths.</summary>
    public IReadOnlyDictionary<string, MapFile> Files()
    {
        return Use(() =>
        {
            var files = new Dictionary<string, MapFile>(StringComparer.Ordinal);
            using var command = Command("SELECT path, size, modified, hash, language, lines, outlined, summary, summary_hash FROM files");
            using var reader = command.ExecuteReader();
            while (reader.Read())
                files[reader.GetString(0)] = new MapFile(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6) != 0)
                {
                    Summary = reader.IsDBNull(7) ? null : reader.GetString(7),
                    SummaryHash = reader.IsDBNull(8) ? null : reader.GetString(8),
                };
            return files;
        });
    }

    /// <summary>A file read (again): its row, its declarations and their search rows, all at once. Its summary stays until a new one is written.</summary>
    public void Put(MapFile file, IReadOnlyList<MapSymbol> symbols)
    {
        Use(() =>
        {
            using var transaction = _db.BeginTransaction();
            DeleteSymbols(file.Path);
            Execute("""
                INSERT INTO files(path, size, modified, hash, language, lines, outlined) VALUES ($p, $s, $m, $h, $l, $n, $o)
                ON CONFLICT(path) DO UPDATE SET size = $s, modified = $m, hash = $h, language = $l, lines = $n, outlined = $o
                """,
                ("$p", file.Path), ("$s", file.Size), ("$m", file.Modified), ("$h", file.Hash), ("$l", file.Language), ("$n", file.Lines),
                ("$o", file.Outlined ? 1 : 0));
            foreach (var symbol in symbols)
            {
                var id = (long)Scalar("""
                    INSERT INTO symbols(path, kind, name, container, detail, line, end_line, doc, depth) VALUES ($p, $k, $n, $c, $d, $l, $e, $doc, $depth)
                    RETURNING id
                    """,
                    ("$p", file.Path), ("$k", symbol.Kind), ("$n", symbol.Name), ("$c", symbol.Container), ("$d", symbol.Detail), ("$l", symbol.Line),
                    ("$e", symbol.EndLine), ("$doc", symbol.Doc), ("$depth", symbol.Depth))!;
                // The container's words are found too, below the declaration's own (a member below its type).
                InsertSearch(id, symbol.Kind, file.Path, symbol.Line, symbol.Name, CodeText.Words(symbol.Name),
                    symbol.Container is { } c ? string.Join(' ', c.Split('.').Select(CodeText.Words)) : null, symbol.Detail, symbol.Doc, symbol.Container);
            }
            PutFileSearch(file.Path, Summary(file.Path));
            transaction.Commit();
        });
    }

    /// <summary>A file gone from the workspace: its rows go.</summary>
    public void Remove(string path)
    {
        Use(() =>
        {
            using var transaction = _db.BeginTransaction();
            DeleteSymbols(path);
            if (Scalar("SELECT id FROM files WHERE path = $p", ("$p", path)) is long id)
                Execute("DELETE FROM search WHERE rowid = $r", ("$r", FileRows - id));
            Execute("DELETE FROM files WHERE path = $p", ("$p", path));
            transaction.Commit();
        });
    }

    /// <summary>A file's summary, written for the file as it is (its hash); searched with its name from now on.</summary>
    public void SetSummary(string path, string hash, string summary)
    {
        Use(() =>
        {
            using var transaction = _db.BeginTransaction();
            Execute("UPDATE files SET summary = $s, summary_hash = $h WHERE path = $p", ("$s", summary), ("$h", hash), ("$p", path));
            PutFileSearch(path, summary);
            transaction.Commit();
        });
    }

    /// <summary>A file's declarations, in their order in it.</summary>
    public IReadOnlyList<MapSymbol> Symbols(string path)
    {
        return Use(() =>
        {
            var symbols = new List<MapSymbol>();
            using var command = Command("SELECT kind, name, container, detail, line, end_line, doc, depth FROM symbols WHERE path = $p ORDER BY id", ("$p", path));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                symbols.Add(new MapSymbol(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), reader.GetInt32(4), reader.GetInt32(5),
                    Text(reader, 6), reader.GetInt32(7)));
            return symbols;
        });
    }

    /// <summary>Whether the map has a file of this path, or any in the folder of this path ("" is the workspace).</summary>
    public bool Has(string path) => Use(() => path.Length == 0
        || Scalar("SELECT 1 FROM files WHERE path = $p OR (path > $p || '/' AND path < $p || '0') LIMIT 1", ("$p", path)) is not null);

    /// <summary>How many declarations the map has.</summary>
    public int SymbolCount()
    {
        return Use(() => Convert.ToInt32(Scalar("SELECT count(*) FROM symbols")));
    }

    void DeleteSymbols(string path)
    {
        Execute("DELETE FROM search WHERE rowid IN (SELECT id FROM symbols WHERE path = $p)", ("$p", path));
        Execute("DELETE FROM symbols WHERE path = $p", ("$p", path));
    }

    string? Summary(string path) => Scalar("SELECT summary FROM files WHERE path = $p", ("$p", path)) as string;

    void PutFileSearch(string path, string? summary)
    {
        if (Scalar("SELECT id FROM files WHERE path = $p", ("$p", path)) is not long id)
            return;
        var name = System.IO.Path.GetFileName(path);
        Execute("DELETE FROM search WHERE rowid = $r", ("$r", FileRows - id));
        InsertSearch(FileRows - id, FileKind, path, 0, name,
            string.Join(' ', path.Split('/').Select(p => CodeText.Words(System.IO.Path.GetFileNameWithoutExtension(p)))), null, null, summary);
    }

    // ---- Folders ----------------------------------------------------------------------------------------------

    public IReadOnlyDictionary<string, MapFolder> Folders()
    {
        return Use(() =>
        {
            var folders = new Dictionary<string, MapFolder>(StringComparer.Ordinal);
            using var command = Command("SELECT path, summary, summary_hash FROM folders");
            using var reader = command.ExecuteReader();
            while (reader.Read())
                folders[reader.GetString(0)] = new MapFolder(reader.GetString(0), Text(reader, 1), Text(reader, 2));
            return folders;
        });
    }

    /// <summary>A folder's summary ("" is the workspace's own), written from its files' and folders' (their hash).</summary>
    public void SetFolderSummary(string path, string hash, string summary)
    {
        Use(() =>
        {
            using var transaction = _db.BeginTransaction();
            Execute("""
                INSERT INTO folders(path, summary, summary_hash) VALUES ($p, $s, $h)
                ON CONFLICT(path) DO UPDATE SET summary = $s, summary_hash = $h
                """, ("$p", path), ("$s", summary), ("$h", hash));
            var id = (long)Scalar("SELECT id FROM folders WHERE path = $p", ("$p", path))!;
            Execute("DELETE FROM search WHERE rowid = $r", ("$r", FolderRows - id));
            var name = path.Length == 0 ? "/" : System.IO.Path.GetFileName(path);
            InsertSearch(FolderRows - id, FolderKind, path, 0, name, string.Join(' ', path.Split('/').Select(CodeText.Words)), null, null, summary);
            transaction.Commit();
        });
    }

    /// <summary>A folder gone (no file of the map is in it any more): its summary goes.</summary>
    public void RemoveFolder(string path)
    {
        Use(() =>
        {
            if (Scalar("SELECT id FROM folders WHERE path = $p", ("$p", path)) is long id)
                Execute("DELETE FROM search WHERE rowid = $r", ("$r", FolderRows - id));
            Execute("DELETE FROM folders WHERE path = $p", ("$p", path));
        });
    }

    // ---- Search -----------------------------------------------------------------------------------------------

    /// <summary>
    /// What a query finds, best first: every word of it (the start of a word of a name, a path, a signature, a doc comment or
    /// a summary), the name matching it exactly first; with no such hit, any of its words. <paramref name="kind"/>: "file",
    /// "folder", "declaration" or null for all; <paramref name="under"/>: a folder's path, or null for the workspace.
    /// </summary>
    public IReadOnlyList<MapHit> Search(string query, string? kind = null, string? under = null, int limit = 25)
    {
        var words = QueryWords(query);
        if (words.Count == 0)
            return [];
        var exact = Regex.Matches(query, @"[\p{L}\p{M}\p{N}_$]+").LastOrDefault()?.Value.ToLowerInvariant() ?? "";
        return Use(() =>
        {
            var hits = Find(string.Join(' ', words.Select(Quote)), exact, kind, under, limit);
            return hits.Count > 0 || words.Count == 1 ? hits : Find(string.Join(" OR ", words.Select(Quote)), exact, kind, under, limit);
        });
    }

    List<MapHit> Find(string match, string exact, string? kind, string? under, int limit)
    {
        var sql = new StringBuilder("""
            SELECT kind, path, line, name, owner, detail, text FROM search WHERE search MATCH $q
            """);
        var parameters = new List<(string, object?)> { ("$q", match), ("$exact", exact), ("$limit", limit) };
        if (kind == FileKind || kind == FolderKind)
        {
            sql.Append(" AND kind = $kind");
            parameters.Add(("$kind", kind));
        }
        else if (kind == "declaration")
            sql.Append($" AND kind NOT IN ('{FileKind}', '{FolderKind}')");
        if (under is { Length: > 0 })
        {
            sql.Append(@" AND (path = $under OR path LIKE $prefix ESCAPE '\')");
            parameters.Add(("$under", under.TrimEnd('/')));
            parameters.Add(("$prefix", Like(under.TrimEnd('/')) + "/%"));
        }
        // The name itself first (a file's without its extension), a type before a file, a function and a member of that name;
        // then the rank, by weights in the columns' order: kind, path, line, name, words, container, detail, text, owner.
        sql.Append("""
             ORDER BY (lower_all(name) = $exact OR (kind = 'file' AND lower_all(name) LIKE $exact || '.%')) DESC,
                CASE WHEN lower_all(name) = $exact OR (kind = 'file' AND lower_all(name) LIKE $exact || '.%') THEN
                    CASE kind WHEN 'class' THEN 0 WHEN 'interface' THEN 0 WHEN 'struct' THEN 0 WHEN 'enum' THEN 0 WHEN 'module' THEN 0
                        WHEN 'file' THEN 1 WHEN 'folder' THEN 1 WHEN 'function' THEN 2 ELSE 3 END
                ELSE 0 END,
                bm25(search, 0, 2.0, 0, 10.0, 6.0, 2.0, 1.0, 1.0, 0)
             LIMIT $limit
            """);
        var hits = new List<MapHit>();
        using var command = Command(sql.ToString(), [.. parameters]);
        try
        {
            using var reader = command.ExecuteReader();
            while (reader.Read())
                hits.Add(new MapHit(reader.GetString(0), reader.GetString(1), Convert.ToInt32(reader.GetValue(2)), reader.GetString(3), Text(reader, 4),
                    Text(reader, 5), Text(reader, 6)));
        }
        catch (SqliteException ex) when (!IsDamaged(ex))
        {
            // A query FTS5 cannot read finds nothing.
        }
        return hits;
    }

    /// <summary>A query's words as the index has them: each name's words (and the whole name), lower case.</summary>
    static List<string> QueryWords(string query) =>
        [.. Regex.Matches(query, @"[\p{L}\p{M}\p{N}_$]+").SelectMany(m => CodeText.Words(m.Value).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Distinct()];

    static string Quote(string word) => "\"" + word.Replace("\"", "\"\"") + "\"*";

    static string Like(string text) => text.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    void InsertSearch(long rowid, string kind, string path, int line, string name, string words, string? container, string? detail, string? text,
        string? owner = null) =>
        Execute("""
            INSERT INTO search(rowid, kind, path, line, name, words, container, detail, text, owner) VALUES ($r, $k, $p, $l, $n, $w, $c, $d, $t, $o)
            """,
            ("$r", rowid), ("$k", kind), ("$p", path), ("$l", line), ("$n", name), ("$w", words), ("$c", container), ("$d", detail), ("$t", text),
            ("$o", owner));

    // ---- SQLite -----------------------------------------------------------------------------------------------

    static string? Text(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : reader.GetString(i);

    static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, sql, parameters);
        command.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(db, sql, parameters);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters) => Command(_db, sql, parameters);

    void Execute(string sql, params (string Name, object? Value)[] parameters) => Execute(_db, sql, parameters);

    object? Scalar(string sql, params (string Name, object? Value)[] parameters) => Scalar(_db, sql, parameters);

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _db.Dispose();
        }
    }
}
