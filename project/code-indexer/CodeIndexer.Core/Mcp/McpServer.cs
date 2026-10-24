using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndexer.Core.Queries;
using CodeIndexer.Core.Storage;

namespace CodeIndexer.Core.Mcp;

/// <summary>
/// A read-only Model Context Protocol server over stdio (one JSON-RPC message per line). It exposes the query commands as
/// tools so an agent keeps one warm process instead of paying process start-up on every lookup. It never writes the index:
/// to refresh it, run <c>codeindex index</c>; a stale index says so in every response.
/// </summary>
public sealed class McpServer
{
    public const string ServerName = "codeindex";

    /// <summary>Protocol revisions this server speaks; the client's is echoed back when it is one of these.</summary>
    private static readonly string[] SupportedVersions = { "2025-06-18", "2025-03-26", "2024-11-05" };

    private readonly string? _defaultDb;
    private readonly string? _defaultSolution;
    private readonly IReadOnlyList<McpTool> _tools = McpTools.Build();

    /// <param name="defaultDb">Index database used when a tool call names none (and no solution).</param>
    /// <param name="defaultSolution">Indexed solution used when a tool call names none.</param>
    public McpServer(string? defaultDb = null, string? defaultSolution = null)
    {
        _defaultDb = string.IsNullOrEmpty(defaultDb) ? null : defaultDb;
        _defaultSolution = string.IsNullOrEmpty(defaultSolution) ? null : defaultSolution;
    }

    public static string Version =>
        (typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>Serves requests until the input ends. Only protocol messages are written to <paramref name="output"/>.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellation = default)
    {
        string? line;
        while (!cancellation.IsCancellationRequested && (line = await input.ReadLineAsync(cancellation)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var response = HandleLine(line);
            if (response is not null)
            {
                await output.WriteLineAsync(response.ToJsonString());
                await output.FlushAsync(cancellation);
            }
        }
    }

    /// <summary>Handles one incoming line; returns the response to send, or null for a notification.</summary>
    public JsonObject? HandleLine(string line)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error: the message is not valid JSON."); }

        if (parsed is not JsonObject message)
            return Error(null, -32600, "Invalid request: expected a JSON object.");

        var id = message["id"];
        var method = message["method"]?.GetValue<string>();
        if (method is null) return null;     // a response from the client; nothing to do
        if (id is null) return null;         // a notification (for example notifications/initialized)

        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(message["params"] as JsonObject)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = new JsonArray(_tools.Select(t => (JsonNode)t.Describe()).ToArray()) }),
                "tools/call" => CallTool(id, message["params"] as JsonObject),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex)
        {
            return Error(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private JsonObject Initialize(JsonObject? parameters)
    {
        var requested = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = requested is not null && SupportedVersions.Contains(requested) ? requested : SupportedVersions[0],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["version"] = Version },
            ["instructions"] = McpTools.Instructions,
        };
    }

    private JsonObject CallTool(JsonNode id, JsonObject? parameters)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return Error(id, -32602, $"Unknown tool: {name}. Available: {string.Join(", ", _tools.Select(t => t.Name))}");

        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();

        string? db;
        string? problem = null;
        var solution = Args.String(arguments, "solution");
        db = solution is null && _defaultDb is not null
            ? (File.Exists(_defaultDb) ? _defaultDb : null)
            : IndexLocator.ResolveDbPath(null, solution ?? _defaultSolution, out problem);
        if (db is null)
            return Result(id, ToolResult(problem ?? $"No index database at '{_defaultDb}'. Run 'codeindex index <solution>' first.", isError: true));

        try
        {
            using var queries = new SymbolQueries(db);   // opened per call so staleness is always current
            return Result(id, ToolResult(tool.Run(arguments, queries).ToJson(), isError: false));
        }
        catch (ArgumentException ex)
        {
            return Result(id, ToolResult(ex.Message, isError: true));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return Result(id, ToolResult($"Query failed: {ex.Message}. If the index was built by an older version, re-run 'codeindex index'.", isError: true));
        }
    }

    private static JsonObject ToolResult(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonNode result) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result,
    };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
