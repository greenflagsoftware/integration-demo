using System.Diagnostics;
using CodeIndexer.Core.Indexing;
using CodeIndexer.Core.Loading;
using Microsoft.Data.Sqlite;
using Xunit.Sdk;

namespace CodeIndexer.Tests;

/// <summary>
/// An index reduced to what it means, for comparing two indexes of the same files: symbols are named by key rather than row
/// id, and times, row ids, and file modification times are left out. An incrementally updated index must dump exactly like a
/// clean rebuild.
/// </summary>
public sealed class IndexDump
{
    private readonly Dictionary<string, List<string>> _sections = new();

    public IReadOnlyDictionary<string, List<string>> Sections => _sections;

    private static readonly (string Name, string Sql)[] Queries =
    {
        ("symbols", @"SELECT s.key, s.kind, s.name, s.full_name, COALESCE(c.key, ''), COALESCE(s.signature, ''), COALESCE(s.accessibility, ''),
                             COALESCE(s.doc_summary, ''), s.is_generated, s.is_external, COALESCE(s.assembly, ''), COALESCE(f.path, ''),
                             COALESCE(s.start_line, 0), COALESCE(s.end_line, 0)
                      FROM symbols s LEFT JOIN symbols c ON c.id = s.container_id LEFT JOIN files f ON f.id = s.file_id"),
        ("locations", @"SELECT s.key, f.path, l.start_line, l.start_col, l.end_line, l.end_col, l.is_primary
                        FROM symbol_locations l JOIN symbols s ON s.id = l.symbol_id JOIN files f ON f.id = l.file_id"),
        ("files", @"SELECT p.path, f.path, f.content_hash, f.line_count, f.is_generated FROM files f JOIN projects p ON p.id = f.project_id"),
        ("projects", "SELECT path, name, target_frameworks, indexed_tfm, language, is_external FROM projects"),
        ("references", @"SELECT s.key, f.path, r.line, r.column, r.length, r.kind, COALESCE(e.key, ''), COALESCE(r.reference_name, '')
                         FROM symbol_references r JOIN symbols s ON s.id = r.symbol_id JOIN files f ON f.id = r.file_id
                         LEFT JOIN symbols e ON e.id = r.enclosing_symbol_id"),
        ("edges", @"SELECT a.key, b.key, e.kind FROM edges e JOIN symbols a ON a.id = e.from_symbol_id JOIN symbols b ON b.id = e.to_symbol_id"),
        ("diagnostics", "SELECT d.severity, d.message, COALESCE(p.path, '') FROM diagnostics d LEFT JOIN projects p ON p.id = d.project_id"),
        ("inputs", "SELECT path, kind, owner, indexed, hash FROM inputs"),
        ("solution_projects", @"SELECT s.name, p.path FROM solution_projects sp JOIN solutions s ON s.id = sp.solution_id JOIN projects p ON p.id = sp.project_id"),
        ("solutions", "SELECT name, path FROM solutions"),
        ("meta", "SELECT key, value FROM meta WHERE key IN ('tool_version', 'schema_version', 'repo_root', 'solution_path', 'filter', 'sdk_version')"),
        ("fts:twice", "SELECT s.key FROM symbols_fts f JOIN symbols s ON s.id = f.rowid WHERE symbols_fts MATCH 'twice'"),
        ("fts:service", "SELECT s.key FROM symbols_fts f JOIN symbols s ON s.id = f.rowid WHERE symbols_fts MATCH 'service OR solo'"),
    };

    public static IndexDump Capture(string dbPath)
    {
        var dump = new IndexDump();
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False;Mode=ReadOnly");
        conn.Open();
        foreach (var (name, sql) in Queries)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var rows = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i)))));
            rows.Sort(StringComparer.Ordinal);
            dump._sections[name] = rows;
        }
        return dump;
    }

    /// <summary>Throws with a readable account of every section that differs.</summary>
    public static void AssertSame(string step, IndexDump expected, IndexDump actual)
    {
        var problems = new List<string>();
        foreach (var (name, expectedRows) in expected._sections)
        {
            var actualRows = actual._sections[name];
            if (expectedRows.SequenceEqual(actualRows)) continue;

            var missing = expectedRows.Except(actualRows).Take(5).Select(x => "      missing:  " + Truncate(x));
            var extra = actualRows.Except(expectedRows).Take(5).Select(x => "      extra:    " + Truncate(x));
            problems.Add($"  [{name}] {expectedRows.Count} rows expected, {actualRows.Count} found\n" + string.Join("\n", missing.Concat(extra)));
        }
        if (problems.Count > 0)
            throw new XunitException($"Step '{step}': the incrementally updated index differs from a clean rebuild.\n" + string.Join("\n", problems));
    }

    private static string Truncate(string s) => s.Length <= 220 ? s : s[..220] + "...";
}

