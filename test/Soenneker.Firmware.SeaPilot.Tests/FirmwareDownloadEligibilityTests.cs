using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace Soenneker.Firmware.SeaPilot.Tests;

public sealed class FirmwareDownloadEligibilityTests
{
    [Test]
    [Arguments("SN04", "package-SN04.zip", true)]
    [Arguments("sn04", "package-SN04.zip", true)]
    [Arguments("SN05", "package-SN04.zip", true)]
    [Arguments("SN04", "package-SN05.zip", false)]
    [Arguments("SN04", "package-SC04.zip", false)]
    [Arguments("SN04", "certificate.html", false)]
    [Arguments(null, "package-SN04.zip", false)]
    public async Task CancelsOnlyUnneededAutomaticDownloads(string? installed, string filename, bool skip)
    {
        bool cancelled = false;
        bool skipped = false;
        try { await FirmwareDownloadEligibility.CheckDownload(filename, installed, () => { cancelled = true; return Task.CompletedTask; }); }
        catch (FirmwareDownloadSkippedException e)
        {
            skipped = true;
            if (!e.Message.Contains("skipping download and update")) throw new Exception("Missing clear skip reason.");
        }
        if (cancelled != skip || skipped != skip) throw new Exception("Incorrect download decision.");
    }

    [Test]
    public void ResponseHeadersAllowSkippingBeforeTransfer()
    {
        foreach (string header in new[] { "attachment; filename=package-SN04.zip", "attachment; filename*=UTF-8''package-SN04.zip" })
        {
            string? filename = FirmwareDownloadEligibility.GetHeaderFilename(header);
            if (filename != "package-SN04.zip" || FirmwareDownloadEligibility.GetSkipReason(filename, "SN04") is null)
                throw new Exception("Header revision was not recognized.");
        }
    }

    [Test]
    public async Task CancelsRealBrowserTransferBeforeBodyCompletes()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        Skip.Unless(File.Exists(playwright.Chromium.ExecutablePath), "Install Chromium for browser integration tests.");
        await using var server = new LocalFirmwareServer(slow: true);
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "chromium" });
        await using IBrowserContext context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        IPage page = await context.NewPageAsync();
        IDownload download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            try { await page.GotoAsync(server.Url); }
            catch (PlaywrightException e) when (e.Message.Contains("ERR_ABORTED") || e.Message.Contains("Download is starting")) { }
        });
        bool skipped = false;
        try
        {
            try { await FirmwareDownloadEligibility.CheckDownload(download.SuggestedFilename, "SN06", download.CancelAsync).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (FirmwareDownloadSkippedException) { skipped = true; }
            if (!skipped || await download.FailureAsync().WaitAsync(TimeSpan.FromSeconds(5)) is null)
                throw new Exception("Same-revision download was not cancelled before completion.");
        }
        finally { if (!skipped) await download.DeleteAsync(); }
    }
}
