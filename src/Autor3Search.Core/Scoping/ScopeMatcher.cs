using System.Text;
using System.Text.RegularExpressions;

namespace Autor3Search.Core.Scoping;

/// <summary>
/// Matches repo-relative paths against the configured scope patterns.
///
/// Supported: <c>**</c> at any depth, <c>*</c> within one segment, <c>?</c> for one
/// character, and a bare directory prefix. Everything else in a pattern is literal —
/// a filename containing a regex metacharacter must not become a wildcard.
/// </summary>
public sealed class ScopeMatcher
{
    private readonly List<Regex> _patterns;

    /// <summary>Compiles the scope patterns.</summary>
    public ScopeMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(Compile)
            .ToList();
    }

    /// <summary>Reports whether the path falls inside the scope.</summary>
    public bool Match(string relativePath)
    {
        var normalized = Paths.ToSlash(relativePath);
        return _patterns.Any(r => r.IsMatch(normalized));
    }

    private static Regex Compile(string pattern)
    {
        var p = Paths.ToSlash(pattern).TrimEnd('/');
        var sb = new StringBuilder("^");

        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '*' && i + 1 < p.Length && p[i + 1] == '*')
            {
                // "a/**" and "a/**/b": the ** absorbs the following slash so that
                // "src/**" also matches "src/A.cs", not only "src/x/A.cs".
                sb.Append(".*");
                i++;
                if (i + 1 < p.Length && p[i + 1] == '/') i++;
                continue;
            }

            sb.Append(p[i] switch
            {
                '*' => "[^/]*",
                '?' => "[^/]",
                _ => Regex.Escape(p[i].ToString()),
            });
        }

        // A pattern naming a directory covers everything beneath it.
        sb.Append("(/.*)?$");

        return new Regex(sb.ToString(), RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }
}
