namespace Soenneker.Firmware.SeaPilot.Dtos;

/// <summary>A completed package download, its source, size, and SHA-256 checksum.</summary>
public sealed record FirmwareDownload
{
    public required FirmwareRelease Firmware { get; init; }
    public required string FilePath { get; init; }
    public required long Length { get; init; }
    public required string Sha256 { get; init; }
}
