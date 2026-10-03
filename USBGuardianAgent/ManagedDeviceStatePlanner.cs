namespace USBGuardianAgent;

public enum ManagedDeviceAction
{
    None,
    Disable,
    Enable,
    ClearOwnership
}

public static class ManagedDeviceStatePlanner
{
    public static ManagedDeviceAction Decide(
        bool shouldBeBlocked,
        bool isRunning,
        bool disabledByAgent)
    {
        if (shouldBeBlocked)
            return isRunning
                ? ManagedDeviceAction.Disable
                : ManagedDeviceAction.None;

        if (!disabledByAgent) return ManagedDeviceAction.None;
        return isRunning
            ? ManagedDeviceAction.ClearOwnership
            : ManagedDeviceAction.Enable;
    }
}
