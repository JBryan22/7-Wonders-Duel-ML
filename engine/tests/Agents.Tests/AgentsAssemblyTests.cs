namespace SevenWondersDuel.Agents.Tests;

public class AgentsAssemblyTests
{
    [Fact]
    public void Agents_assembly_loads() => Assert.NotNull(typeof(SevenWondersDuel.Agents.AgentsInfo).Assembly);
}
