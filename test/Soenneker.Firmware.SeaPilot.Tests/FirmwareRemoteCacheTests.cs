using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class FirmwareRemoteCacheTests(Host host) : HostedUnitTest(host)
{
    [Test]
    public async Task ValidatorReusesAcrossSerialsButRejectsChangedResourcesAndCorruptBytes()
    {
        var cache = Resolve<FirmwareCache>(true);
        var first = new FirmwareRelease { SerialNumber = "FIRST" + Random.Shared.NextInt64(1, long.MaxValue),
            Title = "Firmware", DownloadUri = new Uri("https://www.seagate.com/firmware-SN06.bin") };
        var second = first with { SerialNumber = first.SerialNumber + "SECOND" };
        var identity = new FirmwareRemoteIdentity { Url = first.DownloadUri.AbsoluteUri, ETag = "\"unique-" + Guid.NewGuid() + "\"", Length = 4 };
        string source = Path.Combine(Path.GetTempPath(), $"firmware-{Guid.NewGuid()}.bin");
        try
        {
            await File.WriteAllBytesAsync(source, [1, 2, 3, 4]);
            FirmwareDownload stored = await cache.Store(source, "firmware-SN06.bin", first, default, identity);
            if ((await cache.FindValidated(second, identity, default))?.Sha256 != stored.Sha256)
                throw new Exception("Verified package was not shared across serials.");
            foreach (FirmwareRemoteIdentity changed in new[] { identity with { ETag = "\"different\"" }, identity with { Length = 5 }, identity with { Url = "https://www.seagate.com/other.bin" } })
                if (await cache.FindValidated(second, changed, default) is not null) throw new Exception("Mismatched resource reused cached bytes.");
            await File.WriteAllBytesAsync(stored.FilePath, [5, 6, 7, 8]);
            if (await cache.FindValidated(second, identity, default) is not null) throw new Exception("Corrupt package reused.");
        }
        finally { File.Delete(source); }
    }

    [Test]
    public void WeakOrMissingValidatorsAreNotIdentities()
    {
        foreach (string tag in new[] { "W/\"weak\"", "", "unquoted" })
            if (FirmwareRemoteIdentity.FromHeaders("https://www.seagate.com/package", new Dictionary<string, string> { ["ETag"] = tag, ["Content-Length"] = "4" }) is not null)
                throw new Exception("Unsafe validator accepted.");
    }

    [Test]
    public async Task BrowserTransferRecordsValidatorAndHeadTransfersNoBody()
    {
        using IPlaywright playwright = await Playwright.CreateAsync();
        Skip.Unless(File.Exists(playwright.Chromium.ExecutablePath), "Install Chromium to run download integration tests.");
        await using var server = new LocalFirmwareServer();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, Channel = "chromium" });
        await using IBrowserContext context = await browser.NewContextAsync(new() { AcceptDownloads = true });
        IPage page = await context.NewPageAsync();
        await using FirmwareResponseObserver observer = await FirmwareResponseObserver.Attach(page);
        IDownload download = await page.RunAndWaitForDownloadAsync(async () =>
        {
            try { await page.GotoAsync(server.Url); }
            catch (PlaywrightException e) when (e.Message.Contains("ERR_ABORTED") || e.Message.Contains("Download is starting")) { }
        });
        try
        {
            string? path = await download.PathAsync();
            if (path is null || new FileInfo(path).Length != 4) throw new Exception("Download failed.");
            FirmwareRemoteIdentity? actual = observer.Find(download.Url);
            if (actual is null) throw new Exception("No validator captured from browser transfer.");
            var cache = Resolve<FirmwareCache>(true);
            var firmware = new FirmwareRelease { SerialNumber = "BROWSER" + Random.Shared.NextInt64(1, long.MaxValue),
                Title = "Browser fixture", DownloadUri = new Uri("https://www.seagate.com/fixture-SN06.bin") };
            FirmwareDownload saved = await cache.Store(path, "fixture-SN06.bin", firmware, default, actual);
            IAPIResponse head = await context.APIRequest.HeadAsync(server.Url);
            try
            {
                FirmwareRemoteIdentity? revalidated = FirmwareRemoteIdentity.FromHeaders(head.Url, head.Headers);
                if (revalidated != actual || server.Downloads != 1)
                    throw new Exception("Metadata request fetched body or identified different bytes.");
                FirmwareDownload? reused = await cache.FindValidated(firmware with { SerialNumber = firmware.SerialNumber + "SECOND" }, revalidated!, default);
                if (reused?.FilePath != saved.FilePath || server.Downloads != 1)
                    throw new Exception("Second serial did not reuse the first transfer.");
            }
            finally { await head.DisposeAsync(); }
        }
        finally { await download.DeleteAsync(); }
    }
}
