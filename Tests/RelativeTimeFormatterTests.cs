using System;
using STool.Core;
using Xunit;

namespace STool.Tests;

public class RelativeTimeFormatterTests
{
    [Fact]
    public void Format_WithinOneMinute_ReturnsJustNow()
    {
        Assert.Equal("刚刚", RelativeTimeFormatter.Format(DateTime.Now.AddSeconds(-30)));
    }

    [Fact]
    public void Format_FutureTime_ReturnsJustNow()
    {
        Assert.Equal("刚刚", RelativeTimeFormatter.Format(DateTime.Now.AddMinutes(5)));
    }

    [Fact]
    public void Format_MinutesAgo_ReturnsMinutesText()
    {
        Assert.Equal("5分钟前", RelativeTimeFormatter.Format(DateTime.Now.AddMinutes(-5)));
    }

    [Fact]
    public void Format_OverOneYearAgo_ReturnsFullDate()
    {
        var result = RelativeTimeFormatter.Format(new DateTime(2000, 3, 5, 10, 0, 0));
        Assert.Equal("2000年3月5日", result);
    }

    [Fact]
    public void GetFullTimestamp_ContainsYearMonthDayAndTime()
    {
        var result = RelativeTimeFormatter.GetFullTimestamp(new DateTime(2024, 1, 2, 13, 45, 30));
        Assert.Equal("2024年1月2日 13:45:30", result);
    }
}
