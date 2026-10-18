using System;

namespace Soenneker.Firmware.SeaPilot;

internal sealed class FirmwareDownloadSkippedException(string message) : Exception(message);
