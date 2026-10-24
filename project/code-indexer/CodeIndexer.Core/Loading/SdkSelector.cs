using System.Text.Json;

namespace CodeIndexer.Core.Loading;

/// <summary>The "sdk" section of a global.json file.</summary>
public sealed record GlobalJsonPin(string Version, string RollForward, bool AllowPrerelease);

/// <summary>
/// Chooses which installed .NET SDK's MSBuild to register, honoring the global.json that governs the
/// solution being indexed (the same file the dotnet CLI would honor).
/// </summary>
public static class SdkSelector
{
    /// <summary>Walks up from <paramref name="startDirectory"/> looking for a global.json with an sdk.version.</summary>
    public static (string Path, GlobalJsonPin Pin)? FindGlobalJson(string startDirectory)
    {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "global.json");
            if (File.Exists(candidate))
            {
                var pin = TryParse(File.ReadAllText(candidate));
                if (pin is not null) return (candidate, pin);
            }
            dir = dir.Parent;
        }
        return null;
    }

    public static GlobalJsonPin? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!doc.RootElement.TryGetProperty("sdk", out var sdk)) return null;
            if (!sdk.TryGetProperty("version", out var version) || version.GetString() is not { Length: > 0 } v) return null;
            var roll = sdk.TryGetProperty("rollForward", out var r) ? r.GetString() ?? "latestPatch" : "latestPatch";
            var pre = sdk.TryGetProperty("allowPrerelease", out var p) && p.ValueKind == JsonValueKind.True;
            return new GlobalJsonPin(v, roll, pre);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Picks the SDK version to use. With no pin, the highest stable version (prereleases only when nothing
    /// else exists). With a pin, follows the global.json rollForward policy; returns null when no installed
    /// SDK satisfies it.
    /// </summary>
    public static Version? Select(IReadOnlyCollection<Version> installed, GlobalJsonPin? pin)
    {
        if (installed.Count == 0) return null;
        if (pin is null) return installed.Max();

        if (!Version.TryParse(StripPrerelease(pin.Version), out var want)) return null;

        bool Feature(Version v) => v.Major == want.Major && v.Minor == want.Minor && v.Build / 100 == want.Build / 100;
        bool Minor(Version v) => v.Major == want.Major && v.Minor == want.Minor;
        bool Major(Version v) => v.Major == want.Major;

        IEnumerable<Version> atLeast = installed.Where(v => v >= want);
        return pin.RollForward.ToLowerInvariant() switch
        {
            "disable" => installed.Contains(want) ? want : null,
            "patch" => PickOrNull(atLeast.Where(Feature), min: true),
            "latestpatch" => PickOrNull(atLeast.Where(Feature), min: false),
            "feature" => PickOrNull(atLeast.Where(Minor), min: true),
            "latestfeature" => PickOrNull(atLeast.Where(Minor), min: false),
            "minor" => PickOrNull(atLeast.Where(Major), min: true),
            "latestminor" => PickOrNull(atLeast.Where(Major), min: false),
            "major" => PickOrNull(atLeast, min: true),
            "latestmajor" => PickOrNull(atLeast, min: false),
            _ => PickOrNull(atLeast.Where(Feature), min: false),
        };
    }

    private static Version? PickOrNull(IEnumerable<Version> versions, bool min)
    {
        var list = versions.ToList();
        if (list.Count == 0) return null;
        return min ? list.Min() : list.Max();
    }

    private static string StripPrerelease(string version)
    {
        var i = version.IndexOfAny(new[] { '-', '+' });
        return i < 0 ? version : version[..i];
    }
}
