namespace USBGuardianAgent
{
    /// <summary>
    /// Matches a device's PNPDeviceID against policy allow/block entries (P1.2).
    ///
    /// An entry may be either:
    ///   • a full instance ID  — e.g. USB\VID_0781&PID_5567\4C531001234567890  (exact match only)
    ///   • a vendor/model token — e.g. USB\VID_0781&PID_5567 · VID_0781&PID_5567
    ///                                  DISK&VEN_SANDISK&PROD_ULTRA  (substring / model-wide match)
    ///
    /// Full instance IDs carry a serial tail (2+ backslash-separated segments) and only
    /// match the exact same device. Vendor/model tokens (≤1 backslash) authorise every
    /// device of that make/model — the reference product's "VID/PID match" behaviour.
    /// </summary>
    public static class DeviceMatcher
    {
        public static bool Matches(string deviceId, IEnumerable<string> entries)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;
            foreach (var e in entries)
                if (MatchesEntry(deviceId, e)) return true;
            return false;
        }

        public static bool MatchesEntry(string deviceId, string entry)
        {
            if (string.IsNullOrEmpty(entry)) return false;
            if (string.Equals(deviceId, entry, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsModelToken(entry))
                return deviceId.IndexOf(entry, StringComparison.OrdinalIgnoreCase) >= 0;
            return false;
        }

        /// <summary>
        /// A vendor/model token identifies a make/model without a per-unit serial.
        /// Heuristic: contains VID_&PID_ (or VEN_&PROD_) and has at most one backslash
        /// (i.e. no trailing serial segment such as "\4C531001...").
        /// </summary>
        public static bool IsModelToken(string entry)
        {
            var u = entry.ToUpperInvariant();
            bool hasVidPid  = u.Contains("VID_")  && u.Contains("PID_");
            bool hasVenProd = u.Contains("VEN_") && u.Contains("PROD_");
            if (!hasVidPid && !hasVenProd) return false;
            return entry.Count(c => c == '\\') <= 1;
        }
    }
}
