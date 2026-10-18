using System;
using System.IO;
using Soenneker.Firmware.SeaPilot.Dtos;

namespace Soenneker.Firmware.SeaPilot.Tests;

public sealed class SeagateResponseParserTests
{
    [Test]
    public void ParsesCurrentOfferingsWithoutGuessingRevision()
    {
        FirmwareLookup lookup = SeagateResponseParser.Parse("""
            {"results":[{"error":"false","sn":"ABC123","model_number":"ST18000NM000J","firmware":[
              {"firmware":"https://apps1.seagate.com/downloads/certificate.html?action=performDownload&key=123","title":"512E/4kN Firmware Update","shortDesc":"Current offering"},
              {"firmware":"https://apps1.seagate.com/downloads/certificate.html?action=performDownload&key=456","title":"Alternative package"}]}]}
            """, "ABC123");
        if (!lookup.IsRecognized || lookup.Releases.Count != 2 || lookup.ModelNumber != "ST18000NM000J") throw new Exception("Offerings were lost.");
    }

    [Test]
    public void DistinguishesNoFirmwareFromUnknownSerial()
    {
        FirmwareLookup known = SeagateResponseParser.Parse("""{"results":[{"error":"false","sn":"ABC123","firmware":["none"]}]}""", "ABC123");
        FirmwareLookup unknown = SeagateResponseParser.Parse("""{"results":[{"error":"false","support_page_url":"https://www.seagate.com/search/?sn_search=false"}]}""", "ABC123");
        if (!known.IsRecognized || unknown.IsRecognized || known.Releases.Count != 0 || unknown.Releases.Count != 0) throw new Exception("Outcomes were conflated.");
    }

    [Test]
    [Arguments("{\"results\":[]}")]
    [Arguments("{\"results\":[{\"error\":\"true\",\"message\":\"Unavailable\"}]}")]
    [Arguments("{\"results\":[{\"error\":\"false\",\"sn\":\"OTHER\"}]}")]
    [Arguments("{\"results\":[{\"error\":\"false\"}]}")]
    [Arguments("{\"results\":[{\"error\":\"false\",\"sn\":\"ABC123\",\"firmware\":{}}]}")]
    public void RejectsUntrustworthyResponses(string json)
    {
        try { SeagateResponseParser.Parse(json, "ABC123"); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid response rejection.");
    }

    [Test]
    [Arguments("https://seagate.com.evil.example/fw.zip")]
    [Arguments("http://apps1.seagate.com/fw.zip")]
    [Arguments("https://user:pass@seagate.com/fw.zip")]
    [Arguments("https://seagate.com:8443/fw.zip")]
    public void RejectsNonManufacturerDownloadLocations(string url)
    {
        try { SeagateResponseParser.ValidateUri(new Uri(url)); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected URL rejection.");
    }

}
