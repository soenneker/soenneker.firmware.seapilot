using Microsoft.Extensions.Logging;
using Soenneker.Firmware.SeaPilot.Abstract;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.OpenSeaChest.Util.Abstract;
using Soenneker.OpenSeaChest.Util.Dtos;

namespace Soenneker.Firmware.SeaPilot.Cli;

internal sealed class SeaPilotCommand(ISeaPilot pilot, IOpenSeaChestUtil openSeaChest, ILogger<SeaPilotCommand> logger)
{
    public async Task<int> Run(string[] args, CancellationToken token)
    {
        switch (args[0])
        {
            case "drives":
                logger.LogInformation("Discovering attached drives.");
                List<OpenSeaChestDrive> drives = await openSeaChest.GetDrives(token);
                if (drives.Count == 0) logger.LogInformation("No drives discovered. Native device access may require administrator/root permissions.");
                foreach (OpenSeaChestDrive drive in drives)
                    Console.WriteLine($"{drive.Handle}\t{drive.ModelNumber}\t{drive.SerialNumber}\t{drive.FirmwareRevision}");
                break;
            case "lookup":
                FirmwareLookup lookup = await pilot.FindFirmware(args[1], token);
                Console.WriteLine($"Serial: {lookup.SerialNumber}; recognized: {lookup.IsRecognized}; model: {lookup.ModelNumber}");
                foreach (FirmwareRelease release in lookup.Releases) Console.WriteLine($"{release.Title}\n{release.DownloadUri}");
                if (lookup.Releases.Count == 0) logger.LogInformation("No firmware offered. This does not establish that installed firmware is current.");
                return lookup.IsRecognized ? 0 : 1;
            case "download":
                PrintDownload(await pilot.DownloadLatestFirmware(args[1], args[2], token));
                break;
            case "cached":
                IReadOnlyList<FirmwareDownload> cached = await pilot.GetCachedFirmware(args[1], token);
                if (cached.Count == 0) logger.LogInformation("No verified firmware packages cached for this serial.");
                foreach (FirmwareDownload download in cached) PrintDownload(download);
                break;
            case "verify":
                FirmwareVerification verification = await pilot.VerifyFirmware(args[1], args[2], token);
                Console.WriteLine($"{verification.SerialNumber}: active {verification.ObservedRevision}; target {verification.TargetRevision}; verified active: {verification.IsActive}");
                return verification.IsActive ? 0 : 1;
            case "update-all":
                IReadOnlyList<FirmwareUpdateResult> results = await pilot.UpdateAllDriveFirmwares(token);
                foreach (FirmwareUpdateResult result in results)
                {
                    Console.WriteLine($"{result.Drive.Handle} ({result.Drive.SerialNumber}): {result.Error?.Message ?? result.SkipReason ?? (result.Verification?.IsActive == true ? "Target firmware verified active." : "Write completed; activation unverified. Check native instructions and run verify after any required power cycle.")}");
                    if (result.VerificationError is not null) Console.WriteLine($"Verification failed: {result.VerificationError.Message}");
                    if (result.NativeOutput is not null)
                        foreach (string line in result.NativeOutput) Console.WriteLine(line);
                }
                return results.Any(result => result.Error is not null || result.VerificationError is not null) ? 1 : 0;
        }
        return 0;
    }

    private static void PrintDownload(FirmwareDownload download) =>
        Console.WriteLine($"{download.FilePath}\n{download.Length} bytes; SHA256 {download.Sha256}");

}

