using USBGuardianAgent;

namespace USBGuardianTests;

public class DeviceMatcherTests
{
    [Fact]
    public void FullInstanceIdOnlyMatchesExactDevice()
    {
        const string allowed = @"USB\VID_0781&PID_5567\SERIAL-A";

        Assert.True(DeviceMatcher.MatchesEntry(allowed, allowed));
        Assert.False(DeviceMatcher.MatchesEntry(
            @"USB\VID_0781&PID_5567\SERIAL-B", allowed));
    }

    [Fact]
    public void VidPidTokenMatchesEveryUnitOfModel()
    {
        const string token = @"USB\VID_0781&PID_5567";

        Assert.True(DeviceMatcher.MatchesEntry(
            @"USB\VID_0781&PID_5567\SERIAL-A", token));
        Assert.True(DeviceMatcher.MatchesEntry(
            @"usb\vid_0781&pid_5567\serial-b", token));
    }

    [Fact]
    public void StorageVendorProductTokenMatchesStorageInstance()
    {
        const string token = @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA";

        Assert.True(DeviceMatcher.MatchesEntry(
            @"USBSTOR\DISK&VEN_SANDISK&PROD_ULTRA&REV_1.00\123456", token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("USB")]
    [InlineData("VID_0781")]
    [InlineData(@"USB\VID_0781&PID_5567\SERIAL-A")]
    public void NonModelEntriesAreNotTreatedAsBroadTokens(string entry)
    {
        Assert.False(DeviceMatcher.IsModelToken(entry));
    }
}
