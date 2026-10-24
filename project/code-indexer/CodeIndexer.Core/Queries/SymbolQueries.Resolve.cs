namespace CodeIndexer.Core.Queries;

/// <summary>The outcome of turning a selector into a symbol.</summary>
public sealed class Resolution
{
    public SymbolResult? Symbol { get; init; }
    public List<SymbolResult> Candidates { get; init; } = new();
    public string? Warning { get; init; }
    public bool IsResolved => Symbol is not null;

    public static Resolution Found(SymbolResult symbol) => new() { Symbol = symbol };
    public static Resolution NotFound(string warning) => new() { Warning = warning };
    public static Resolution Ambiguous(string warning, List<SymbolResult> candidates) => new() { Warning = warning, Candidates = candidates };
}

public sealed partial class SymbolQueries
{
    private const int MaxCandidates = 10;

    /// <summary>
    /// Resolves a selector to exactly one symbol, or explains why it cannot. A name that matches several symbols is
    /// ambiguous and returns the candidates (with ids and keys) instead of guessing.
    /// </summary>
    public Resolution Resolve(SymbolSelector selector)
    {
        if (selector.IsEmpty)
            return Resolution.NotFound("No symbol given: pass a name, --id, --key, or a file:line[:col] position.");

        if (selector.Id is { } id)
            return GetSymbol(id) is { } byId ? Resolution.Found(byId) : Resolution.NotFound($"No symbol with id {id}.");

        if (!string.IsNullOrWhiteSpace(selector.Key))
            return ResolveKey(selector.Key.Trim(), selector);

        if (selector.TryGetPosition(out var file, out var line, out var column))
        {
            var at = At(file, line, column);
            if (at.Target is not null) return Resolution.Found(at.Target);
            return at.Candidates is { Count: > 0 }
                ? Resolution.Ambiguous(at.Warnings?.FirstOrDefault() ?? "Several symbols at that position.", at.Candidates)
                : Resolution.NotFound(at.Warnings?.FirstOrDefault() ?? $"Nothing found at {selector.Text}.");
        }

        if (selector.LooksLikeDocumentationId)
        {
            var byKey = ResolveKey(selector.Text!.Trim(), selector);
            if (byKey.IsResolved || byKey.Candidates.Count > 0) return byKey;
        }

        return ResolveName(selector);
    }

    private Resolution ResolveKey(string key, SymbolSelector selector)
    {
        // A full key ("Assembly|M:...") or just the documentation id.
        var matches = Read(
            $"SELECT {SymbolColumns} FROM {SymbolFrom} WHERE s.key = @k OR substr(s.key, instr(s.key, '|') + 1) = @k ORDER BY s.is_external, s.id",
            r => ReadSymbol(r), ("@k", key));
        matches = ApplyKind(matches, selector);
        return FromMatches(matches, $"No symbol with key '{key}'.", selector);
    }

    private Resolution ResolveName(SymbolSelector selector)
    {
        var text = selector.Text!.Trim();
        var bare = selector.BareName ?? text;
        var global = text.StartsWith("global::", StringComparison.Ordinal) ? text : "global::" + text;
        var withoutGlobal = text.StartsWith("global::", StringComparison.Ordinal) ? text["global::".Length..] : text;

        // Indexed symbols first (namespaces only if nothing else matches), then external stubs.
        foreach (var external in new[] { 0, 1 })
        {
            foreach (var allowNamespaces in new[] { false, true })
            {
                foreach (var tier in NameTiers(bare, text, global, withoutGlobal))
                {
                    var sql = $"SELECT {SymbolColumns} FROM {SymbolFrom} LEFT JOIN symbols c ON c.id = s.container_id WHERE ({tier.Where}) AND s.is_external = {external}" +
                              (allowNamespaces ? " AND s.kind = 'namespace'" : " AND s.kind <> 'namespace'") +
                              " ORDER BY s.is_generated, s.kind, f.path, s.start_line, s.id";
                    var matches = Read(sql, r => ReadSymbol(r), tier.Args);
                    matches = ApplyKind(matches, selector);
                    if (matches.Count > 0)
                        return FromMatches(matches, "", selector);
                }
            }
        }

        return Resolution.NotFound($"Symbol not found: {text}");
    }

    private static IEnumerable<(string Where, (string, object?)[] Args)> NameTiers(string bare, string text, string global, string withoutGlobal)
    {
        // 1. the fully qualified name; 2. the simple name; 3. "Container.Member" where Container is the declaring
        // type or namespace; 4. any trailing dotted path.
        yield return ("s.full_name = @a OR s.full_name = @b", new (string, object?)[] { ("@a", text), ("@b", global) });
        yield return ("s.name = @n", new (string, object?)[] { ("@n", bare) });

        var dot = bare.LastIndexOf('.');
        if (dot > 0 && dot < bare.Length - 1)
        {
            var member = bare[(dot + 1)..];
            var container = bare[..dot];
            var containerName = container[(container.LastIndexOf('.') + 1)..];
            yield return ("s.name = @member AND c.name = @container", new (string, object?)[] { ("@member", member), ("@container", containerName) });
        }

        yield return ("s.full_name LIKE @suffix ESCAPE '\\'", new (string, object?)[] { ("@suffix", "%." + EscapeLike(withoutGlobal)) });
    }

    private static List<SymbolResult> ApplyKind(List<SymbolResult> matches, SymbolSelector selector) =>
        string.IsNullOrEmpty(selector.Kind) ? matches : matches.Where(m => m.Kind.Equals(selector.Kind, StringComparison.OrdinalIgnoreCase)).ToList();

    private static Resolution FromMatches(List<SymbolResult> matches, string notFound, SymbolSelector selector)
    {
        if (matches.Count == 0) return Resolution.NotFound(notFound);

        // Hand-written code wins over generated code unless generated code was asked for.
        if (!selector.IncludeGenerated && matches.Any(m => !m.IsGenerated))
            matches = matches.Where(m => !m.IsGenerated).ToList();

        if (matches.Count == 1) return Resolution.Found(matches[0]);

        var shown = matches.Take(MaxCandidates).ToList();
        var more = matches.Count > shown.Count ? $" (showing {shown.Count} of {matches.Count})" : "";
        return Resolution.Ambiguous(
            $"'{selector.Text ?? selector.Key}' matches {matches.Count} symbols{more}; re-run with --id or --key (or --kind) to pick one.",
            shown);
    }
}
