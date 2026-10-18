namespace Soenneker.Firmware.SeaPilot.Dtos;

/// <summary>A manufacturer-compatible raw image selected from a firmware package.</summary>
public sealed record FirmwareImageSelection
{
    public required string FilePath { get; init; }
    public required string TargetRevision { get; init; }
    public required string Sha256 { get; init; }
}
