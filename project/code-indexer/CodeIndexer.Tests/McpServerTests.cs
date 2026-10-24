using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndexer.Core.Mcp;

namespace CodeIndexer.Tests;

public class McpServerTests : IClassFixture<SemanticsIndex>
{
    private readonly SemanticsIndex _ix;
    private readonly McpServer _server;
    private int _nextId = 1;

    public McpServerTests(SemanticsIndex ix)
    {
        _ix = ix;
        _server = new McpServer(defaultDb: ix.DbPath);
    }

    private JsonObject Request(string method, JsonObject? parameters = null, JsonNode? id = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id ?? _nextId++, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return _server.HandleLine(message.ToJsonString())!;
    }

    private JsonObject Call(string tool, JsonObject arguments)
    {
        var response = Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });
        return response["result"]!.AsObject();
    }

    private static JsonObject Json(JsonObject toolResult)
    {
        Assert.False(toolResult["isError"]!.GetValue<bool>(), toolResult["content"]![0]!["text"]!.GetValue<string>());
        return JsonNode.Parse(toolResult["content"]![0]!["text"]!.GetValue<string>())!.AsObject();
    }

    // ---- protocol ------------------------------------------------------------------------------

    [Fact]
    public void Initialize_ReturnsTheNegotiatedVersion_Capabilities_AndInstructions()
    {
        var r = Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
        });

        Assert.Equal("2.0", r["jsonrpc"]!.GetValue<string>());
        var result = r["result"]!;
        Assert.Equal("2025-03-26", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("codeindex", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Matches(@"^\d+\.\d+\.\d+$", result["serverInfo"]!["version"]!.GetValue<string>());
        Assert.Contains("instead of grep", result["instructions"]!.GetValue<string>());
    }

    [Fact]
    public void Initialize_WithAnUnknownVersion_OffersTheLatestOne()
    {
        var r = Request("initialize", new JsonObject { ["protocolVersion"] = "1999-01-01" });

        Assert.Equal("2025-06-18", r["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Notifications_GetNoResponse()
    {
        var line = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }.ToJsonString();

        Assert.Null(_server.HandleLine(line));
    }

    [Fact]
    public void Ping_ReturnsAnEmptyResult() =>
        Assert.Empty(Request("ping")["result"]!.AsObject());

    [Fact]
    public void ResponseIds_MatchTheRequests_WhateverTheirType()
    {
        Assert.Equal(42, Request("ping", id: 42)["id"]!.GetValue<int>());
        Assert.Equal("abc", Request("ping", id: "abc")["id"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownMethod_IsAMethodNotFoundError()
    {
        var r = Request("resources/list");

        Assert.Equal(-32601, r["error"]!["code"]!.GetValue<int>());
        Assert.Null(r["result"]);
    }

    [Fact]
    public void InvalidJson_IsAParseError_WithANullId()
    {
        var r = _server.HandleLine("{ this is not json")!;

        Assert.Equal(-32700, r["error"]!["code"]!.GetValue<int>());
        Assert.Null(r["id"]);
    }

    [Fact]
    public void UnknownTool_IsAProtocolError_ListingWhatExists()
    {
        var r = Request("tools/call", new JsonObject { ["name"] = "grep", ["arguments"] = new JsonObject() });

        Assert.Equal(-32602, r["error"]!["code"]!.GetValue<int>());
        Assert.Contains("find_symbol", r["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunAsync_ReadsLines_WritesOneJsonLinePerRequest_AndStopsAtEndOfInput()
    {
        var input = string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            "",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"find_symbol","arguments":{"name":"ShapeBase"}}}""") + "\n";
        var output = new StringWriter();

        await _server.RunAsync(new StringReader(input), output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, l => Assert.NotNull(JsonNode.Parse(l)));           // stdout carries only protocol messages
        Assert.Equal(new[] { 1, 2, 3 }, lines.Select(l => JsonNode.Parse(l)!["id"]!.GetValue<int>()));
    }

    // ---- tools ---------------------------------------------------------------------------------

    [Fact]
    public void ToolList_DescribesEveryTool_WithAUsefulSchema()
    {
        var tools = Request("tools/list")["result"]!["tools"]!.AsArray();

        var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(
            new[] { "find_symbol", "search", "definition", "show", "outline", "at", "refs", "callers", "callees", "implementations", "hierarchy", "index_info" },
            names);
        foreach (var tool in tools)
        {
            Assert.True(tool!["description"]!.GetValue<string>().Length > 40, $"{tool["name"]} needs a real description");
            var schema = tool["inputSchema"]!;
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.NotNull(schema["properties"]);
            // Every property carries a description, so an agent knows what to pass.
            foreach (var (propName, prop) in schema["properties"]!.AsObject())
                Assert.True(prop!["description"] is not null, $"{tool["name"]}.{propName} has no description");
        }
    }

    [Fact]
    public void ToolList_RequiredArguments_AreDeclared()
    {
        var tools = Request("tools/list")["result"]!["tools"]!.AsArray().ToDictionary(t => t!["name"]!.GetValue<string>());

        Assert.Equal(new[] { "name" }, tools["find_symbol"]!["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(new[] { "file", "line" }, tools["at"]!["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Empty(tools["callers"]!["inputSchema"]!["required"]!.AsArray());
    }

    [Fact]
    public void FindSymbol_ReturnsTheSameJsonAsTheCommandLine()
    {
        var json = Json(Call("find_symbol", new JsonObject { ["name"] = "ShapeBase", ["kind"] = "class" }));

        Assert.Equal("find-symbol", json["command"]!.GetValue<string>());
        Assert.Equal("global::Semantics.Core.ShapeBase", json["data"]![0]!["full_name"]!.GetValue<string>());
        Assert.False(json["stale"]!.GetValue<bool>());
    }

    [Fact]
    public void AmbiguousSelector_ReturnsCandidates_AndTheIdThenResolves()
    {
        var ambiguous = Json(Call("callers", new JsonObject { ["symbol"] = "Calculator.Add" }));
        Assert.Null(ambiguous["target"]);
        var candidates = ambiguous["candidates"]!.AsArray();
        Assert.Equal(3, candidates.Count);

        var addInt = candidates.Single(c => c!["signature"]!.GetValue<string>().Contains("int a, int b"))!;
        var resolved = Json(Call("callers", new JsonObject { ["id"] = addInt["id"]!.GetValue<long>() }));

        Assert.Equal(new[] { "Run", "Total", "Twice", "UseLambda" },
            resolved["data"]!.AsArray().Select(n => n!["symbol"]!["name"]!.GetValue<string>()).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Arguments_MayBeStrings_BecauseSomeClientsSendThemThatWay()
    {
        var json = Json(Call("callers", new JsonObject { ["symbol"] = "Circle.Area", ["through_dispatch"] = "true", ["depth"] = "3" }));

        Assert.Contains(json["data"]!.AsArray(), n => n!["symbol"]!["name"]!.GetValue<string>() == "SumAreas");
    }

    [Fact]
    public void ThroughDispatch_IsReachableFromTheToolSchema()
    {
        var direct = Json(Call("callers", new JsonObject { ["symbol"] = "Circle.Area" }));
        var dispatched = Json(Call("callers", new JsonObject { ["symbol"] = "Circle.Area", ["through_dispatch"] = true, ["depth"] = 0 }));

        Assert.Equal(1, direct["total_count"]!.GetValue<int>());
        Assert.Equal(4, dispatched["total_count"]!.GetValue<int>());
    }

    [Fact]
    public void At_UsesFileLineAndColumn()
    {
        var line = _ix.Line("Consumer.cs", "app-add-int");
        var column = _ix.Column("Consumer.cs", "app-add-int", "Add(");

        var json = Json(Call("at", new JsonObject { ["file"] = "Consumer.cs", ["line"] = line, ["column"] = column }));

        Assert.Equal("reference", json["data"]!["match"]!.GetValue<string>());
        Assert.Equal("Add", json["target"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void EveryTool_RunsAgainstTheIndex_WithoutError()
    {
        var calls = new (string Tool, JsonObject Args)[]
        {
            ("find_symbol", new() { ["name"] = "Calc" }),
            ("search", new() { ["text"] = "calculator" }),
            ("definition", new() { ["symbol"] = "Widget" }),
            ("show", new() { ["symbol"] = "ShapeBase", ["source"] = true }),
            ("outline", new() { ["symbol"] = "Calculator.cs" }),
            ("refs", new() { ["symbol"] = "Account.Balance", ["ref_kinds"] = "write", ["group_by"] = "member", ["context"] = true }),
            ("callees", new() { ["symbol"] = "SumAreas", ["through_dispatch"] = true }),
            ("implementations", new() { ["symbol"] = "IShape", ["transitive"] = true }),
            ("hierarchy", new() { ["symbol"] = "Circle" }),
            ("index_info", new()),
        };

        foreach (var (tool, args) in calls)
            Assert.Equal(tool == "index_info" ? "index-info" : tool.Replace('_', '-'), Json(Call(tool, args))["command"]!.GetValue<string>());
    }

    [Fact]
    public void IndexInfo_SaysWhatIsIndexedAndWhetherToTrustIt()
    {
        var data = Json(Call("index_info", new JsonObject()))["data"]!;

        Assert.Equal(new[] { "Semantics.App", "Semantics.Core" }, data["projects"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.True(data["symbols"]!.GetValue<int>() > 50);
        Assert.True(data["references"]!.GetValue<int>() > 50);
        Assert.NotNull(data["tool_version"]);
    }

    // ---- errors a tool call can have -----------------------------------------------------------

    [Fact]
    public void MissingRequiredArgument_IsAToolError_NotAProtocolError()
    {
        var result = Call("find_symbol", new JsonObject());

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("'name' is required", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void NonNumericId_IsAToolError()
    {
        var result = Call("definition", new JsonObject { ["id"] = "seven" });

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("must be an integer", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void NoSuchSymbol_IsANormalResultWithAWarning()
    {
        var json = Json(Call("definition", new JsonObject { ["symbol"] = "NoSuchThing_XYZZY" }));

        Assert.Contains("not found", json["warnings"]![0]!.GetValue<string>());
    }

    [Fact]
    public void MissingIndex_TellsTheAgentHowToCreateOne()
    {
        var server = new McpServer(defaultDb: Path.Combine(Path.GetTempPath(), "does-not-exist.db"));
        var line = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "find_symbol", ["arguments"] = new JsonObject { ["name"] = "x" } },
        }.ToJsonString();

        var result = server.HandleLine(line)!["result"]!;

        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("codeindex index", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void AnIndexFromAnOlderVersion_IsAToolErrorThatSaysToReindex()
    {
        var copy = Fixtures.TempDbPath();
        try
        {
            File.Copy(_ix.DbPath, copy);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE meta SET value = '5' WHERE key = 'schema_version'";
                cmd.ExecuteNonQuery();
            }
            var line = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "find_symbol", ["arguments"] = new JsonObject { ["name"] = "x" } },
            }.ToJsonString();

            var result = new McpServer(defaultDb: copy).HandleLine(line)!["result"]!;

            Assert.True(result["isError"]!.GetValue<bool>());
            Assert.Contains("older version of the tool", result["content"]![0]!["text"]!.GetValue<string>());
        }
        finally { Fixtures.DeleteDb(copy); }
    }

    [Fact]
    public void AStaleIndex_IsReportedInTheToolResult()
    {
        var copy = Fixtures.TempDbPath();
        try
        {
            File.Copy(_ix.DbPath, copy);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={copy};Pooling=False"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE inputs SET hash = 'deadbeef', size_bytes = size_bytes + 1 WHERE kind = 'source' AND path LIKE '%/Calculator.cs'";
                cmd.ExecuteNonQuery();
            }
            var server = new McpServer(defaultDb: copy);
            var line = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = "find_symbol", ["arguments"] = new JsonObject { ["name"] = "Calculator" } },
            }.ToJsonString();

            var text = server.HandleLine(line)!["result"]!["content"]![0]!["text"]!.GetValue<string>();
            var json = JsonNode.Parse(text)!;

            Assert.True(json["stale"]!.GetValue<bool>());
            Assert.StartsWith("Index is stale", json["warnings"]![0]!.GetValue<string>());
        }
        finally { Fixtures.DeleteDb(copy); }
    }
}

public class ToolVersionTests
{
    [Fact]
    public void ThePackageVersion_MatchesTheIndexFormatVersion()
    {
        // The tool version is stamped into every index; it comes from <Version> in Directory.Build.props.
        Assert.Equal(CodeIndexer.Core.Indexing.IndexEngine.ToolVersion, McpServer.Version);
    }
}
