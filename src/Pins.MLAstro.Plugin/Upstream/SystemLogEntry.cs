using System;
using System.Globalization;



namespace MLAstroRPA.Dockables
{
    /// <summary>Level of a system log line (mirrors the Web UI CSS class).</summary>
    public enum SystemLogLevel
    {
        Info,
        Success,
        Warning,
        Critical,
        Apply,
        RebootRequired,
        Reset
    }

    /// <summary>
    /// One line of the System log table on the CONNECTION tab - it mirrors the Web UI System Log:
    /// a timestamp in front, colour by keyword, newest line on top.
    /// Firmware/plugin messages only (no raw TX/RX frames).
    /// </summary>
    public class SystemLogEntry
    {
        public SystemLogEntry(string message, SystemLogLevel level, DateTime timestamp)
        {
            // The Web UI uses toLocaleTimeString() -> the SHORT time format of the system locale
            // (vi-VN: "17:07:51" 24-hour; en-US: "5:07:51 PM"). The .NET "t" pattern - also taken from
            // the system locale - keeps the plugin time column identical to the web log table.
            Time = timestamp.ToString("t", CultureInfo.CurrentCulture);
            Message = message ?? string.Empty;
            Level = level;
        }

        /// <summary>Timestamp, formatted like the Web UI (e.g. "11:08:06 PM").</summary>
        public string Time { get; }

        public string Message { get; }

        public SystemLogLevel Level { get; }

        /// <summary>Full display line: "[time] content".</summary>
        public string DisplayText => $"[{Time}] {Message}";

        /// <summary>Bold like the Web UI for critical / apply / reboot-required lines.</summary>
    }
}
