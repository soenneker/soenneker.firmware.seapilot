using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Firmware.SeaPilot.Options;
using Soenneker.OpenSeaChest.Util.Dtos;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Executes prepared updates sequentially against native discovery/write adapters.</summary>
public sealed class FirmwareUpdateWriter(FirmwareFileStore files, IOptions<SeaPilotOptions> options,
    ILogger<FirmwareUpdateWriter> logger)
{
    /// <summary>Checks image and device identity before each write. Active writes and their verification complete before cancellation is observed.</summary>
    public async Task<IReadOnlyList<FirmwareUpdateResult>> WriteAll(List<FirmwareUpdateResult> results,
        Func<CancellationToken, Task<List<OpenSeaChestDrive>>> discover,
        Func<string, string, CancellationToken, Task<List<string>>> write, CancellationToken token)
    {
        for (int i = 0; i < results.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            FirmwareUpdateResult result = results[i];
            if (result.Image is null || result.Error is not null || result.SkipReason is not null) continue;
            try
            {
                FirmwareImageSelection image = result.Image;
                FirmwareDownload verified = await files.Inspect(image.FilePath, Path.GetFileName(image.FilePath),
                    result.Package!.Firmware, options.Value.MaximumDownloadBytes, token);
                if (verified.Sha256 != image.Sha256) throw new InvalidDataException("Prepared firmware image checksum changed.");
                SeaPilot.VerifyDeviceIdentity(result.Drive, await discover(token));
                token.ThrowIfCancellationRequested();
                result = result with { FlashAttempted = true };
                logger.LogInformation("Writing {Revision} to {Device}. Keep the drive powered; cancellation waits for this write.", image.TargetRevision, result.Drive.Handle);
                Task<List<string>> pending = write(result.Drive.Handle, image.FilePath, CancellationToken.None);
                while (!pending.IsCompleted)
                {
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (TimeoutException) when (!pending.IsCompleted)
                    { logger.LogInformation("Firmware write to {Device} is still running.", result.Drive.Handle); }
                }
                result = result with { NativeOutput = await pending, WriteCompleted = true };
                foreach (string line in result.NativeOutput) logger.LogInformation("{Device}: {Output}", result.Drive.Handle, line);
                try
                {
                    FirmwareVerification verification = Verify(result.Drive.SerialNumber!, image.TargetRevision,
                        await discover(CancellationToken.None), result.Drive.ModelNumber);
                    result = result with { Verification = verification };
                    logger.LogInformation("{Device}: active firmware {Observed}; target {Target}; verified active: {Active}.",
                        result.Drive.Handle, verification.ObservedRevision, verification.TargetRevision, verification.IsActive);
                }
                catch (Exception exception)
                {
                    result = result with { VerificationError = exception };
                    logger.LogWarning(exception, "Write completed for {Device}, but activation could not be verified.", result.Drive.Handle);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                result = result with { Error = exception };
                logger.LogError(exception, "Firmware update failed for {Device}. Continuing with remaining drives.", result.Drive.Handle);
            }
            results[i] = result;
        }
        token.ThrowIfCancellationRequested();
        return results.AsReadOnly();
    }

    /// <summary>Matches by serial even after device handles change. Conflicting identities or revisions fail verification.</summary>
    public static FirmwareVerification Verify(string serial, string target, IReadOnlyList<OpenSeaChestDrive> drives, string? model = null)
    {
        OpenSeaChestDrive[] matches = drives.Where(d => string.Equals(d.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0 || matches.Any(d => d.DiscoveryError is not null || string.IsNullOrWhiteSpace(d.FirmwareRevision) ||
            string.IsNullOrWhiteSpace(d.ModelNumber) ||
            !string.Equals(d.ModelNumber, model ?? matches[0].ModelNumber, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(d.FirmwareRevision, matches[0].FirmwareRevision, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Drive is missing or its identity/revision cannot be verified.");
        return new FirmwareVerification { SerialNumber = serial, TargetRevision = target, ObservedRevision = matches[0].FirmwareRevision! };
    }
}
