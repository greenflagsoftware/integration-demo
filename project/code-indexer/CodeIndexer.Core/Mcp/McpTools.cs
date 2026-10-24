using System.Text.Json.Nodes;
using CodeIndexer.Core.Queries;

namespace CodeIndexer.Core.Mcp;

/// <summary>A tool the server offers: its name, what it is for, the shape of its arguments, and how to run it.</summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, SymbolQueries, IQueryResponse> Run)
{
    public JsonObject Describe() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
    };
}

/// <summary>Argument readers. They throw <see cref="ArgumentException"/>, which the server reports as a tool error.</summary>
internal static class Args
{
    public static string? String(JsonObject a, string name)
    {
        if (a[name] is not JsonNode node) return null;
        var text = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    public static long? Long(JsonObject a, string name)
    {
        var text = String(a, name);
        if (text is null) return null;
        return long.TryParse(text, out var n) ? n : throw new ArgumentException($"'{name}' must be an integer, got '{text}'.");
    }

    public static int Int(JsonObject a, string name, int fallback) =>
        Long(a, name) is { } n ? (int)Math.Clamp(n, int.MinValue, int.MaxValue) : fallback;

    public static bool Bool(JsonObject a, string name) =>
        String(a, name) is { } t && (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1");
}

internal static class McpTools
{
    public const string Instructions =
        "Code index for a .NET solution (Roslyn-built, read-only). Use these tools instead of grep when you need to know where a C# " +
        "symbol is defined, what uses it, what calls it, what implements it, or what a type derives from: they resolve names " +
        "semantically (overloads, partial classes, interface calls), so results are exact where text search guesses. " +
        "Typical flow: find_symbol to locate something and get its id; then definition, refs, callers, callees, implementations, " +
        "hierarchy, or outline with that id (or with a name, Type.Member, documentation id, or file:line:col). " +
        "If a name matches several symbols the result has no target and lists 'candidates': pick one and repeat with its id or key. " +
        "Every result says whether the index is stale (files changed since it was built); if so, tell the user to run " +
        "'codeindex index <solution>' and treat results as possibly out of date. Generated code and library (external) symbols " +
        "are hidden unless asked for.";

    private static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };
    private static JsonObject Enum(string description, params string[] values) => new()
    {
        ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
    };

    private static JsonObject Schema(JsonObject properties, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = properties,
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        ["additionalProperties"] = false,
    };

    private static JsonObject Merge(JsonObject first, JsonObject second)
    {
        var result = (JsonObject)first.DeepClone();
        foreach (var (k, v) in second) result[k] = v?.DeepClone();
        return result;
    }

    private static JsonObject SolutionProp() => new()
    {
        ["solution"] = Prop("string", "Name of the indexed solution to query, when several are indexed (see index_info). Optional."),
    };

    private static JsonObject SelectorProps() => Merge(new JsonObject
    {
        ["symbol"] = Prop("string", "The symbol: a name (Add), Type.Member (Calculator.Add), a documentation id (M:Ns.Type.Method(System.Int32)), or a position file:line or file:line:col (1-based; the symbol used there)."),
        ["id"] = Prop("integer", "Symbol id from a previous result (most precise; valid until the next index run that redoes its project)."),
        ["key"] = Prop("string", "Symbol key or documentation id from a previous result (stable across re-indexing)."),
        ["kind"] = Prop("string", "Only match this kind: class, struct, interface, enum, delegate, method, property, field, event."),
        ["include_generated"] = Prop("boolean", "Also consider generated code (hidden by default)."),
    }, SolutionProp());

    private static SymbolSelector Selector(JsonObject a) => new(
        Id: Args.Long(a, "id"), Key: Args.String(a, "key"), Text: Args.String(a, "symbol"),
        Kind: Args.String(a, "kind"), IncludeGenerated: Args.Bool(a, "include_generated"));

    private static string Required(JsonObject a, string name) =>
        Args.String(a, name) ?? throw new ArgumentException($"'{name}' is required.");

    public static IReadOnlyList<McpTool> Build() => new List<McpTool>
    {
        new("find_symbol",
            "Find symbols by name: exact matches first, then prefix, then substring. Use this first to locate a class, method, or property " +
            "and get its id. Hides generated code and library symbols unless asked. Returns id, key, kind, signature, file and lines.",
            Schema(Merge(new JsonObject
            {
                ["name"] = Prop("string", "Name, prefix, or part of a name to look for."),
                ["kind"] = Prop("string", "Only this kind: class, struct, interface, enum, delegate, method, property, field, event."),
                ["project"] = Prop("string", "Only symbols in projects whose name or path contains this."),
                ["include_generated"] = Prop("boolean", "Include generated code."),
                ["include_external"] = Prop("boolean", "Include symbols declared in libraries (framework, NuGet)."),
                ["limit"] = Prop("integer", "Maximum results (default 20)."),
                ["offset"] = Prop("integer", "Results to skip, for paging."),
            }, SolutionProp()), "name"),
            (a, q) => q.FindSymbol(Required(a, "name"), new FindOptions(
                Args.String(a, "kind"), Args.String(a, "project"), Args.Bool(a, "include_generated"), Args.Bool(a, "include_external"),
                Args.Int(a, "limit", 20), Args.Int(a, "offset", 0)))),

        new("search",
            "Full-text search over symbol names, signatures, and XML documentation (FTS5 syntax: words, \"phrases\", prefix*, AND/OR). " +
            "Use for concepts you cannot name ('retry backoff', 'signed url').",
            Schema(Merge(new JsonObject
            {
                ["text"] = Prop("string", "Search text."),
                ["include_generated"] = Prop("boolean", "Include generated code."),
                ["include_external"] = Prop("boolean", "Include library symbols."),
                ["limit"] = Prop("integer", "Maximum results (default 20)."),
            }, SolutionProp()), "text"),
            (a, q) => q.Search(Required(a, "text"), new SearchOptions(
                Args.Bool(a, "include_generated"), Args.Bool(a, "include_external"), Args.Int(a, "limit", 20)))),

        new("definition",
            "Where a symbol is declared: file and line range of every declaration (a partial class has one per part). " +
            "Library symbols have no location.",
            Schema(SelectorProps()),
            (a, q) => q.Definition(Selector(a))),

        new("show",
            "A symbol's details (signature, accessibility, documentation) and, with source=true, the source of its declaration.",
            Schema(Merge(SelectorProps(), new JsonObject
            {
                ["source"] = Prop("boolean", "Include the declaration's source code."),
                ["max_lines"] = Prop("integer", "Maximum source lines (default 200)."),
            })),
            (a, q) => q.Show(Selector(a), Args.Bool(a, "source"), Args.Int(a, "max_lines", 200))),

        new("outline",
            "Structure of a file (pass a path such as src/Foo.cs) or of a type (pass its name): symbols nested by container, with line numbers. " +
            "Cheaper than reading the file when you only need to know what is in it.",
            Schema(Merge(SelectorProps(), new JsonObject())),
            (a, q) => q.Outline(Selector(a))),

        new("at",
            "The symbol at a source position, for identifying an identifier you just read. On a use of a symbol returns that symbol " +
            "(with how it is used and which member contains the use); elsewhere the declaration containing the position. " +
            "Without a column, a line with several uses returns candidates.",
            Schema(Merge(new JsonObject
            {
                ["file"] = Prop("string", "Path of the file (any unique suffix, for example Models/Order.cs)."),
                ["line"] = Prop("integer", "Line number, 1-based."),
                ["column"] = Prop("integer", "Column, 1-based. Optional but recommended."),
            }, SolutionProp()), "file", "line"),
            (a, q) => q.At(Required(a, "file"), Args.Int(a, "line", 0), Args.Long(a, "column") is { } c ? (int)c : null)),

        new("refs",
            "Every use of a symbol, with the kind of use (call, read, write, type-use, create, inherit, implement, attribute, typeof, nameof) " +
            "and the member that contains it. The semantic replacement for grepping a name: it does not confuse overloads or same-named " +
            "members, and it finds uses that text search misses. References in generated files are hidden unless include_generated.",
            Schema(Merge(SelectorProps(), new JsonObject
            {
                ["ref_kinds"] = Prop("string", "Only these kinds, comma separated, for example 'call,create' or 'write'."),
                ["file"] = Prop("string", "Only references in files whose path contains this."),
                ["project"] = Prop("string", "Only references in projects whose name or path contains this."),
                ["context"] = Prop("boolean", "Include the source line of each reference."),
                ["group_by"] = Enum("Group the results.", "none", "file", "member", "kind"),
                ["limit"] = Prop("integer", "Maximum references (default 100)."),
                ["offset"] = Prop("integer", "References to skip, for paging."),
            })),
            (a, q) => q.Refs(Selector(a), new RefsOptions(
                Kinds: Args.String(a, "ref_kinds")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                File: Args.String(a, "file"), Project: Args.String(a, "project"),
                IncludeGenerated: Args.Bool(a, "include_generated"), Context: Args.Bool(a, "context"),
                GroupBy: Args.String(a, "group_by") ?? "none", Limit: Args.Int(a, "limit", 100), Offset: Args.Int(a, "offset", 0)))),

        new("callers",
            "Methods that call this one (incoming call hierarchy), with the call sites. depth 1 is direct callers; 0 follows the chain " +
            "to the top. through_dispatch=true also counts calls made through an interface or base member this method implements or overrides " +
            "(otherwise a call to IShape.Area is not a call to Circle.Area).",
            Schema(Merge(SelectorProps(), CallProps())),
            (a, q) => q.Callers(Selector(a), CallOptionsFrom(a))),

        new("callees",
            "Methods this one calls (outgoing call hierarchy), with the call sites. through_dispatch=true also lists the implementations " +
            "of any interface or abstract member that is called. Library calls are hidden unless include_external.",
            Schema(Merge(SelectorProps(), CallProps())),
            (a, q) => q.Callees(Selector(a), CallOptionsFrom(a))),

        new("implementations",
            "For an interface or class: the types that implement or derive from it. For a method, property, or event: the members that implement " +
            "or override it. Direct by default; transitive=true follows the chain.",
            Schema(Merge(SelectorProps(), new JsonObject
            {
                ["transitive"] = Prop("boolean", "Follow implementers of implementers and overrides of overrides."),
                ["limit"] = Prop("integer", "Maximum results (default 50)."),
            })),
            (a, q) => q.Implementations(Selector(a), Args.Bool(a, "transitive"), Args.Int(a, "limit", 50))),

        new("hierarchy",
            "Type hierarchy: the base class and interfaces of a type (supertypes) and the types derived from or implementing it (subtypes).",
            Schema(Merge(SelectorProps(), new JsonObject
            {
                ["direction"] = Enum("Which side to show.", "both", "up", "down"),
                ["depth"] = Prop("integer", "Maximum depth (default 3)."),
            })),
            (a, q) => q.Hierarchy(Selector(a), Args.String(a, "direction") ?? "both", Args.Int(a, "depth", 3))),

        new("index_info",
            "Which solutions are indexed, whether the index is stale, and how big it is. Call this first if unsure what is available.",
            Schema(SolutionProp()),
            (a, q) => q.Info()),
    };

    private static JsonObject CallProps() => new()
    {
        ["depth"] = Prop("integer", "How many calls deep (default 1; 0 = unlimited)."),
        ["limit"] = Prop("integer", "Maximum methods returned (default 50)."),
        ["through_dispatch"] = Prop("boolean", "Follow interface/abstract/base dispatch."),
        ["include_creates"] = Prop("boolean", "Also follow object creation (new)."),
        ["include_external"] = Prop("boolean", "Include library methods."),
        ["exclude_generated"] = Prop("boolean", "Leave out members declared in generated code."),
    };

    private static CallOptions CallOptionsFrom(JsonObject a) => new(
        Args.Int(a, "depth", 1), Args.Int(a, "limit", 50), Args.Bool(a, "through_dispatch"), Args.Bool(a, "include_creates"),
        Args.Bool(a, "include_external"), Args.Bool(a, "exclude_generated"));
}
