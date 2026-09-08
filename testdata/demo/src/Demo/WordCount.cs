namespace Demo;

/// <summary>Counts word occurrences. Deliberately inefficient.</summary>
public static class WordCount
{
    /// <summary>Returns how many times each lowercase word appears in <paramref name="s"/>.</summary>
    public static Dictionary<string, int> CountWords(string s)
    {
        var counts = new Dictionary<string, int>();

        foreach (var field in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var word = "";
            foreach (var r in field)
            {
                var c = char.ToLowerInvariant(r);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    word += c;   // quadratic: rebuilds the string every character
                }
            }

            if (word.Length > 0)
            {
                counts[word] = counts.TryGetValue(word, out var n) ? n + 1 : 1;
            }
        }

        return counts;
    }
}
