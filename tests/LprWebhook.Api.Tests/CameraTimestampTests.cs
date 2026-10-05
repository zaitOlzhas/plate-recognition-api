using LprWebhook.Api.Contracts;
using LprWebhook.Api.Parsing;

namespace LprWebhook.Api.Tests;

public class CameraTimestampTests
{
    [Theory]
    [InlineData("2026-10-05T14:23:07.512", 512)]
    [InlineData("2026-10-05T14:23:07.051", 51)]
    [InlineData("2026-10-05T14:23:07.51", 51)]  // unpadded ${millisecond}: 51 ms, not 510 ms
    [InlineData("2026-10-05T14:23:07.5", 5)]
    [InlineData("2026-10-05T14:23:07", 0)]
    [InlineData("2026-10-05 14:23:07.512", 512)]
    public void Parses_variable_millisecond_lengths(string timestamp, int expectedMs)
    {
        var result = CameraTimestamp.Parse(timestamp);

        Assert.Equal(new DateTime(2026, 10, 5, 14, 23, 7, expectedMs), result);
        Assert.Equal(DateTimeKind.Unspecified, result!.Value.Kind);
    }

    [Fact]
    public void Long_fractions_are_decimal_fractions()
    {
        var result = CameraTimestamp.Parse("2026-10-05T14:23:07.5123456789");

        Assert.Equal(new DateTime(2026, 10, 5, 14, 23, 7).AddTicks(5_123_456), result);
    }

    [Fact]
    public void Unpadded_date_parts_are_accepted()
    {
        Assert.Equal(new DateTime(2026, 1, 5, 4, 3, 2, 7), CameraTimestamp.Parse("2026-1-5T4:3:2.7"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("${year}-${month}-${day}T${hour}:${minute}:${second}.${millisecond}")]
    [InlineData("2026-13-05T14:23:07.512")]
    public void Falls_back_to_date_object_when_timestamp_is_unusable(string? timestamp)
    {
        var date = new PayloadDate("2026", "10", "05", "14", "23", "07", "9");

        Assert.Equal(new DateTime(2026, 10, 5, 14, 23, 7, 9), CameraTimestamp.Resolve(timestamp, date));
    }

    [Fact]
    public void Returns_null_when_nothing_is_usable()
    {
        Assert.Null(CameraTimestamp.Resolve("garbage", new PayloadDate("", "", "", null, null, null, null)));
        Assert.Null(CameraTimestamp.Resolve(null, null));
    }
}
