using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using Microsoft.Data.Sqlite;

namespace CodeIndexer.Tests;

/// <summary>
/// A test that documents behavior the indexer is meant to have but does not have yet. It is skipped in a
/// normal run so the suite stays green and honest; set CODEINDEX_RUN_PENDING=1 to run these and see which
/// still fail. When one starts passing, change it to a plain [Fact].
/// </summary>
public sealed class PendingFactAttribute : FactAttribute
{
    public PendingFactAttribute(string phase, string what)
    {
        if (Environment.GetEnvironmentVariable("CODEINDEX_RUN_PENDING") != "1")
            Skip = $"Pending (plan phase {phase}): {what}";
    }
}

/// <summary>
/// Indexes the Semantics fixture solution once and exposes SQL helpers for golden-result tests.
/// Expected locations are found by "// @marker" comments in the fixture source, never by line number.
/// </summary>
public sealed class SemanticsIndex : IDisposable
{
    private readonly string _dbPath;
    private readonly string _fixtureDir;
    public SqliteConnection Conn { get; }

    public SemanticsIndex()
    {
        var solution = Fixtures.Semantics;
        _fixtureDir = Path.GetDirectoryName(solution)!;
        _dbPath = Fixtures.TempDbPath();

        var load = new SolutionLoader().LoadAsync(solution).GetAwaiter().GetResult();
        new IndexEngine(Fixtures.RepoRoot).IndexSolutionAsync(_dbPath, load).GetAwaiter().GetResult();

        Conn = new SqliteConnection($"Data Source={_dbPath};Pooling=False");
        Conn.Open();
    }

    public void Dispose()
    {
        Conn.Dispose();
        Fixtures.DeleteDb(_dbPath);
    }

    // ---- markers -------------------------------------------------------------------------------

    /// <summary>1-based line of the single line in <paramref name="fileName"/> ending with "// @marker".</summary>
    public int Line(string fileName, string marker)
    {
        var file = Directory.EnumerateFiles(_fixtureDir, fileName, SearchOption.AllDirectories)
            .Single(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        var lines = File.ReadAllLines(file);
        var hits = lines.Select((text, i) => (text, line: i + 1))
            .Where(x => x.text.TrimEnd().EndsWith("// @" + marker, StringComparison.Ordinal))
            .ToList();
        return hits.Count == 1
            ? hits[0].line
            : throw new InvalidOperationException($"Expected exactly one '// @{marker}' in {fileName}, found {hits.Count}.");
    }

    // ---- symbols -------------------------------------------------------------------------------

    /// <summary>Row id of the symbol whose documentation id (the part of the key after '|') is <paramref name="docId"/>.</summary>
    public long Id(string docId)
    {
        var ids = Query<long>("SELECT id FROM symbols WHERE substr(key, instr(key, '|') + 1) = @d", ("@d", docId));
        return ids.Count == 1
            ? ids[0]
            : throw new InvalidOperationException($"Expected one symbol with documentation id '{docId}', found {ids.Count}.");
    }

    public bool Exists(string docId) =>
        Query<long>("SELECT id FROM symbols WHERE substr(key, instr(key, '|') + 1) = @d", ("@d", docId)).Count > 0;

    public string DocId(long symbolId) =>
        Query<string>("SELECT substr(key, instr(key, '|') + 1) FROM symbols WHERE id = @i", ("@i", symbolId)).Single();

    /// <summary>Names of the symbols declared in a file, in source order.</summary>
    public List<string> NamesInFile(string fileName) =>
        Query<string>(
            "SELECT s.name FROM symbols s JOIN files f ON f.id = s.file_id WHERE f.path LIKE @p ORDER BY s.start_line, s.id",
            ("@p", "%/" + fileName));

    public (string Kind, int StartLine, string File, bool IsGenerated) Row(long symbolId)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT s.kind, s.start_line, f.path, s.is_generated FROM symbols s JOIN files f ON f.id = s.file_id WHERE s.id = @i";
        cmd.Parameters.AddWithValue("@i", symbolId);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3) != 0);
    }

    // ---- relationships -------------------------------------------------------------------------

    /// <summary>Documentation ids of the symbols with an edge of <paramref name="kind"/> pointing at <paramref name="toId"/>.</summary>
    public SortedSet<string> EdgeSources(long toId, string kind) => new(Query<string>(
        "SELECT substr(s.key, instr(s.key, '|') + 1) FROM edges e JOIN symbols s ON s.id = e.from_symbol_id WHERE e.to_symbol_id = @t AND e.kind = @k",
        ("@t", toId), ("@k", kind)), StringComparer.Ordinal);

    /// <summary>Documentation ids of the symbols that <paramref name="fromId"/> points at with an edge of <paramref name="kind"/>.</summary>
    public SortedSet<string> EdgeTargets(long fromId, string kind) => new(Query<string>(
        "SELECT substr(s.key, instr(s.key, '|') + 1) FROM edges e JOIN symbols s ON s.id = e.to_symbol_id WHERE e.from_symbol_id = @f AND e.kind = @k",
        ("@f", fromId), ("@k", kind)), StringComparer.Ordinal);

    /// <summary>Distinct (file name, line) pairs where the symbol is referenced, optionally of one kind.</summary>
    public SortedSet<string> RefLines(long symbolId, string? kind = null)
    {
        var sql = "SELECT DISTINCT f.path || ':' || r.line FROM symbol_references r JOIN files f ON f.id = r.file_id WHERE r.symbol_id = @s"
                  + (kind is null ? "" : " AND r.kind = @k");
        var args = kind is null ? new[] { ("@s", (object)symbolId) } : new[] { ("@s", (object)symbolId), ("@k", (object)kind) };
        var raw = Query<string>(sql, args);
        return new SortedSet<string>(raw.Select(x => x.Substring(x.LastIndexOf('/') + 1)), StringComparer.Ordinal);
    }

    /// <summary>The expected "File.cs:line" entry for a marker.</summary>
    public string At(string fileName, string marker) => $"{fileName}:{Line(fileName, marker)}";

    public long Scalar(string sql, params (string Name, object Value)[] args) => Query<long>(sql, args).Single();

    public List<string> Strings(string sql, params (string Name, object Value)[] args) => Query<string>(sql, args);

    // ---- plumbing ------------------------------------------------------------------------------

    private List<T> Query<T>(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        var list = new List<T>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((T)Convert.ChangeType(r.GetValue(0), typeof(T)));
        return list;
    }
}
