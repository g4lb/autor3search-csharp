using Demo;
using Xunit;

namespace Demo.Tests;

public class WordCountTests
{
    [Fact]
    public void CountsRepeatedWords()
    {
        var got = WordCount.CountWords("the quick brown the");
        Assert.Equal(2, got["the"]);
        Assert.Equal(1, got["quick"]);
        Assert.Equal(1, got["brown"]);
        Assert.Equal(3, got.Count);
    }

    [Fact]
    public void LowercasesAndStripsPunctuation()
    {
        var got = WordCount.CountWords("Hello, WORLD! hello?");
        Assert.Equal(2, got["hello"]);
        Assert.Equal(1, got["world"]);
        Assert.Equal(2, got.Count);
    }

    [Fact]
    public void KeepsDigits()
    {
        var got = WordCount.CountWords("abc123 abc123");
        Assert.Equal(2, got["abc123"]);
    }

    [Fact]
    public void IgnoresPunctuationOnlyFields()
    {
        Assert.Empty(WordCount.CountWords("!!! ??? ..."));
    }

    /// <summary>
    /// Counts over a programmatically built input, so no implementation can pass by
    /// special-casing the literal strings the other tests use.
    /// </summary>
    [Fact]
    public void CountsCorrectlyOverAGeneratedInput()
    {
        // 50 repetitions of three distinct words, with punctuation and mixed case that
        // the counter is required to normalise away.
        var input = string.Concat(Enumerable.Repeat("Alpha, BETA! gamma? ", 50));

        var got = WordCount.CountWords(input);

        Assert.Equal(3, got.Count);
        Assert.Equal(50, got["alpha"]);
        Assert.Equal(50, got["beta"]);
        Assert.Equal(50, got["gamma"]);
    }
}
