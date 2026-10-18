using System;

namespace Soenneker.Firmware.SeaPilot.Dtos;

/// <summary>A current firmware offering returned by Seagate for a specific serial number.</summary>
public sealed record FirmwareRelease
{
    public required string SerialNumber { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required Uri DownloadUri { get; init; }
}
