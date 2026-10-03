using USBGuardianAgent;

namespace USBGuardianTests;

public sealed class ManagedDeviceStatePlannerTests
{
    [Theory]
    [InlineData(true, true, false, ManagedDeviceAction.Disable)]
    [InlineData(true, true, true, ManagedDeviceAction.Disable)]
    [InlineData(true, false, false, ManagedDeviceAction.None)]
    [InlineData(true, false, true, ManagedDeviceAction.None)]
    [InlineData(false, false, false, ManagedDeviceAction.None)]
    [InlineData(false, false, true, ManagedDeviceAction.Enable)]
    [InlineData(false, true, false, ManagedDeviceAction.None)]
    [InlineData(false, true, true, ManagedDeviceAction.ClearOwnership)]
    public void DecisionPreservesExternalDeviceState(
        bool shouldBeBlocked,
        bool isRunning,
        bool disabledByAgent,
        ManagedDeviceAction expected)
    {
        Assert.Equal(
            expected,
            ManagedDeviceStatePlanner.Decide(
                shouldBeBlocked,
                isRunning,
                disabledByAgent));
    }
}
