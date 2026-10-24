using System.Text.RegularExpressions;

namespace CodeIndexer.Core.Queries;

/// <summary>
/// Says which symbol a command is about. In order of precision: a row id, a key, a source position, a name.
/// A name that matches several symbols yields candidates rather than an arbitrary first hit.
/// </summary>
public sealed record SymbolSelector(
    long? Id = null,
    string? Key = null,
    string? Text = null,
    string? Kind = null,
    bool IncludeGenerated = false)
{
    public bool IsEmpty => Id is null && string.IsNullOrWhiteSpace(Key) && string.IsNullOrWhiteSpace(Text);

    private static readonly Regex PositionPattern =
        new(@"^(?<file>.+?):(?<line>\d+)(?::(?<col>\d+))?$", RegexOptions.Compiled);

    private static readonly Regex DocumentationId =
        new(@"^[NTFPME]:\S+$", RegexOptions.Compiled);

    /// <summary>"path/to/File.cs:12" or "path/to/File.cs:12:5" (1-based line and column).</summary>
    public bool TryGetPosition(out string file, out int line, out int? column)
    {
        file = ""; line = 0; column = null;
        if (Text is null) return false;
        var m = PositionPattern.Match(Text.Trim());
        if (!m.Success) return false;
        file = PathUtil.Normalize(m.Groups["file"].Value);
        line = int.Parse(m.Groups["line"].Value);
        column = m.Groups["col"].Success ? int.Parse(m.Groups["col"].Value) : null;
        return true;
    }

    /// <summary>A documentation id such as "M:Ns.Type.Method(System.Int32)" pasted without its assembly.</summary>
    public bool LooksLikeDocumentationId => Text is not null && DocumentationId.IsMatch(Text.Trim());

    /// <summary>The name with any generic argument list removed: "Repository&lt;T&gt;" becomes "Repository".</summary>
    public string? BareName => Text is null ? null : Regex.Replace(Text.Trim(), "<[^<>]*>", "");
}
