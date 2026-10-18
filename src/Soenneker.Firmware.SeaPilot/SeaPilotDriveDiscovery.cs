using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.OpenSeaChest.Util;
using Soenneker.OpenSeaChest.Util.Abstract;
using Soenneker.OpenSeaChest.Util.Dtos;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Discovers devices independently so an inaccessible handle cannot hide other drives.</summary>
public sealed class SeaPilotDriveDiscovery(IOpenSeaChestUtil native, ILogger<SeaPilotDriveDiscovery> logger)
{
    /// <summary>Scans and reads each device independently. Scan failures and caller cancellation propagate; detail failures are retained per device.</summary>
    public Task<List<DriveDiscoveryResult>> Discover(CancellationToken token = default) => Discover(
        cancellation => native.Scan(cancellation).AsTask(),
        (handle, cancellation) => native.Execute(OpenSeaChestTool.Info,
            ["--device", handle, "--deviceInfo", "--llInfo"], cancellation).AsTask(), token);

    /// <summary>Returns all handles, retaining failed devices with empty identity so they cannot pass firmware eligibility or identity checks.</summary>
    public async Task<List<OpenSeaChestDrive>> GetDrives(CancellationToken token = default) =>
        (await Discover(token)).Select(result => result.Drive).ToList();

    /// <summary>Runs discovery through native scan/detail adapters. Supports adapter-boundary testing without hardware access.</summary>
    public async Task<List<DriveDiscoveryResult>> Discover(Func<CancellationToken, Task<List<string>>> scan,
        Func<string, CancellationToken, Task<List<string>>> read, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        List<string> output = await scan(token);
        var results = new List<DriveDiscoveryResult>();
        foreach ((string handle, string? vendor) in DriveDiscoveryParser.ParseScan(output))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                List<string> info = await read(handle, token);
                token.ThrowIfCancellationRequested();
                results.Add(new DriveDiscoveryResult { Drive = DriveDiscoveryParser.ParseDevice(handle, vendor, info) });
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogWarning("Skipping unavailable device {Device}: {Reason}", handle, exception.GetBaseException().Message);
                results.Add(new DriveDiscoveryResult
                {
                    Drive = new OpenSeaChestDrive { Handle = handle, Vendor = vendor,
                        Details = new Dictionary<string, string>(), RawDeviceInfo = [] },
                    Error = exception
                });
            }
        }
        token.ThrowIfCancellationRequested();
        return results;
    }
}
