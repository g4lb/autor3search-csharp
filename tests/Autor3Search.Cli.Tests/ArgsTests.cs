using Autor3Search.Cli;
using Xunit;

namespace Autor3Search.Cli.Tests;

/// <summary>Tests for the hand-rolled flag parser.</summary>
public class ArgsTests
{
    /// <summary>The first token is always the command.</summary>
    [Fact]
    public void TheFirstTokenIsTheCommand()
    {
        Assert.Equal("eval", Args.Parse(["eval"]).Command);
    }

    /// <summary>An empty argument list has an empty command.</summary>
    [Fact]
    public void AnEmptyArgumentListHasAnEmptyCommand()
    {
        Assert.Equal("", Args.Parse([]).Command);
    }

    /// <summary>A value flag is accepted in every form: -name value, --name value, -name=value, --name=value.</summary>
    [Theory]
    [InlineData(new object[] { new[] { "baseline", "-tag", "sep8" } })]
    [InlineData(new object[] { new[] { "baseline", "--tag", "sep8" } })]
    [InlineData(new object[] { new[] { "baseline", "-tag=sep8" } })]
    [InlineData(new object[] { new[] { "baseline", "--tag=sep8" } })]
    public void AValueFlagIsAcceptedInEveryForm(string[] argv)
    {
        Assert.Equal("sep8", Args.Parse(argv).GetString("tag"));
    }

    /// <summary>A flag that was never given reads back as null.</summary>
    [Fact]
    public void AMissingValueFlagIsNull()
    {
        Assert.Null(Args.Parse(["baseline"]).GetString("tag"));
    }

    /// <summary>Boolean flags default to false and become true when given.</summary>
    [Fact]
    public void BooleanFlagsDefaultToFalseAndSetToTrue()
    {
        Assert.False(Args.Parse(["eval"]).GetFlag("json"));
        Assert.True(Args.Parse(["eval", "--json"]).GetFlag("json"));
        Assert.True(Args.Parse(["eval", "-json"]).GetFlag("json"));
    }

    // A boolean flag must not swallow the next token: `stop -force -tag sep8` has to
    // leave "-tag sep8" intact, or -force would consume "-tag" as its value.
    /// <summary>A boolean flag must not consume the following flag as its value.</summary>
    [Fact]
    public void ABooleanFlagDoesNotConsumeTheFollowingFlag()
    {
        var a = Args.Parse(["stop", "-force", "-tag", "sep8"]);
        Assert.True(a.GetFlag("force"));
        Assert.Equal("sep8", a.GetString("tag"));
    }

    /// <summary>An explicit =true or =false value is honoured for a boolean flag.</summary>
    [Fact]
    public void ExplicitBooleanValuesAreHonoured()
    {
        Assert.False(Args.Parse(["eval", "--json=false"]).GetFlag("json"));
        Assert.True(Args.Parse(["eval", "--json=true"]).GetFlag("json"));
    }

    /// <summary>Non-flag tokens after the command are collected as positional arguments.</summary>
    [Fact]
    public void PositionalArgumentsAreCollected()
    {
        var a = Args.Parse(["report", "extra-one", "extra-two"]);
        Assert.Equal(["extra-one", "extra-two"], a.Positional);
    }

    /// <summary>The -C directory flag is recognised like any other value flag.</summary>
    [Fact]
    public void TheDirectoryFlagIsRecognised()
    {
        Assert.Equal("/tmp/repo", Args.Parse(["status", "-C", "/tmp/repo"]).GetString("C"));
    }

    /// <summary>Flag names are case-sensitive.</summary>
    [Fact]
    public void FlagNamesAreCaseSensitive()
    {
        // -C selects the directory; a lowercase -c must not be silently treated as it.
        var a = Args.Parse(["status", "-C", "/tmp/repo"]);
        Assert.Null(a.GetString("c"));
    }
}
