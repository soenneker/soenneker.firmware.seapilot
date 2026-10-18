using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwareDownloadProgressTests(Host host) : HostedUnitTest(host)
{
    [Test]
    public void ReportsOnlyKnownRevisionAndMatchingDownloadProgress()
    {
        if (!FirmwareDownloadProgress.DescribeRevision("Exos-SN05.zip", "SN04").Contains("SN04 -> offered SN05") ||
            !FirmwareDownloadProgress.DescribeRevision("certificate.html", "SN04").Contains("not present"))
            throw new Exception("Revision labels are misleading.");
        var progress = new FirmwareDownloadProgress(Resolve<ILogger<FirmwareDownloadProgress>>(true), "SERIAL", "SN04");
        progress.OnBegin(null, JsonDocument.Parse("""{"guid":"first","suggestedFilename":"Exos-SN05.zip"}""").RootElement);
        progress.OnProgress(null, JsonDocument.Parse("""{"guid":"other","receivedBytes":100,"totalBytes":100,"state":"completed"}""").RootElement);
        if (progress.ReceivedBytes != 0) throw new Exception("Another download changed progress.");
        progress.OnProgress(null, JsonDocument.Parse("""{"guid":"first","receivedBytes":25,"totalBytes":100,"state":"inProgress"}""").RootElement);
        if (progress.Percentage != 25) throw new Exception("Incorrect byte percentage.");
        progress.OnProgress(null, JsonDocument.Parse("""{"guid":"first","receivedBytes":100,"totalBytes":100,"state":"inProgress"}""").RootElement);
        if (progress.Percentage >= 100) throw new Exception("Reported completion before Chromium completed.");
        progress.OnProgress(null, JsonDocument.Parse("""{"guid":"first","receivedBytes":100,"totalBytes":0,"state":"inProgress"}""").RootElement);
        if (progress.Percentage is not null || progress.ReceivedBytes != 100) throw new Exception("Invented percentage for unknown total.");
    }

    [Test]
    public async Task ChromiumProgressPreservesPlaywrightDownload()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        Skip.Unless(File.Exists(playwright.Chromium.ExecutablePath), "Install Playwright Chromium to run the browser telemetry integration test.");
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "chromium" });
        await using IBrowserContext context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        IPage page = await context.NewPageAsync();
        await using FirmwareDownloadProgress progress = await FirmwareDownloadProgress.Attach(page,
            Resolve<ILogger<FirmwareDownloadProgress>>(true), "SERIAL", "SN04");
        IDownload download = await page.RunAndWaitForDownloadAsync(() => page.EvaluateAsync("""
            () => {
                const link = document.createElement('a');
                link.href = URL.createObjectURL(new Blob([new Uint8Array(1048576)]));
                link.download = 'Exos-SN05.zip';
                document.body.appendChild(link);
                link.click();
            }
            """));
        try
        {
            string? path = await download.PathAsync();
            if (path is null || new FileInfo(path).Length != 1048576 || await download.FailureAsync() is not null)
                throw new Exception("Telemetry interfered with downloading.");
            for (int i = 0; i < 20 && progress.Percentage != 100; i++) await Task.Delay(100);
            if (progress.Filename != "Exos-SN05.zip" || progress.ReceivedBytes != 1048576 || progress.Percentage != 100)
                throw new Exception($"Chromium events were not observed: {progress.Filename}, {progress.ReceivedBytes}, {progress.Percentage}");
        }
        finally { await download.DeleteAsync(); }
    }
}
