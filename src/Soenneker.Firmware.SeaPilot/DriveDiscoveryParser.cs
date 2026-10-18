using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Soenneker.OpenSeaChest.Util.Dtos;

namespace Soenneker.Firmware.SeaPilot;

internal static class DriveDiscoveryParser
{
    internal static List<(string Handle, string? Vendor)> ParseScan(IReadOnlyList<string> lines)
    {
        ThrowOnAccessFailure(lines);
        if (lines.Any(line => line.Trim().Equals("No devices found", StringComparison.OrdinalIgnoreCase)))
            return [];
        int header = -1;
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].Contains("Vendor", StringComparison.Ordinal) && lines[i].Contains("Handle", StringComparison.Ordinal)
                && lines[i].Contains("Model Number", StringComparison.Ordinal) && lines[i].Contains("FwRev", StringComparison.Ordinal))
            { header = i; break; }
        if (header < 0)
            throw new InvalidDataException("Unrecognized openSeaChest scan output; a device table or explicit empty result was expected.");

        var result = new List<(string Handle, string? Vendor)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = header + 1; i < lines.Count; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            // Upstream prints the vendor in a minimum-width eight-character column.
            // Only parse vendor and handle here: model strings can contain spaces and overflow their column.
            if (line.Length < 10)
                throw new InvalidDataException($"Malformed openSeaChest scan row: {line}");
            string? vendor = Normalize(line[..8]);
            string remainder = line[8..].TrimStart();
            int end = remainder.IndexOfAny([' ', '\t']);
            string handle = end < 0 ? remainder.TrimEnd() : remainder[..end];
            if (!(handle.StartsWith("/dev/", StringComparison.Ordinal) || handle.StartsWith("PD", StringComparison.OrdinalIgnoreCase)
                || handle.StartsWith("SCSI", StringComparison.OrdinalIgnoreCase) || handle.StartsWith(@"\\.\", StringComparison.Ordinal)))
                throw new InvalidDataException($"Unrecognized device handle in scan row: {line}");
            if (seen.Add(handle))
                result.Add((handle, vendor));
        }
        if (result.Count == 0)
            throw new InvalidDataException("The scan table contained no device rows and did not report an empty system.");
        return result;
    }

    internal static OpenSeaChestDrive ParseDevice(string handle, string? vendor, IReadOnlyList<string> lines)
    {
        ThrowOnAccessFailure(lines);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool hasIdentityField = false;
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon < 1)
                continue;
            string key = line[..colon].Trim();
            hasIdentityField |= key.Equals("Model Number", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Serial Number", StringComparison.OrdinalIgnoreCase) || key.Equals("Firmware Revision", StringComparison.OrdinalIgnoreCase);
            string? value = Normalize(line[(colon + 1)..]);
            if (value is not null)
                fields.TryAdd(key, value);
        }
        string? Get(string key) => fields.GetValueOrDefault(key);
        // An exit code of zero alone does not prove that the tool actually returned device information.
        if (!hasIdentityField)
            throw new InvalidDataException($"No recognizable device information was returned for {handle}.");
        uint? blockSize = uint.TryParse(Get("Logical Sector Size (B)") ?? Get("Logical Block Size (B)"), NumberStyles.None,
            CultureInfo.InvariantCulture, out uint size) && size > 0 ? size : null;
        ulong? capacity = null;
        if (blockSize.HasValue)
        {
            if (ulong.TryParse(Get("Namespace Capacity (LBAs)"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong blocks))
                capacity = checked(blocks * blockSize.Value);
            else if (ulong.TryParse(Get("MaxLBA"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong maxLba))
                capacity = checked((maxLba + 1) * blockSize.Value);
        }
        return new OpenSeaChestDrive
        {
            Handle = handle,
            Vendor = Get("Vendor ID") ?? vendor,
            ModelNumber = Get("Model Number"),
            SerialNumber = Get("Serial Number"),
            FirmwareRevision = Get("Firmware Revision"),
            InterfaceType = Get("Interface Type"),
            CapacityBytes = capacity,
            LogicalBlockSizeBytes = blockSize,
            Details = new ReadOnlyDictionary<string, string>(fields),
            RawDeviceInfo = Array.AsReadOnly(lines.ToArray())
        };
    }

    private static string? Normalize(string value)
    {
        value = value.Trim();
        return value.Length == 0 || value.Equals("Not Reported", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Not Available", StringComparison.OrdinalIgnoreCase) || value.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            ? null : value;
    }

    private static void ThrowOnAccessFailure(IReadOnlyList<string> lines)
    {
        foreach (string line in lines)
        {
            if (line.Contains("permission", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("denied", StringComparison.OrdinalIgnoreCase) || line.Contains("lack of", StringComparison.OrdinalIgnoreCase))
                || line.Contains("must run with elevated privileges", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(line.Trim());
            if (line.TrimStart().StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Not all devices enumerated", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(line.Trim());
        }
    }
}