using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace Soenneker.Firmware.SeaPilot;

/// <summary>Captures validators from the actual browser transfer, without issuing a second download.</summary>
public sealed class FirmwareResponseObserver : IAsyncDisposable
{
    private ICDPSession? _session;
    private readonly Dictionary<string, FirmwareRemoteIdentity> _responses = new(StringComparer.Ordinal);

    public FirmwareRemoteIdentity? Find(string url)
    {
        lock (_responses)
            return _responses.GetValueOrDefault(url);
    }

    public static async Task<FirmwareResponseObserver> Attach(IPage page)
    {
        var observer = new FirmwareResponseObserver();
        try
        {
            observer._session = await page.Context.NewCDPSessionAsync(page);
            observer._session.Event("Network.responseReceived").OnEvent += observer.OnResponse;
            await observer._session.SendAsync("Network.enable");
        }
        catch (PlaywrightException)
        {
            await observer.DisposeAsync();
        }

        return observer;
    }

    private void OnResponse(object? sender, JsonElement? message)
    {
        if (message is not { } data || !data.TryGetProperty("response", out JsonElement response) ||
            !response.TryGetProperty("status", out JsonElement status) || status.GetInt32() != 200 ||
            !response.TryGetProperty("url", out JsonElement url) ||
            !response.TryGetProperty("headers", out JsonElement headers))
            return;
        var values = new Dictionary<string, string>();
        foreach (JsonProperty property in headers.EnumerateObject())
            values[property.Name] = property.Value.ToString();
        FirmwareRemoteIdentity? identity = FirmwareRemoteIdentity.FromHeaders(url.GetString()!, values);
        if (identity is not null)
        {
            lock (_responses)
                _responses[identity.Url] = identity;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is null)
            return;
        _session.Event("Network.responseReceived").OnEvent -= OnResponse;
        try
        {
            await _session.DetachAsync();
        }
        catch (PlaywrightException)
        {
        }

        _session = null;
    }
}