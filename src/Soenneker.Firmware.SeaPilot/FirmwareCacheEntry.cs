using Soenneker.Firmware.SeaPilot.Dtos;
using System.Collections.Generic;

namespace Soenneker.Firmware.SeaPilot;

public sealed record FirmwareCacheEntry
{
    public FirmwareRemoteIdentity? RemoteIdentity { get; init; }
    public required FirmwareRelease Firmware { get; init; }
    public required string Filename { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
    public List<string> Serials { get; init; } = [];
}

