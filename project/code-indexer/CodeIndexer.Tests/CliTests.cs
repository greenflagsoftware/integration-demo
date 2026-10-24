using System.Diagnostics;
using System.Text.Json.Nodes;

namespace CodeIndexer.Tests;

/// <summary>Runs the built command-line tool as a process, the way a user or an agent does.</summary>
public class CliTests
{
    private static readonly string CliDll = FindCliDll();

    private static string FindCliDll()
    {
        // .../CodeIndexer.Tests/bin/<Config>/net10.0/  ->  .../CodeIndexer.Cli/bin/<Config>/net10.0/
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var tfm = Path.GetFileName(baseDir);
        var config = Path.GetFileName(Path.GetDirectoryName(baseDir)!);
        return Path.GetFullPath(Path.Combine(Fixtures.CodeIndexerRoot, "CodeIndexer.Cli", "bin", config, tfm, "CodeIndexer.Cli.dll"));
    }

    private sealed record Run(int ExitCode, string Out, string Err);

    private static Run Execute(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(CliDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(120_000), "the tool did not finish");
        return new Run(p.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void Help_NamesTheTool_ExplainsTheWorkflow_AndListsEveryCommand()
    {
        var help = Execute("--help").Out;

        Assert.Contains("codeindex [command] [options]", help);
        Assert.DoesNotContain("CodeIndexer.Cli", help);
        Assert.Contains("Workflow:", help);
        Assert.Contains("candidates", help);
        foreach (var command in new[] { "index", "find-symbol", "search", "at", "definition", "show", "outline", "refs", "callers", "callees", "implementations", "hierarchy", "mcp", "info" })
            Assert.Matches($@"(?m)^\s+{command}\b", help);
    }

    [Theory]
    [InlineData("callers")]
    [InlineData("refs")]
    [InlineData("index")]
    [InlineData("find-symbol")]
    public void CommandHelp_HasNoEmptyDefaultNoise(string command)
    {
        var help = Execute(command, "--help").Out;

        Assert.DoesNotContain("[]", help);
        Assert.DoesNotContain("[default: False]", help);
        Assert.Contains($"codeindex {command}", help);
    }

    [Fact]
    public void MissingRequiredArgument_IsAClearError_NotAnEmptyResult()
    {
        var run = Execute("find-symbol");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("Required argument missing", run.Out + run.Err);
    }

    [Fact]
    public void NoIndexDatabase_IsExitCode2_WithAMessageOnStderr()
    {
        var run = Execute("find-symbol", "Foo", "--db", Path.Combine(Path.GetTempPath(), "no-such-index.db"));

        Assert.Equal(2, run.ExitCode);
        Assert.Contains("codeindex index", run.Err);
        Assert.Equal("", run.Out.Trim());
    }

    [Fact]
    public void IndexThenQueryThenIndexAgain_WorksEndToEnd()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            var first = Execute("index", Fixtures.Semantics, "--db", db);
            Assert.Equal(0, first.ExitCode);
            Assert.Contains("rebuild the whole index (no existing index)", first.Out);
            Assert.Contains(".NET SDK:", first.Out);
            Assert.Matches(@"Indexed: 2 projects", first.Out);

            var query = Execute("callers", "Calculator.Add", "--db", db);
            Assert.Equal(0, query.ExitCode);
            var json = JsonNode.Parse(query.Out)!;
            Assert.Equal("callers", json["command"]!.GetValue<string>());
            Assert.Equal(3, json["candidates"]!.AsArray().Count);

            var byId = Execute("callers", "--id", json["candidates"]![0]!["id"]!.GetValue<long>().ToString(), "--db", db, "--format", "table");
            Assert.Equal(0, byId.ExitCode);
            Assert.StartsWith("callers - Semantics @ ", byId.Out);

            var second = Execute("index", Fixtures.Semantics, "--db", db);
            Assert.Equal(0, second.ExitCode);
            Assert.Contains("up to date", second.Out);
            Assert.DoesNotContain("Indexed:", second.Out);

            var forced = Execute("index", Fixtures.Semantics, "--db", db, "--full", "--dry-run");
            Assert.Contains("Would rebuild the whole index (--full was given)", forced.Out);
            Assert.Contains("Dry run", forced.Out);
        }
        finally { Fixtures.DeleteDb(db); }
    }

    [Fact]
    public void Mcp_SpeaksJsonRpcOverStdio_AndWritesNothingElseToStdout()
    {
        var db = Fixtures.TempDbPath();
        try
        {
            Assert.Equal(0, Execute("index", Fixtures.Semantics, "--db", db).ExitCode);

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (var a in new[] { CliDll, "mcp", "--db", db }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;

            JsonNode Ask(int id, string method, string? parameters = null)
            {
                p.StandardInput.WriteLine($$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}"{{(parameters is null ? "" : ",\"params\":" + parameters)}}}""");
                p.StandardInput.Flush();
                var line = p.StandardOutput.ReadLine();
                Assert.NotNull(line);
                return JsonNode.Parse(line)!;     // would throw if anything but a JSON message reached stdout
            }

            var init = Ask(1, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"t","version":"1"}}""");
            Assert.Equal("codeindex", init["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            p.StandardInput.WriteLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            var tools = Ask(2, "tools/list")["result"]!["tools"]!.AsArray();
            Assert.Equal(12, tools.Count);

            var call = Ask(3, "tools/call", """{"name":"find_symbol","arguments":{"name":"ShapeBase","kind":"class"}}""");
            var text = call["result"]!["content"]![0]!["text"]!.GetValue<string>();
            Assert.Equal("global::Semantics.Core.ShapeBase", JsonNode.Parse(text)!["data"]![0]!["full_name"]!.GetValue<string>());

            p.StandardInput.Close();
            Assert.True(p.WaitForExit(20_000), "the server did not stop when its input ended");
            Assert.Equal(0, p.ExitCode);
            Assert.Equal("", p.StandardError.ReadToEnd());
        }
        finally { Fixtures.DeleteDb(db); }
    }
}
