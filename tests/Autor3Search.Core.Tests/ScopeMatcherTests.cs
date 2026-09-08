using Autor3Search.Core.Scoping;
using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Tests for <see cref="ScopeMatcher"/>.</summary>
public class ScopeMatcherTests
{
    /// <summary>A trailing <c>**</c> matches at any depth beneath the directory.</summary>
    [Fact]
    public void DoubleStarMatchesAtAnyDepth()
    {
        var m = new ScopeMatcher(["src/**"]);
        Assert.True(m.Match("src/A.cs"));
        Assert.True(m.Match("src/deep/nested/B.cs"));
        Assert.False(m.Match("tests/A.cs"));
    }

    /// <summary>A single <c>*</c> does not cross a path separator.</summary>
    [Fact]
    public void SingleStarStopsAtASeparator()
    {
        var m = new ScopeMatcher(["src/*.cs"]);
        Assert.True(m.Match("src/A.cs"));
        Assert.False(m.Match("src/deep/B.cs"));
    }

    /// <summary>A bare directory prefix covers everything beneath it.</summary>
    [Fact]
    public void APlainPrefixMatchesTheDirectory()
    {
        var m = new ScopeMatcher(["src"]);
        Assert.True(m.Match("src/A.cs"));
        Assert.True(m.Match("src/deep/B.cs"));
        Assert.False(m.Match("srcextra/A.cs"));
    }

    /// <summary>A pattern with no wildcard matches only that exact path.</summary>
    [Fact]
    public void AnExactPathMatchesOnlyItself()
    {
        var m = new ScopeMatcher(["src/A.cs"]);
        Assert.True(m.Match("src/A.cs"));
        Assert.False(m.Match("src/B.cs"));
    }

    /// <summary>A path is in scope if any one pattern matches it.</summary>
    [Fact]
    public void AnyPatternMatchingIsEnough()
    {
        var m = new ScopeMatcher(["src/**", "lib/**"]);
        Assert.True(m.Match("src/A.cs"));
        Assert.True(m.Match("lib/B.cs"));
        Assert.False(m.Match("other/C.cs"));
    }

    /// <summary>A bare <c>**</c> pattern matches every path.</summary>
    [Fact]
    public void BareDoubleStarMatchesEverything()
    {
        var m = new ScopeMatcher(["**"]);
        Assert.True(m.Match("anything/at/all.cs"));
        Assert.True(m.Match("root.cs"));
    }

    // Every path reaching the matcher is already forward-slashed by Paths.ToSlash,
    // but a caller that forgets must not silently get a non-match on Windows.
    /// <summary>A backslash-separated path is normalised before matching.</summary>
    [Fact]
    public void BackslashPathsAreNormalisedBeforeMatching()
    {
        var m = new ScopeMatcher(["src/**"]);
        Assert.True(m.Match(@"src\A.cs"));
    }

    /// <summary>A <c>?</c> matches exactly one character.</summary>
    [Fact]
    public void QuestionMarkMatchesOneCharacter()
    {
        var m = new ScopeMatcher(["src/?.cs"]);
        Assert.True(m.Match("src/A.cs"));
        Assert.False(m.Match("src/AB.cs"));
    }

    /// <summary>Regex metacharacters in a pattern are treated as literal text.</summary>
    [Fact]
    public void RegexMetacharactersInAPatternAreLiteral()
    {
        var m = new ScopeMatcher(["src/a+b.cs"]);
        Assert.True(m.Match("src/a+b.cs"));
        Assert.False(m.Match("src/aab.cs"));
    }
}
