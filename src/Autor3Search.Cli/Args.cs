namespace Autor3Search.Cli;

/// <summary>
/// A minimal flag parser.
///
/// Hand-rolled on purpose: System.CommandLine is still prerelease, and this tool ships
/// to users who install it with `dotnet tool install -g` -- taking a prerelease
/// dependency into a globally installed tool buys a parser and sells the ability to
/// promise a stable install.
///
/// Accepts <c>-name value</c>, <c>--name value</c>, <c>-name=value</c> and
/// <c>--name=value</c>. Flag names are case-sensitive, so <c>-C</c> (the directory
/// flag) is never confused with a hypothetical <c>-c</c>.
/// </summary>
internal sealed class Args
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    /// <summary>The subcommand, or an empty string when none was given.</summary>
    public string Command { get; private init; } = "";

    /// <summary>Non-flag arguments after the command.</summary>
    public IReadOnlyList<string> Positional => _positional;

    /// <summary>Parses a command line.</summary>
    public static Args Parse(string[] argv)
    {
        var args = new Args { Command = argv.Length > 0 ? argv[0] : "" };

        for (var i = 1; i < argv.Length; i++)
        {
            var token = argv[i];

            if (!token.StartsWith('-'))
            {
                args._positional.Add(token);
                continue;
            }

            var name = token.TrimStart('-');

            var eq = name.IndexOf('=');
            if (eq >= 0)
            {
                args._values[name[..eq]] = name[(eq + 1)..];
                continue;
            }

            // A following token that itself starts with '-' belongs to the next flag,
            // not to this one: `stop -force -tag sep8` must not have -force eat -tag.
            if (i + 1 < argv.Length && !argv[i + 1].StartsWith('-'))
            {
                args._values[name] = argv[++i];
            }
            else
            {
                args._values[name] = "true";
            }
        }

        return args;
    }

    /// <summary>The value of a flag, or null when it was not given.</summary>
    public string? GetString(string name) => _values.GetValueOrDefault(name);

    /// <summary>Whether a boolean flag was set. Honours an explicit <c>=false</c>.</summary>
    public bool GetFlag(string name) =>
        _values.TryGetValue(name, out var v)
        && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);
}
