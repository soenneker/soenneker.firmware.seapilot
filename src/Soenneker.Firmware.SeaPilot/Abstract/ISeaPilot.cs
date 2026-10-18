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
    /// <remarks>Firmware applicability is never inferred from matching models or another drive's lookup. Cached bytes may be reused only after the current drive's own lookup identifies its offered package. Duplicate handles for the same serial are processed once.</remarks>
    /// <remarks>Uses the primary image and revision in a serial-specific Seagate ZIP. Skips identical/newer revisions in the same firmware branch. Unsupported layouts, ambiguous offers, unknown upgrade paths and preparation/write failures are returned per drive. Packages and images are shared across serials and retained on disk. Verifies checksums and device identity before each write. Requires administrator/root access. Native completion may still require a power cycle; inspect returned output.</remarks>
    /// <param name="cancellationToken">Stops preparation or subsequent drives. An active firmware write finishes before cancellation is observed; earlier updates are not rolled back.</param>
    ValueTask<IReadOnlyList<FirmwareUpdateResult>> UpdateAllDriveFirmwares(CancellationToken cancellationToken = default);
    /// <summary>Queries Seagate's current serial-specific firmware offerings using an installed stealth Chromium browser.</summary>
    /// <remarks>Versions are not ordered or inferred. An unrecognized serial is distinct from a recognized drive with no downloads. Network, challenge, and malformed response errors propagate.</remarks>
    ValueTask<FirmwareLookup> FindFirmware(string serialNumber, CancellationToken cancellationToken = default);

    /// <summary>Uses OpenSeaChest's parsed drive discovery to find the selected device, then queries Seagate. Requires native device access permissions.</summary>
    ValueTask<FirmwareLookup> FindFirmwareForDevice(string device, CancellationToken cancellationToken = default);

    /// <summary>Downloads a selected Seagate firmware package to a new file in the destination directory and returns its SHA-256 digest.</summary>
    /// <remarks>Reuses checksum-verified cached packages and identical destination files. Complete binaries are cached before copying to the destination, so copy failures do not require downloading again. Does not overwrite different files, extract archives, execute installers, or flash devices. The digest is not a manufacturer signature.</remarks>
    ValueTask<FirmwareDownload> DownloadFirmware(FirmwareRelease firmware, string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Looks up and downloads the current offering when Seagate returns exactly one package. Throws when the serial is unknown, no package exists, or selection is ambiguous.</summary>
    ValueTask<FirmwareDownload> DownloadLatestFirmware(string serialNumber, string destinationDirectory,
        CancellationToken cancellationToken = default);

    /// <summary>Lists checksum-verified packages cached for a serial without accessing Seagate or launching a browser. These are previously downloaded offerings, not a check for newer releases.</summary>
    ValueTask<IReadOnlyList<FirmwareDownload>> GetCachedFirmware(string serialNumber,
        CancellationToken cancellationToken = default);
}