/// <summary>
/// A throwaway copy of the Incremental fixture on disk (restored), plus helpers to edit it and to index it incrementally or
/// from scratch.
/// </summary>
public sealed class IncrementalWorkspace : IDisposable
{
    public string Root { get; }
    public string Solution => Path.Combine(Root, "Inc.sln");
    public string Db => Path.Combine(Root, ".index", "inc.db");

    public IncrementalWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"codeindex_inc_{Guid.NewGuid():N}");
        Copy(Path.Combine(Fixtures.CodeIndexerRoot, "tests", "Fixtures", "Incremental"), Root);
        Directory.CreateDirectory(Path.GetDirectoryName(Db)!);
        Dotnet("restore Inc.sln --disable-build-servers");
    }

    private static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj") continue;
            Copy(dir, Path.Combine(destination, name));
        }
    }

    /// <summary>
    /// Runs the dotnet CLI in the workspace. Build servers and MSBuild node reuse are disabled, and the call waits for the
    /// process itself rather than for end-of-output: a lingering MSBuild node inherits the redirected pipes and would
    /// otherwise keep the read open forever.
    /// </summary>
    public int Dotnet(string arguments)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(180_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"dotnet {arguments} did not finish in 3 minutes. Output: {output}");
        }
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"dotnet {arguments} failed ({p.ExitCode}). Output: {output}");
        return p.ExitCode;
    }

    public string Path_(string relative) => Path.Combine(Root, relative);
    public void Write(string relative, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path_(relative))!);
        File.WriteAllText(Path_(relative), text);
    }
    public void Edit(string relative, Func<string, string> change) => File.WriteAllText(Path_(relative), change(File.ReadAllText(Path_(relative))));
    public void Delete(string relative) => File.Delete(Path_(relative));
    public void Rename(string from, string to) => File.Move(Path_(from), Path_(to));

    public Task<IndexRunResult> Index(ProjectFilter? filter = null, bool full = false, bool dryRun = false) =>
        IndexRunner.RunAsync(new IndexRunOptions(Solution, Db, Root, filter ?? new ProjectFilter(), full, dryRun));

    /// <summary>What a clean rebuild of the files as they are now produces.</summary>
    public async Task<IndexDump> CleanDump(ProjectFilter? filter = null)
    {
        var clean = Path.Combine(Root, ".index", "clean.db");
        foreach (var f in new[] { clean, clean + "-wal", clean + "-shm" }) if (File.Exists(f)) File.Delete(f);
        var run = await IndexRunner.RunAsync(new IndexRunOptions(Solution, clean, Root, filter ?? new ProjectFilter(), Full: true));
        Assert.Equal(IndexMode.Full, run.Mode);
        return IndexDump.Capture(clean);
    }

    public IndexDump Dump() => IndexDump.Capture(Db);

    public void Execute(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={Db};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, long> SymbolIds(string keyPrefix)
    {
        using var conn = new SqliteConnection($"Data Source={Db};Pooling=False;Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, id FROM symbols WHERE key LIKE @p";
        cmd.Parameters.AddWithValue("@p", keyPrefix + "%");
        using var r = cmd.ExecuteReader();
        var map = new Dictionary<string, long>();
        while (r.Read()) map[r.GetString(0)] = r.GetInt64(1);
        return map;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(Root, recursive: true); return; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}
