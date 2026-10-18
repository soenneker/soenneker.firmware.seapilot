using System;

namespace Soenneker.Firmware.SeaPilot.Dtos;

/// <summary>A fresh observation of a drive's active firmware, matched by serial number.</summary>
public sealed record FirmwareVerification
{
    public required string SerialNumber { get; init; }
    public required string TargetRevision { get; init; }
    public required string ObservedRevision { get; init; }
    public bool IsActive => string.Equals(TargetRevision, ObservedRevision, StringComparison.OrdinalIgnoreCase);
}
