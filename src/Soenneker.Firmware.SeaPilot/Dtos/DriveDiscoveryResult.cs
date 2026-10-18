using System;
using Soenneker.OpenSeaChest.Util.Dtos;

namespace Soenneker.Firmware.SeaPilot.Dtos;

/// <summary>A scanned device and any failure reading its details. Failed devices contain no trusted firmware identity.</summary>
public sealed record DriveDiscoveryResult
{
    public required OpenSeaChestDrive Drive { get; init; }
    public Exception? Error { get; init; }
}
