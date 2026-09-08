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
}
