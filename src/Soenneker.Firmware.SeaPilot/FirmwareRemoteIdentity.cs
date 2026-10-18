using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>A server's strong validator for one exact firmware resource.</summary>
public sealed record FirmwareRemoteIdentity
{
    public required string Url { get; init; }
    public required string ETag { get; init; }
    public required long Length { get; init; }

    public static FirmwareRemoteIdentity? FromHeaders(string url, IEnumerable<KeyValuePair<string, string>> headers)
    {
        var values = headers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (!values.TryGetValue("etag", out string? tag) || tag.Length < 2 || !tag.StartsWith('"') || !tag.EndsWith('"') ||
            !values.TryGetValue("content-length", out string? size) || !long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out long length) || length <= 0 ||
            (values.TryGetValue("content-encoding", out string? encoding) && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))) return null;
        return new FirmwareRemoteIdentity { Url = url, ETag = tag, Length = length };
    }
}
