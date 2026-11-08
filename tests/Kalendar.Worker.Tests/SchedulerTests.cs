using Kalendar.Worker.Scheduling;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kalendar.Worker.Tests;

public class SchedulerTests
{
    [Theory]
    [InlineData("2026-01-31T10:00:00Z", "monthly", 1, "2026-02-28T10:00:00Z")]
    [InlineData("2028-01-31T10:00:00Z", "monthly", 1, "2028-02-29T10:00:00Z")]
    [InlineData("2026-11-08T10:00:00Z", "daily", 2, "2026-11-10T10:00:00Z")]
    [InlineData("2026-11-09T10:00:00Z", "weekly", 1, "2026-11-11T10:00:00Z")]
    [InlineData("2026-11-11T10:00:00Z", "weekly", 2, "2026-11-23T10:00:00Z")]
    public void RecurrenceRetainsUtcTimeAndRespectsIntervals(string current, string frequency, int interval, string expected)
    {
        var date = DateTimeOffset.Parse(current).UtcDateTime;
        var rule = new TaskRecurrence(frequency, interval, ["wednesday", "monday"], null);
        Assert.Equal(DateTimeOffset.Parse(expected).UtcDateTime, RecurrenceDates.Next(date, rule));
        Assert.Null(RecurrenceDates.Next(date, rule with { EndAt = date }));
    }

    [Fact]
    public void IdempotencyKeyUsesMillisecondsAndUtc()
    {
        var date = new DateTime(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc).AddTicks(12345);
        Assert.Equal("reminder:task:2026-11-08T10:00:00.0010000Z", RecurrenceDates.Key("reminder", "task", date));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 60)]
    [InlineData(3, 300)]
    [InlineData(4, 900)]
    [InlineData(5, 3600)]
    public void RetryBackoff(int attempts, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), Scheduler.RetryDelay(attempts));

    [Fact]
    public void SchedulerSettingsHaveDefaultsAndRejectInvalidValues()
    {
        Assert.Equal(new SchedulerSettings(5, 60, 5, 10), SchedulerSettings.Read(new ConfigurationBuilder().Build()));
        foreach (var key in new[] { "PollIntervalSeconds", "LeaseSeconds", "MaxAttempts", "ReconcileIntervalMinutes" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"Scheduler:{key}"] = "0" }).Build();
            Assert.Throws<InvalidOperationException>(() => SchedulerSettings.Read(configuration));
        }
    }
}
