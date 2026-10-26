using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Soenneker.Firmware.SeaPilot.Dtos;

namespace Soenneker.Firmware.SeaPilot.Abstract;

/// <summary>
/// Seagate firmware discovery and downloads using OpenSeaChest and stealth browser automation.
/// </summary>
public interface ISeaPilot
{
    /// <summary>Discovers all drives, independently queries Seagate for every unique drive serial on every call, prepares shared cached firmware packages, then automatically updates eligible drives sequentially.</summary>
    /// <remarks>Firmware applicability is never inferred from matching models or another drive's lookup. Each current offering is revalidated against the exact resource URL, strong server ETag and size before checksum-verified cached content is shared; unavailable validators require a fresh download. Duplicate handles for the same serial are processed once.</remarks>
    /// <remarks>Selects the newest offered revision in the installed firmware branch, then uses the primary image and revision in its serial-specific Seagate ZIP. Skips identical/newer revisions in the same firmware branch. Unsupported layouts, ambiguous offers, unknown upgrade paths and preparation/write failures are returned per drive. Packages and images are shared across serials and retained on disk. Verifies checksums and device identity before each write. Requires administrator/root access. Native completion may still require a power cycle; inspect returned output.</remarks>
    /// <param name="cancellationToken">Stops preparation or subsequent drives. An active firmware write finishes before cancellation is observed; earlier updates are not rolled back.</param>
    ValueTask<IReadOnlyList<FirmwareUpdateResult>> UpdateAllDriveFirmwares(CancellationToken cancellationToken = default);
    /// <summary>Rediscovers a drive by serial and checks its active revision against the target, including after a power cycle. Requires native device access.</summary>
    /// <remarks>Throws if identity or revision cannot be established. A false IsActive result does not prove activation is pending; inspect the manufacturer's instructions.</remarks>
    ValueTask<FirmwareVerification> VerifyFirmware(string serialNumber, string targetRevision, CancellationToken cancellationToken = default);

    /// <summary>Queries Seagate's current serial-specific firmware offerings using an installed stealth Chromium browser.</summary>
    /// <remarks>Versions are not ordered or inferred. An unrecognized serial is distinct from a recognized drive with no downloads. Network, challenge, and malformed response errors propagate.</remarks>
    ValueTask<FirmwareLookup> FindFirmware(string serialNumber, CancellationToken cancellationToken = default);

    /// <summary>Uses OpenSeaChest's parsed drive discovery to find the selected device, then queries Seagate. Requires native device access permissions.</summary>
    ValueTask<FirmwareLookup> FindFirmwareForDevice(string device, CancellationToken cancellationToken = default);

    /// <summary>Downloads a selected Seagate firmware package to a new file in the destination directory and returns its SHA-256 digest.</summary>
    /// <remarks>Reuses checksum-verified cached packages and identical destination files. Complete binaries are cached before copying to the destination, so copy failures do not require downloading again. Does not overwrite different files, extract archives, execute installers, or flash devices. The digest is not a manufacturer signature.</remarks>
    ValueTask<FirmwareDownload> DownloadFirmware(FirmwareRelease firmware, string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Selects the newest current offering within a single firmware branch; reuses checksum-verified cached content only after server revalidation, otherwise downloads it. Throws when the serial is unknown, no package exists, or selection is ambiguous.</summary>
    ValueTask<FirmwareDownload> DownloadLatestFirmware(string serialNumber, string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Lists checksum-verified packages cached for a serial without accessing Seagate or launching a browser. These are previously downloaded offerings, not a check for newer releases.</summary>
    ValueTask<IReadOnlyList<FirmwareDownload>> GetCachedFirmware(string serialNumber,
        CancellationToken cancellationToken = default);
}

