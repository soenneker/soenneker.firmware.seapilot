using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Soenneker.Firmware.SeaPilot.Abstract;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Firmware.SeaPilot.Options;
using Soenneker.OpenSeaChest.Util.Abstract;
using Soenneker.OpenSeaChest.Util.Dtos;
using System.Linq;
using Soenneker.Locks.Files;
using Soenneker.Playwrights.Extensions.Stealth;
using Soenneker.Playwrights.Installation.Abstract;

namespace Soenneker.Firmware.SeaPilot;

public sealed class SeaPilot(
    IOpenSeaChestUtil openSeaChest,
    IPlaywrightInstallationUtil installer,
    IOptions<SeaPilotOptions> options,
    FirmwareFileStore fileStore,
    FirmwareCache cache,
    FirmwarePackageResolver packages,
    FirmwareUpdateWriter writer,
    FirmwareUpdateLock updateLock,
    ILogger<SeaPilot> logger) : ISeaPilot
{

    public async ValueTask<IReadOnlyList<FirmwareUpdateResult>> UpdateAllDriveFirmwares(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        logger.LogInformation("Waiting for exclusive firmware update access.");
        using FileLockLease lease = await updateLock.Acquire(cancellationToken);
        logger.LogInformation("Discovering attached drives.");
        List<OpenSeaChestDrive> discovered = await openSeaChest.GetDrives(cancellationToken);
        var results = new List<FirmwareUpdateResult>(discovered.Count);
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new Dictionary<string, FirmwareImageSelection>(StringComparer.Ordinal);
        logger.LogInformation("Discovered {Count} drives. Preparing all applicable firmware before any writes.", discovered.Count);
        foreach (OpenSeaChestDrive drive in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new FirmwareUpdateResult { Drive = drive };
            if (drive.DiscoveryError is not null)
            {
                results.Add(result with { SkipReason = "Device information unavailable: " + drive.DiscoveryError.GetBaseException().Message });
                continue;
            }
            try
            {
                logger.LogInformation("Checking {Device}: {Model}, serial {Serial}, firmware {Revision}.",
                    drive.Handle, drive.ModelNumber, drive.SerialNumber, drive.FirmwareRevision);
                if (!IsSeagateDrive(drive))
                    result = result with { SkipReason = "Not an identified Seagate drive with serial, model and firmware revision." };
                else if (!serials.Add(drive.SerialNumber!))
                    result = result with { SkipReason = "Serial already processed through another device handle." };
                else
                {
                    // Applicability belongs to this serial, never to a model group or a cached package.
                    // Only downloaded bytes and extracted images are shared after this fresh lookup.
                    FirmwareLookup lookup = await FindFirmware(drive.SerialNumber!, cancellationToken);
                    ValidateLookupIdentity(drive, lookup);
                    if (!lookup.IsRecognized || lookup.Releases.Count == 0)
                        result = result with { SkipReason = !lookup.IsRecognized ? "Serial not recognized by Seagate." :
                            "No firmware offered; this does not establish that installed firmware is current." };
                    else
                    {
                        if (lookup.Releases.Count != 1)
                            throw new InvalidDataException("Multiple offered packages cannot be selected automatically.");
                        FirmwareRelease release = lookup.Releases[0];
                        // A direct versioned ZIP URL can establish that no download is needed. Opaque URLs require the package metadata.
                        string? offeredRevision = null;
                        if (release.DownloadUri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            try { offeredRevision = FirmwarePackageResolver.GetRevision(release.DownloadUri.AbsolutePath); }
                            catch (InvalidDataException) { }
                        }
                        if (offeredRevision is not null && !FirmwarePackageResolver.NeedsUpdate(drive.FirmwareRevision!, offeredRevision))
                            result = result with { SkipReason = "Installed firmware is the offered revision or newer." };
                        else
                        {
                            FirmwareDownload package = await DownloadFirmwareCore(release, null, true, cancellationToken, drive.FirmwareRevision);
                            result = result with { Package = package };
                            if (!images.TryGetValue(package.Sha256, out FirmwareImageSelection? image))
                            {
                                image = await packages.Prepare(package, cancellationToken);
                                images.Add(package.Sha256, image);
                            }
                            result = result with { Image = image };
                            logger.LogInformation("{Device}: installed {Installed}; package target {Target}.", drive.Handle, drive.FirmwareRevision, image.TargetRevision);
                            FirmwarePackageResolver.ValidateSectorSize(drive, image.FilePath);
                            if (!FirmwarePackageResolver.NeedsUpdate(drive.FirmwareRevision!, image.TargetRevision))
                                result = result with { SkipReason = "Installed firmware is the offered revision or newer." };
                            else
                                logger.LogInformation("Prepared {Revision} for {Device} using shared image {Path}.", image.TargetRevision, drive.Handle, image.FilePath);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { result = result with { Error = exception }; }
            results.Add(result);
            if (result.Error is not null) logger.LogError(result.Error, "Could not prepare firmware for {Device}.", drive.Handle);
            else if (result.SkipReason is not null) logger.LogInformation("Skipped {Device}: {Reason}", drive.Handle, result.SkipReason);
        }

        logger.LogInformation("Preparation complete. Updating {Count} drives sequentially.",
            results.Count(result => result.Image is not null && result.Error is null && result.SkipReason is null));
        return await writer.WriteAll(results,
            token => openSeaChest.GetDrives(token).AsTask(),
            (device, image, token) => openSeaChest.UpgradeFirmware(device, image, token).AsTask(), cancellationToken);
    }

    public async ValueTask<FirmwareVerification> VerifyFirmware(string serialNumber, string targetRevision,
        CancellationToken cancellationToken = default)
    {
        string serial = ValidateSerial(serialNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetRevision);
        return FirmwareUpdateWriter.Verify(serial, targetRevision.Trim(), await openSeaChest.GetDrives(cancellationToken));
    }

    public static bool IsSeagateDrive(OpenSeaChestDrive drive) =>
        drive.DiscoveryError is null && !string.IsNullOrWhiteSpace(drive.SerialNumber) && !string.IsNullOrWhiteSpace(drive.FirmwareRevision) &&
        !string.IsNullOrWhiteSpace(drive.ModelNumber) &&
        (string.Equals(drive.Vendor?.Trim(), "Seagate", StringComparison.OrdinalIgnoreCase) ||
         drive.ModelNumber.StartsWith("ST", StringComparison.OrdinalIgnoreCase));

    public static void ValidateLookupIdentity(OpenSeaChestDrive drive, FirmwareLookup lookup)
    {
        if (!string.Equals(drive.SerialNumber, lookup.SerialNumber, StringComparison.OrdinalIgnoreCase) ||
            lookup.Releases.Any(release => !string.Equals(release.SerialNumber, drive.SerialNumber, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Firmware lookup or offering belongs to a different drive serial.");
        if (lookup.IsRecognized && lookup.Releases.Count > 0 &&
            (lookup.ModelNumber is null || drive.ModelNumber is null ||
             (!drive.ModelNumber.Equals(lookup.ModelNumber, StringComparison.OrdinalIgnoreCase) &&
              !drive.ModelNumber.StartsWith(lookup.ModelNumber + "-", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Local model does not match Seagate's serial-specific offering.");
    }

    public static void VerifyDeviceIdentity(OpenSeaChestDrive original, IReadOnlyList<OpenSeaChestDrive> current)
    {
        OpenSeaChestDrive? drive = current.SingleOrDefault(d => string.Equals(d.Handle, original.Handle,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (drive is null || drive.DiscoveryError is not null || !string.Equals(drive.SerialNumber, original.SerialNumber, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(drive.ModelNumber, original.ModelNumber, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(drive.FirmwareRevision, original.FirmwareRevision, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Device disappeared or its identity/revision changed before flashing.");
    }

    public async ValueTask<FirmwareLookup> FindFirmware(string serialNumber,
        CancellationToken cancellationToken = default)
    {
        string serial = ValidateSerial(serialNumber);
        logger.LogInformation("Looking up Seagate firmware for serial {Serial}.", serial);
        return await WithBrowser(async (page, token) =>
        {
            IResponse? response = await page.GotoAsync("https://www.seagate.com/support/downloads/",
                new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            if (response is null || !response.Ok)
                throw new IOException($"Seagate support page returned HTTP {response?.Status}.");
            // The site's serialNumberSearch uses this endpoint. Fetch within the browser's origin and cookie context.
            var json = await page.EvaluateAsync<string>("""
                                                        async serial => {
                                                            const response = await fetch('/ww/serialNumber?sn=' + encodeURIComponent(serial) + '&langId=en_US&callType=post');
                                                            if (!response.ok) throw new Error('Seagate firmware lookup HTTP ' + response.status);
                                                            return await response.text();
                                                        }
                                                        """, serial);
            FirmwareLookup lookup = SeagateResponseParser.Parse(json, serial);
            logger.LogInformation("Lookup complete for {Serial}: recognized {Recognized}, {PackageCount} firmware packages offered.", serial, lookup.IsRecognized, lookup.Releases.Count);
            return lookup;
        }, options.Value.LookupTimeout, cancellationToken);
    }

    public async ValueTask<FirmwareLookup> FindFirmwareForDevice(string device,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(device);
        if (device.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) || device.IndexOfAny(['*', '?']) >= 0)
            throw new ArgumentException("Select one device handle.", nameof(device));
        cancellationToken.ThrowIfCancellationRequested();
        OpenSeaChestDrive drive = (await openSeaChest.GetDrives(cancellationToken)).SingleOrDefault(d =>
                                      d.Handle.Equals(device,
                                          OperatingSystem.IsWindows()
                                              ? StringComparison.OrdinalIgnoreCase
                                              : StringComparison.Ordinal)) ??
                                  throw new InvalidOperationException(
                                      "OpenSeaChest did not discover the requested device handle.");
        if (drive.DiscoveryError is not null)
            throw new InvalidOperationException("The requested device could not be read.", drive.DiscoveryError);
        if (string.IsNullOrWhiteSpace(drive.SerialNumber) || string.IsNullOrWhiteSpace(drive.ModelNumber))
            throw new InvalidDataException("OpenSeaChest did not return a serial and model for this device.");
        FirmwareLookup result = await FindFirmware(drive.SerialNumber, cancellationToken);
        string model = drive.ModelNumber;
        if (result.ModelNumber is not null && !model.Equals(result.ModelNumber, StringComparison.OrdinalIgnoreCase) &&
            !model.StartsWith(result.ModelNumber + "-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local device model does not match Seagate's serial lookup.");
        return result with
        {
            Device = drive.Handle, InstalledRevision = drive.FirmwareRevision,
            ModelNumber = result.ModelNumber ?? model
        };
    }

    public async ValueTask<IReadOnlyList<FirmwareDownload>> GetCachedFirmware(string serialNumber,
        CancellationToken cancellationToken = default)
    {
        string serial = ValidateSerial(serialNumber);
        logger.LogInformation("Checking cached firmware for {Serial} and verifying checksums.", serial);
        IReadOnlyList<FirmwareDownload> downloads = await cache.GetAll(serial, cancellationToken);
        logger.LogInformation("Found {PackageCount} verified cached packages for {Serial}.", downloads.Count, serial);
        return downloads;
    }

    public async ValueTask<FirmwareDownload> DownloadLatestFirmware(string serialNumber, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        FirmwareLookup lookup = await FindFirmware(serialNumber, cancellationToken);
        if (!lookup.IsRecognized)
            throw new InvalidOperationException("Seagate did not recognize this serial number.");
        if (lookup.Releases.Count == 0)
            throw new InvalidOperationException(
                "Seagate currently offers no firmware download for this serial number.");
        if (lookup.Releases.Count != 1)
            throw new InvalidOperationException(
                "Seagate offers multiple packages. Select a release from FindFirmware and call DownloadFirmware.");
        return await DownloadFirmwareCore(lookup.Releases[0], destinationDirectory, true, cancellationToken);
    }

    public ValueTask<FirmwareDownload> DownloadFirmware(FirmwareRelease firmware, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        return DownloadFirmwareCore(firmware, destinationDirectory, false, cancellationToken);
    }

    private async ValueTask<FirmwareDownload> DownloadFirmwareCore(FirmwareRelease firmware, string? destinationDirectory,
        bool refresh, CancellationToken cancellationToken, string? installedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(firmware);
        ArgumentNullException.ThrowIfNull(firmware.DownloadUri);
        SeagateResponseParser.ValidateUri(firmware.DownloadUri);
        ValidateSerial(firmware.SerialNumber);
        string? directory = destinationDirectory is null ? null : Path.GetFullPath(destinationDirectory);
        logger.LogInformation("Preparing {Title} for {Serial}: {Revision}. Destination: {Directory}.", firmware.Title,
            firmware.SerialNumber, FirmwareDownloadProgress.DescribeRevision(firmware.DownloadUri.AbsolutePath, installedRevision),
            directory ?? options.Value.CacheDirectory);
        FirmwareDownload? existing = refresh ? null : await cache.Find(firmware, null, cancellationToken);
        if (existing is not null)
        {
            logger.LogInformation("Reusing verified cached package {Path}.", existing.FilePath);
            return await fileStore.Save(existing.FilePath, Path.GetFileName(existing.FilePath), directory!, firmware,
                options.Value.MaximumDownloadBytes, cancellationToken);
        }
        logger.LogInformation("Requesting {Title} from Seagate for {Serial}; timeout is {Timeout}.", firmware.Title, firmware.SerialNumber, options.Value.DownloadTimeout);
        return await WithBrowser(async (page, token) =>
        {
            await using FirmwareDownloadProgress progress = await FirmwareDownloadProgress.Attach(page, logger, firmware.SerialNumber, installedRevision);
            IDownload download = await page.RunAndWaitForDownloadAsync(async () =>
            {
                // Chromium aborts navigation when it hands a response to the download manager.
                try
                {
                    IResponse? response = await page.GotoAsync(firmware.DownloadUri.AbsoluteUri,
                        new() { WaitUntil = WaitUntilState.Commit });
                    if (response is not null)
                        throw new IOException(
                            $"Seagate returned a web page (HTTP {response.Status}) instead of a firmware download. The link may have expired or require user interaction.");
                }
                catch (PlaywrightException exception) when
                    (exception.Message.Contains("ERR_ABORTED", StringComparison.Ordinal) ||
                     exception.Message.Contains("Download is starting", StringComparison.Ordinal))
                {
                }
            });
            try
            {
                SeagateResponseParser.ValidateUri(new Uri(download.Url));
                logger.LogInformation("Receiving {Filename} for {Serial}: {Revision}.", download.SuggestedFilename,
                    firmware.SerialNumber, FirmwareDownloadProgress.DescribeRevision(download.SuggestedFilename, installedRevision));
                string? path = await download.PathAsync();
                string? failure = await download.FailureAsync();
                if (failure is not null || path is null)
                    throw new IOException("Seagate download failed: " + failure);
                logger.LogInformation("Download complete. Validating and caching {Filename}.", download.SuggestedFilename);
                FirmwareDownload stored = await cache.Store(path, download.SuggestedFilename, firmware, token);
                if (directory is null) return stored;
                FirmwareDownload saved = await fileStore.Save(stored.FilePath, download.SuggestedFilename, directory, firmware,
                    options.Value.MaximumDownloadBytes, token);
                logger.LogInformation("Firmware saved to {Path} ({Bytes} bytes).", saved.FilePath, saved.Length);
                return saved;
            }
            finally
            {
                await download.DeleteAsync();
            }
        }, options.Value.DownloadTimeout, cancellationToken);
    }

    private async ValueTask<T> WithBrowser<T>(Func<IPage, CancellationToken, Task<T>> action, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue ||
            options.Value.MaximumDownloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Timeouts and download size must be positive; timeouts must fit in milliseconds.");
        var launch = new BrowserTypeLaunchOptions
            { Headless = options.Value.Headless, Channel = "chromium", Timeout = (float)timeout.TotalMilliseconds };
        logger.LogInformation("Preparing Chromium; the first run may download the browser.");
        Task installation = installer.EnsureInstalled(launch, cancellationToken).AsTask();
        await ReportWaiting(installation, "Browser installation");
        logger.LogInformation("Starting browser session.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.LaunchStealthChromium(launch);
        deadline.Token.ThrowIfCancellationRequested();
        await using IBrowserContext context = await browser.CreateStealthContext(new BrowserNewContextOptions
            { AcceptDownloads = true, Locale = "en-US" });
        context.SetDefaultTimeout((float)timeout.TotalMilliseconds);
        IPage page = await context.NewPageAsync();
        Task<T> operation = action(page, deadline.Token);
        try
        {
            Task<T> pending = operation.WaitAsync(deadline.Token);
            await ReportWaiting(pending, "Seagate browser operation");
            return await pending;
        }
        catch (OperationCanceledException)
        {
            // Stop native browser work, then drain the task so no file operations outlive this call.
            await context.CloseAsync();
            try
            {
                await operation;
            }
            catch (Exception)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("The Seagate operation exceeded its configured timeout.");
        }
    }

    private async Task ReportWaiting(Task operation, string stage)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!operation.IsCompleted)
        {
            try { await operation.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) when (!operation.IsCompleted)
            {
                logger.LogInformation("{Stage} is still running ({ElapsedSeconds:0} seconds elapsed).", stage, elapsed.Elapsed.TotalSeconds);
            }
        }
        await operation;
    }

    private static string ValidateSerial(string serialNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        string serial = serialNumber.Trim();
        if (serial.Length > 64 || !System.Linq.Enumerable.All(serial, char.IsAsciiLetterOrDigit))
            throw new ArgumentException("A serial number must contain only letters and digits (maximum 64).",
                nameof(serialNumber));
        return serial;
    }
}


