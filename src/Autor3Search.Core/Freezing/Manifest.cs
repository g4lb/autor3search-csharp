using System.Text.Json;
using System.Text.Json.Serialization;

namespace Autor3Search.Core.Freezing;

/// <summary>One frozen file and the hash it was frozen at.</summary>
/// <param name="Path">Repo-relative path, forward slashes.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the file's bytes.</param>
public readonly record struct ManifestEntry(string Path, string Sha256);

/// <summary>
/// The set of files frozen for a run, with the hash each was frozen at.
///
/// Lives outside the repository. The agent edits the repository, so a manifest kept
/// in-tree would be a list of restrictions the restricted party could rewrite.
/// </summary>
public sealed class Manifest
{
    /// <summary>
    /// Repo-relative forward-slash path to hash.
    ///
    /// A public settable property, not a private field behind [JsonInclude]:
    /// System.Text.Json does not serialize private fields, and IncludeFields covers
    /// only PUBLIC fields — so the private-field form round-trips to an empty manifest,
    /// which would silently disable every freeze gate rather than fail loudly.
    ///
    /// The setter is public solely so System.Text.Json can populate this property on
    /// deserialization; callers should otherwise treat it as read-only and mutate a
    /// manifest only through <see cref="FromEntries"/>. It is deliberately NOT
    /// <c>init</c>-only or <c>private set</c> — those read as safer, but a non-public
    /// accessor here reintroduces exactly the class of serialization trap this type
    /// exists to avoid: some System.Text.Json configurations silently skip a property
    /// they cannot set, which would again round-trip to an empty, gate-disabling
    /// manifest instead of failing loudly.
    /// </summary>
    [JsonPropertyName("files")]
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Builds a manifest from entries.</summary>
    public static Manifest FromEntries(IEnumerable<ManifestEntry> entries)
    {
        var m = new Manifest();
        foreach (var e in entries) m.Files[e.Path] = e.Sha256;
        return m;
    }

    /// <summary>Reads a manifest from disk.</summary>
    public static Manifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Manifest>(json, Options)
               ?? throw new InvalidOperationException($"{path} is not a valid frozen manifest");
    }

    /// <summary>Writes the manifest to disk, creating parent directories.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };
}
