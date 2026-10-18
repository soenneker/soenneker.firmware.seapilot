using System;
using System.IO;

namespace Soenneker.Firmware.SeaPilot.Options;

/// <summary>Browser and operation limits. The installer uses the application's Playwright configuration.</summary>
public sealed class SeaPilotOptions
{
    /// <summary>Persistent cache of complete packages and checksum receipts. Survives browser cleanup and failed destination copies.</summary>
    public string CacheDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soenneker", "Firmware", "SeaPilot");
    /// <summary>Whether Chromium runs without a visible window.</summary>
    public bool Headless { get; set; } = true;
    /// <summary>Lookup deadline after browser installation. Installation uses the installation utility's own cancellation behavior.</summary>
    public TimeSpan LookupTimeout { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Download deadline after browser installation, including browser startup and saving the file.</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Maximum package size accepted into the destination. Chromium first downloads into its owned temporary directory.</summary>
    public long MaximumDownloadBytes { get; set; } = 1024L * 1024 * 1024;
}
