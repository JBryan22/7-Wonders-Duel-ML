using SevenWondersDuel.Agents;

namespace SevenWondersDuel.Agents.Tests;

public class PowerThrottlingTests
{
    [Fact]
    public void OptOut_turns_execution_speed_throttling_off_on_Windows_and_does_nothing_elsewhere()
    {
        var accepted = PowerThrottling.OptOut();
        var current = PowerThrottling.Current();
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(accepted);
            Assert.Null(current);
            return;
        }
        Assert.True(accepted);
        Assert.NotNull(current);
        Assert.NotEqual(0u, current.Value.Control & PowerThrottling.ExecutionSpeed); // we decide, not Windows
        Assert.Equal(0u, current.Value.State & PowerThrottling.ExecutionSpeed);     // and we said: not throttled
    }
}
