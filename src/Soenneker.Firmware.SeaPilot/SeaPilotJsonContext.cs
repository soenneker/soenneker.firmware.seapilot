using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Firmware.SeaPilot;

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(FirmwareCacheEntry))]
public partial class SeaPilotJsonContext : JsonSerializerContext;

