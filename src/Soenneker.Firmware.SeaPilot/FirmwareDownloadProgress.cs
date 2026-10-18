using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Observes Chromium download events without changing Playwright's download ownership or paths.</summary>
public sealed class FirmwareDownloadProgress : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly string _serial;
    private readonly string? _installed;
    private ICDPSession? _session;
    private string? _guid;
    private long _lastReport;
    internal string? Filename { get; private set; }
    internal double ReceivedBytes { get; private set; }
    internal double? Percentage { get; private set; }

    internal FirmwareDownloadProgress(ILogger logger, string serial, string? installed)
    {
        _logger = logger;
        _serial = serial;
        _installed = installed;
    }

    /// <summary>Enables optional download telemetry. Unsupported telemetry does not prevent downloading.</summary>
    public static async Task<FirmwareDownloadProgress> Attach(IPage page, ILogger logger, string serial, string? installed = null)
    {
        var progress = new FirmwareDownloadProgress(logger, serial, installed);
        try
        {
            progress._session = await page.Context.NewCDPSessionAsync(page);
            progress._session.Event("Page.downloadWillBegin").OnEvent += progress.OnBegin;
            progress._session.Event("Page.downloadProgress").OnEvent += progress.OnProgress;
            await progress._session.SendAsync("Page.enable");
        }
        catch (PlaywrightException)
        {
            logger.LogInformation("Download percentage unavailable for {Serial}; waiting for the browser download to finish.", serial);
            await progress.DisposeAsync();
        }
        return progress;
    }

    internal static string DescribeRevision(string filename, string? installed)
    {
        string target;
        try { target = FirmwarePackageResolver.GetRevision(filename); }
        catch (InvalidDataException) { return "target revision not present in filename; will inspect the package"; }
        return installed is null ? $"target {target}" : $"installed {installed} -> offered {target}";
    }

    internal void OnBegin(object? sender, JsonElement? message)
    {
        if (message is not { } data || !data.TryGetProperty("guid", out JsonElement guid) ||
            !data.TryGetProperty("suggestedFilename", out JsonElement filename)) return;
        _guid = guid.GetString();
        Filename = filename.GetString();
        if (Filename is null) return;
        if (FirmwareDownloadEligibility.GetSkipReason(Filename, _installed) is { } reason)
        {
            _logger.LogInformation("{Serial}: {Reason}", _serial, reason);
            return;
        }
        _logger.LogInformation("Downloading {Filename} for {Serial}: {Revision}.", Filename, _serial, DescribeRevision(Filename, _installed));
    }

    internal void OnProgress(object? sender, JsonElement? message)
    {
        if (message is not { } data || _guid is null ||
            !data.TryGetProperty("guid", out JsonElement guid) || guid.GetString() != _guid ||
            !data.TryGetProperty("receivedBytes", out JsonElement received) || !received.TryGetDouble(out double bytes) ||
            !data.TryGetProperty("totalBytes", out JsonElement total) || !total.TryGetDouble(out double size) ||
            !data.TryGetProperty("state", out JsonElement state)) return;
        if (Filename is not null && FirmwareDownloadEligibility.GetSkipReason(Filename, _installed) is not null) return;
        if (!double.IsFinite(bytes) || bytes < 0 || !double.IsFinite(size)) return;
        string? status = state.GetString();
        ReceivedBytes = bytes;
        Percentage = size > 0 ? Math.Clamp(bytes / size * 100, 0, status == "completed" ? 100 : 99) : null;
        if (status == "canceled")
        {
            _logger.LogInformation("Download of {Filename} stopped after {MiB:F1} MiB.", Filename, bytes / 1048576);
            return;
        }
        if (_lastReport != 0 && status != "completed" && Stopwatch.GetElapsedTime(_lastReport) < TimeSpan.FromSeconds(5)) return;
        _lastReport = Stopwatch.GetTimestamp();
        if (Percentage is { } percent)
            _logger.LogInformation("Downloading {Filename}: {Percent:F0}% ({Received:F1} / {Total:F1} MiB).", Filename, percent, bytes / 1048576, size / 1048576);
        else
            _logger.LogInformation("Downloading {Filename}: {Received:F1} MiB received; total size unknown.", Filename, bytes / 1048576);
    }

    /// <summary>Detaches telemetry; browser teardown errors do not mask the download outcome.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_session is null) return;
        _session.Event("Page.downloadWillBegin").OnEvent -= OnBegin;
        _session.Event("Page.downloadProgress").OnEvent -= OnProgress;
        try { await _session.DetachAsync(); }
        catch (PlaywrightException) { }
        _session = null;
    }
}
