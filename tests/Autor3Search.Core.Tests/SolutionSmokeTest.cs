using Xunit;

namespace Autor3Search.Core.Tests;

/// <summary>Smoke tests confirming the solution wires up and the Core assembly loads.</summary>
public class SolutionSmokeTest
{
    /// <summary>Verifies the Core assembly can be loaded and reports its expected name.</summary>
    [Fact]
    public void CoreAssemblyLoads()
    {
        var asm = typeof(Autor3Search.Core.AssemblyMarker).Assembly;
        Assert.Equal("Autor3Search.Core", asm.GetName().Name);
    }
}
