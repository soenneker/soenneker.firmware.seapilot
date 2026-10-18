using System;
using System.Collections.Generic;
using Soenneker.OpenSeaChest.Util.Dtos;

namespace Soenneker.Firmware.SeaPilot.Dtos;

/// <summary>The outcome for one discovered drive. Native output is present only after successful native execution; activation may still require a power cycle.</summary>
public sealed record FirmwareUpdateResult
{
    public required OpenSeaChestDrive Drive { get; init; }
    public FirmwareDownload? Package { get; init; }
    public FirmwareImageSelection? Image { get; init; }
    public string? SkipReason { get; init; }
    public Exception? Error { get; init; }
    /// <summary>True only when the native write returned successfully; activation is checked separately.</summary>
    public bool WriteCompleted { get; init; }
    public FirmwareVerification? Verification { get; init; }
    public Exception? VerificationError { get; init; }
    public bool FlashAttempted { get; init; }
    public IReadOnlyList<string>? NativeOutput { get; init; }
}
