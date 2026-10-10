using SevenWondersDuel.Engine;

namespace SevenWondersDuel.Engine.Tests;

public class EngineInfoTests
{
    [Fact]
    public void Engine_assembly_loads() => Assert.Equal("7wd-engine", EngineInfo.Name);
}
