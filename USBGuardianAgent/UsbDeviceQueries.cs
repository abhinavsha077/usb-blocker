namespace USBGuardianAgent;

public static class UsbDeviceQueries
{
    public const string Storage = "SELECT * FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USBSTOR%' OR " +
        "(Service='UASPStor' AND PNPDeviceID LIKE 'USB\\\\%')";
    public const string Portable = "SELECT * FROM Win32_PnPEntity WHERE " +
        "(PNPClass='WPD' OR PNPClass='PortableDevice') AND PNPDeviceID LIKE '%USB%'";
    public static readonly string[] Removable = { Storage, Portable };
}
