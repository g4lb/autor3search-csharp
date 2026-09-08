namespace Autor3Search.Core.Bench;

/// <summary>
/// Shared combinatorial helpers.
///
/// Deliberately one copy. Both the Mann-Whitney p-value floor and the median
/// confidence interval's order-statistic search depend on this function, and a fix
/// applied to one of two copies would make them disagree silently while each still
/// looked self-consistent.
/// </summary>
internal static class Combinatorics
{
    /// <summary>
    /// C(n, k) as a double, multiplying and dividing in step so the running value
    /// stays near the result rather than overflowing through a factorial.
    /// </summary>
    internal static double Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0.0;
        k = Math.Min(k, n - k);

        var result = 1.0;
        for (var i = 1; i <= k; i++) result = result * (n - k + i) / i;
        return result;
    }
}
