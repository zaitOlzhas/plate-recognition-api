using LprWebhook.Api.Parsing;

namespace LprWebhook.Api.Tests;

public class PayloadParsingTests
{
    private const string Sample = """
        {
          "event": "plate_recognized",
          "timestamp": "2026-10-05T14:23:07.512",
          "date": { "year": "2026", "month": "10", "day": "05", "hour": "14", "minute": "23", "second": "07", "millisecond": "512" },
          "server": { "name": "LPR-Server-1", "domain_name": "lpr.local" },
          "camera": { "name": "Gate Entrance", "number": 1, "index": 0, "id": "cam-001" },
          "plate": { "text": "123ABC02", "direction": "in", "description": "", "list": { "name": "Whitelist", "description": "Employees" } }
        }
        """;

    [Fact]
    public void Parses_sample_payload_with_snake_case_names()
    {
        var result = PayloadParser.Parse(Sample);

        Assert.True(result.Success, result.Error);
        var p = result.Payload!;
        Assert.Equal("plate_recognized", p.Event);
        Assert.Equal("lpr.local", p.Server!.DomainName);
        Assert.Equal(1, p.Camera!.Number);
        Assert.Equal(0, p.Camera.Index);
        Assert.Equal("cam-001", p.Camera.Id);
        Assert.Equal("123ABC02", p.Plate!.Text);
        Assert.Equal("Whitelist", p.Plate.List!.Name);
        Assert.Equal("512", p.Date!.Millisecond);
        Assert.False(result.Repaired);
    }

    [Fact]
    public void Camera_numbers_may_arrive_as_strings()
    {
        var result = PayloadParser.Parse("""{ "camera": { "number": "7", "index": " 3 " }, "plate": { "text": "A1" } }""");

        Assert.True(result.Success, result.Error);
        Assert.Equal(7, result.Payload!.Camera!.Number);
        Assert.Equal(3, result.Payload.Camera.Index);
    }

    [Theory]
    [InlineData("""{ "camera": { "number": "", "index": "" }, "plate": { "text": "A1" } }""")]
    [InlineData("""{ "camera": { "number": null, "index": "n/a" }, "plate": { "text": "A1" } }""")]
    [InlineData("""{ "camera": { "number": , "index": }, "plate": { "text": "A1" } }""")] // unquoted placeholder substituted with nothing
    public void Empty_or_invalid_camera_number_becomes_null(string json)
    {
        var result = PayloadParser.Parse(json);

        Assert.True(result.Success, result.Error);
        Assert.Null(result.Payload!.Camera!.Number);
        Assert.Null(result.Payload.Camera.Index);
    }

    [Fact]
    public void Date_fields_accept_numbers_too()
    {
        var result = PayloadParser.Parse("""{ "date": { "year": 2026, "month": 10 }, "plate": { "text": "A1" } }""");

        Assert.True(result.Success, result.Error);
        Assert.Equal("2026", result.Payload!.Date!.Year);
        Assert.Equal("10", result.Payload.Date.Month);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var result = PayloadParser.Parse("""{ "plate": { "text": "A1", "confidence": 0.93, "box": [1,2,3,4] }, "extra": { "x": 1 } }""");

        Assert.True(result.Success, result.Error);
        Assert.Equal("A1", result.Payload!.Plate!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "plate": { "text": "" } }""")]
    [InlineData("""{ "plate": { } }""")]
    [InlineData("""{ "event": "plate_recognized" }""")]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    public void Invalid_body_or_missing_plate_text_is_rejected(string json)
    {
        var result = PayloadParser.Parse(json);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void Repair_does_not_touch_colons_inside_strings()
    {
        var json = """{ "plate": { "text": "A:,1", "description": "x:}" }, "camera": { "number": } }""";

        var result = PayloadParser.Parse(json);

        Assert.True(result.Success, result.Error);
        Assert.True(result.Repaired);
        Assert.Equal("A:,1", result.Payload!.Plate!.Text);
        Assert.Equal("x:}", result.Payload.Plate.Description);
    }
}
