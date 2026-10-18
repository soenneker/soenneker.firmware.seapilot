using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Firmware.SeaPilot.Abstract;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Tests.HostedUnit;

namespace Soenneker.Firmware.SeaPilot.Tests;

[ClassDataSource<Host>(Shared = SharedType.PerTestSession)]
public sealed class SeaPilotTests(Host host) : HostedUnitTest(host)
{
    [Test]
    public async Task CancellationDoesNotStartBrowser()
    {
        var util = Resolve<ISeaPilot>(true);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        try { await util.FindFirmware("EXAMPLE", cancellation.Token); }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected cancellation.");
    }

    [Test]
    public async Task RejectsInvalidSerialBeforeBrowserLaunch()
    {
        var util = Resolve<ISeaPilot>(true);
        try { await util.FindFirmware("bad&sn=other"); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected serial validation.");
    }

    [Test]
    public async Task RejectsWildcardDeviceBeforeNativeExecution()
    {
        var util = Resolve<ISeaPilot>(true);
        try { await util.FindFirmwareForDevice("all"); }
        catch (ArgumentException) { return; }
        throw new Exception("Expected device validation.");
    }

    [Test]
    public async Task LiveSerialLookup()
    {
        string? serial = Environment.GetEnvironmentVariable("SEAPILOT_TEST_SERIAL");
        Skip.Unless(!string.IsNullOrWhiteSpace(serial), "Set SEAPILOT_TEST_SERIAL to enable live lookup.");
        FirmwareLookup result = await Resolve<ISeaPilot>(true).FindFirmware(serial!);
        if (!result.IsRecognized || result.ModelNumber is null) throw new Exception("Expected a recognized serial.");
        Console.WriteLine($"Model: {result.ModelNumber}; Packages: {result.Releases.Count}");
    }

    [Test]
    public async Task LiveFirmwareDownload()
    {
        string? serial = Environment.GetEnvironmentVariable("SEAPILOT_TEST_DOWNLOAD_SERIAL");
        Skip.Unless(!string.IsNullOrWhiteSpace(serial), "Set SEAPILOT_TEST_DOWNLOAD_SERIAL to a serial with a current firmware offering.");
        var util = Resolve<ISeaPilot>(true);
        FirmwareLookup lookup = await util.FindFirmware(serial!);
        Console.WriteLine($"Recognized: {lookup.IsRecognized}; Model: {lookup.ModelNumber}; Packages: {lookup.Releases.Count}");
        if (!lookup.IsRecognized || lookup.Releases.Count == 0) throw new Exception("Expected firmware for the supplied live test serial.");
        foreach (FirmwareRelease release in lookup.Releases) Console.WriteLine($"{release.Title}: {release.DownloadUri}");
        string directory = Environment.GetEnvironmentVariable("SEAPILOT_TEST_DOWNLOAD_DIRECTORY") ?? Path.Combine(Path.GetTempPath(), $"seapilot-{Guid.NewGuid()}");
        FirmwareDownload download = await util.DownloadLatestFirmware(serial!, directory);
        if (!File.Exists(download.FilePath) || download.Length <= 0 || download.Sha256.Length != 64) throw new Exception("Incomplete download.");
        Console.WriteLine($"Downloaded: {download.FilePath}\nBytes: {download.Length}\nSHA256: {download.Sha256}");
    }

    [Test]
    public async Task LiveUnknownSerialIsNotAnUpToDateDrive()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("SEAPILOT_TEST_SERIAL") is not null, "Live Seagate test is opt-in.");
        FirmwareLookup result = await Resolve<ISeaPilot>(true).FindFirmware("INVALID123");
        if (result.IsRecognized || result.Releases.Count != 0) throw new Exception("Unknown serial was misclassified.");
    }

    [Test]
    public async Task LiveWebPageIsRejectedInsteadOfDownloaded()
    {
        Skip.Unless(Environment.GetEnvironmentVariable("SEAPILOT_TEST_SERIAL") is not null, "Live Seagate test is opt-in.");
        var release = new FirmwareRelease { SerialNumber = "EXAMPLE", Title = "Web page", DownloadUri = new Uri("https://www.seagate.com/support/downloads/") };
        string destination = Path.Combine(Path.GetTempPath(), $"seapilot-test-{Guid.NewGuid()}");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await Resolve<ISeaPilot>(true).DownloadFirmware(release, destination, deadline.Token); }
        catch (IOException exception) when (exception.Message.Contains("instead of a firmware download", StringComparison.Ordinal))
        {
            if (Directory.Exists(destination)) throw new Exception("A web page created download output.");
            return;
        }
        throw new Exception("Expected immediate rejection of the web page.");
    }

    [Test]
    public async Task LiveOfficialPackageCacheReplay()
    {
        string? url = Environment.GetEnvironmentVariable("SEAPILOT_TEST_FIRMWARE_URL");
        Skip.Unless(url is not null, "Set SEAPILOT_TEST_FIRMWARE_URL to enable an official package download and cache replay.");
        var release = new FirmwareRelease { SerialNumber = "LIVEFIXTURE", Title = "Official Seagate download verification fixture", DownloadUri = new Uri(url!) };
        var pilot = Resolve<ISeaPilot>(true);
        string root = Environment.GetEnvironmentVariable("SEAPILOT_TEST_DOWNLOAD_DIRECTORY") ?? Path.Combine(Path.GetTempPath(), $"seapilot-live-{Guid.NewGuid()}");
        FirmwareDownload first = await pilot.DownloadFirmware(release, Path.Combine(root, "first"));
        FirmwareDownload replay = await pilot.DownloadFirmware(release, Path.Combine(root, "replay"));
        if (first.Sha256 != replay.Sha256 || first.Length != replay.Length || (await pilot.GetCachedFirmware(release.SerialNumber)).Count == 0)
            throw new Exception("Live download did not survive a cache round trip.");
        Console.WriteLine($"Verified: {first.FilePath}\nBytes: {first.Length}\nSHA256: {first.Sha256}");
    }

    [Test]
    public async Task LiveDownloadCancellationLeavesNoPartialOutput()
    {
        string? serial = Environment.GetEnvironmentVariable("SEAPILOT_TEST_DOWNLOAD_SERIAL");
        Skip.Unless(!string.IsNullOrWhiteSpace(serial), "Live firmware test is opt-in.");
        var util = Resolve<ISeaPilot>(true);
        FirmwareLookup lookup = await util.FindFirmware(serial!);
        if (lookup.Releases.Count == 0) throw new Exception("No live firmware offering available.");
        string destination = Path.Combine(Path.GetTempPath(), $"seapilot-test-{Guid.NewGuid()}");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await util.DownloadFirmware(lookup.Releases[0], destination, cancellation.Token); }
        catch (OperationCanceledException)
        {
            if (Directory.Exists(destination) && Directory.GetFiles(destination).Length != 0)
                throw new Exception("Cancellation left partial output.");
            return;
        }
        throw new Exception("Expected cancellation of the live download.");
    }
}
