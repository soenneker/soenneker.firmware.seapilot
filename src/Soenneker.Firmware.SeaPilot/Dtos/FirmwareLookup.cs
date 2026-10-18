using System;
using System.Collections.Generic;

namespace Soenneker.Firmware.SeaPilot.Dtos;

/// <summary>Serial lookup outcome. No offerings does not establish that the installed firmware is current.</summary>
public sealed record FirmwareLookup
{
    public required string SerialNumber { get; init; }
    public required bool IsRecognized { get; init; }
    public string? ModelNumber { get; init; }
    public string? Device { get; init; }
    public string? InstalledRevision { get; init; }
    public Uri? SupportUri { get; init; }
    public required IReadOnlyList<FirmwareRelease> Releases { get; init; }
}
