using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Soenneker.Firmware.SeaPilot.Dtos;
using Soenneker.Utils.Json;

namespace Soenneker.Firmware.SeaPilot;

public static class SeagateResponseParser
{
    public static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            !(uri.Host.Equals("seagate.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".seagate.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Expected an HTTPS download URL on seagate.com.");
    }

    public static FirmwareLookup Parse(string json, string serial)
    {
        JsonElement document = JsonUtil.Deserialize(json, SeaPilotJsonContext.Default.JsonElement);
        if (!document.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() != 1)
            throw new InvalidDataException("Seagate returned an unexpected results envelope.");
        JsonElement result = results[0];
        string? error = Text(result, "error");
        if (!string.Equals(error, "false", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Seagate rejected the lookup: " + Text(result, "message"));
        string? returnedSerial = Text(result, "sn");
        if (returnedSerial is not null && !returnedSerial.Equals(serial, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Seagate returned a different drive serial number.");
        var releases = new List<FirmwareRelease>();
        if (result.TryGetProperty("firmware", out JsonElement firmware) && firmware.ValueKind != JsonValueKind.Null)
        {
            if (firmware.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Seagate returned an unexpected firmware list.");
            foreach (JsonElement item in firmware.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() == "none")
                {
                    if (firmware.GetArrayLength() != 1)
                        throw new InvalidDataException("Seagate returned contradictory firmware results.");
                    break;
                }
                string url = Text(item, "firmware") ?? throw new InvalidDataException("Firmware download URL is missing.");
                var uri = new Uri(url, UriKind.Absolute);
                ValidateUri(uri);
                releases.Add(new FirmwareRelease { SerialNumber = serial, Title = Text(item, "title") ?? throw new InvalidDataException("Firmware title is missing."),
                    Description = Text(item, "shortDesc"), DownloadUri = uri });
            }
        }
        if (returnedSerial is null && releases.Count != 0)
            throw new InvalidDataException("Firmware results were not associated with a serial number.");
        Uri? support = null;
        string? supportUrl = Text(result, "support_page_url");
        if (supportUrl is not null)
        {
            support = new Uri(supportUrl, UriKind.Absolute);
            ValidateUri(support);
        }
        // Unknown serials currently return a generic search URL without sn or firmware.
        if (returnedSerial is null && (support is null || support.AbsolutePath != "/search/"))
            throw new InvalidDataException("Seagate returned neither a drive identity nor its unknown-serial search response.");
        return new FirmwareLookup { SerialNumber = serial, IsRecognized = returnedSerial is not null,
            ModelNumber = Text(result, "model_number"), SupportUri = support, Releases = releases.AsReadOnly() };
    }

    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Expected a Seagate response object.");
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return value.ToString();
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Unexpected value for Seagate field {name}.");
        string? text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

