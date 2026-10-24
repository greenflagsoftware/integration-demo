using System.Text;
using System.Text.Json;

namespace CodeIndexer.Core.Queries;

/// <summary>
/// Renders a query response as plain text for humans (<c>--format table</c>). It works from the response's JSON, so
/// every command is covered without a per-command renderer: lists of objects become tables of their scalar fields,
/// objects become key/value lines, and nested objects show their name.
/// </summary>
public static class TableFormatter
{
    private const int MaxCell = 56;

    // Fields that make a table unreadable and are available in JSON.
    private static readonly HashSet<string> HiddenColumns = new() { "key", "doc_summary", "source_code", "accessibility", "is_external", "is_generated" };

    public static string Render(JsonElement response)
    {
        var sb = new StringBuilder();

        string Str(string name) => response.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var commit = Str("indexed_commit");
        sb.Append(Str("command")).Append(" - ").Append(Str("solution_name"))
          .Append(" @ ").AppendLine(commit.Length > 8 ? commit[..8] : commit);
        if (response.TryGetProperty("stale", out var stale) && stale.ValueKind == JsonValueKind.True)
            sb.AppendLine("STALE: files changed since indexing. Re-run 'codeindex index'.");

        if (response.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array)
            foreach (var w in warnings.EnumerateArray())
                sb.Append("warning: ").AppendLine(w.GetString());

        if (response.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object)
            sb.Append("target: ").AppendLine(Describe(target));

        if (response.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("candidates (re-run with --id or --key):");
            AppendTable(sb, candidates, 1);
        }

        bool ambiguous = response.TryGetProperty("candidates", out var cands) && cands.ValueKind == JsonValueKind.Array && cands.GetArrayLength() > 0;
        if (!ambiguous && response.TryGetProperty("data", out var data))
            AppendValue(sb, data, 0);

        if (response.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            sb.AppendLine("(truncated: raise --limit or use --offset)");

        return sb.ToString().TrimEnd();
    }

    private static void AppendValue(StringBuilder sb, JsonElement value, int indent)
    {
        var pad = new string(' ', indent * 2);
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                if (value.GetArrayLength() == 0) { sb.Append(pad).AppendLine("(none)"); break; }
                if (value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object))
                {
                    if (value.EnumerateArray().Any(e => e.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0))
                        AppendTree(sb, value, indent);
                    else
                        AppendTable(sb, value, indent);
                }
                else
                    foreach (var e in value.EnumerateArray()) sb.Append(pad).AppendLine(Cell(e));
                break;

            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                {
                    if (HiddenColumns.Contains(prop.Name) && prop.Value.ValueKind != JsonValueKind.Object) continue;
                    if (prop.Value.ValueKind is JsonValueKind.Null) continue;
                    if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() == 0) continue;

                    if (prop.Value.ValueKind == JsonValueKind.Array && prop.Value.GetArrayLength() > 0
                        || prop.Value.ValueKind == JsonValueKind.Object && IsContainer(prop))
                    {
                        sb.Append(pad).Append(prop.Name).AppendLine(":");
                        AppendValue(sb, prop.Value, indent + 1);
                    }
                    else
                    {
                        sb.Append(pad).Append(prop.Name).Append(": ").AppendLine(Cell(prop.Value));
                    }
                }
                break;

            default:
                sb.Append(pad).AppendLine(Cell(value));
                break;
        }
    }

    // A symbol-like object prints as one line; anything else is expanded.
    private static bool IsContainer(JsonProperty prop) =>
        !(prop.Value.TryGetProperty("kind", out _) && (prop.Value.TryGetProperty("full_name", out _) || prop.Value.TryGetProperty("name", out _)));

    // Outlines and hierarchies nest through "children"; show them as an indented tree rather than a table.
    private static void AppendTree(StringBuilder sb, JsonElement nodes, int indent)
    {
        foreach (var node in nodes.EnumerateArray())
        {
            // A hierarchy node wraps its symbol; an outline node is the symbol.
            var symbol = node.TryGetProperty("symbol", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : node;
            var relation = node.TryGetProperty("relation", out var rel) && rel.ValueKind == JsonValueKind.String ? $"[{rel.GetString()}] " : "";
            sb.Append(new string(' ', indent * 2)).Append(relation).AppendLine(Describe(symbol, withLines: true));
            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array && children.GetArrayLength() > 0)
                AppendTree(sb, children, indent + 1);
        }
    }

    private static void AppendTable(StringBuilder sb, JsonElement rows, int indent)
    {
        var pad = new string(' ', indent * 2);
        var objects = rows.EnumerateArray().ToList();

        var columns = new List<string>();
        foreach (var row in objects)
            foreach (var prop in row.EnumerateObject())
            {
                if (HiddenColumns.Contains(prop.Name) && prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (prop.Value.ValueKind == JsonValueKind.Null || columns.Contains(prop.Name)) continue;
                columns.Add(prop.Name);
            }

        var grid = objects
            .Select(row => columns.Select(c => row.TryGetProperty(c, out var v) ? Cell(v) : "").ToArray())
            .ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Length, grid.Count == 0 ? 0 : grid.Max(r => r[i].Length))).ToArray();

        sb.Append(pad).AppendLine(string.Join("  ", columns.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        foreach (var row in grid)
            sb.Append(pad).AppendLine(string.Join("  ", row.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd());
    }

    private static string Cell(JsonElement v) => Truncate((v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        JsonValueKind.Null => "",
        JsonValueKind.Array => $"[{v.GetArrayLength()}]",
        JsonValueKind.Object => Describe(v),
        _ => v.GetRawText(),
    }).ReplaceLineEndings(" "));

    private static string Describe(JsonElement symbol, bool withLines = false)
    {
        string Get(string n) => symbol.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        var name = Get("full_name") is { Length: > 0 } full ? full.Replace("global::", "") : Get("name");
        var kind = Get("kind");
        var id = symbol.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? $" #{idEl.GetRawText()}" : "";
        var path = Get("file_path");
        var line = symbol.TryGetProperty("start_line", out var l) && l.ValueKind == JsonValueKind.Number ? $":{l.GetRawText()}" : "";
        var where = path.Length > 0 ? $" ({path}{line})" : "";
        if (withLines)
        {
            var end = symbol.TryGetProperty("end_line", out var e) && e.ValueKind == JsonValueKind.Number ? $"-{e.GetRawText()}" : "";
            var start = symbol.TryGetProperty("start_line", out var st) && st.ValueKind == JsonValueKind.Number ? $" :{st.GetRawText()}{end}" : "";
            where = start;
        }
        return $"{kind} {name}{id}{where}".Trim();
    }

    private static string Truncate(string s) => s.Length <= MaxCell ? s : s[..(MaxCell - 1)] + "~";
}
