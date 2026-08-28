using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Services.Terminology;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Terminology;

public sealed class TerminologySyncScheduleEvaluatorTests
{
    private static TerminologySyncScheduleEvaluator Evaluator(FakeSystemSettingsCache cache) => new(cache);

    [Fact]
    public async Task IsDueAsync_scheduler_disabled_is_never_due()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Cvx:SchedulerEnabled", "False");
        var config = new TerminologySyncScheduleConfig("Terminology:Cvx", "03:00");

        var due = await Evaluator(cache).IsDueAsync(config, Monday(hour: 4), CancellationToken.None);

        due.Should().BeFalse();
    }

    [Fact]
    public async Task IsDueAsync_before_execution_time_is_not_due()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Cvx:SchedulerEnabled", "True");
        cache.Set("Terminology:Cvx:Frequency", "Daily");
        var config = new TerminologySyncScheduleConfig("Terminology:Cvx", "03:00");

        var due = await Evaluator(cache).IsDueAsync(config, Monday(hour: 2), CancellationToken.None);

        due.Should().BeFalse();
    }

    [Fact]
    public async Task IsDueAsync_daily_is_due_every_day_once_past_execution_time()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Ndc:SchedulerEnabled", "True");
        cache.Set("Terminology:Ndc:Frequency", "Daily");
        var config = new TerminologySyncScheduleConfig("Terminology:Ndc", "04:00", TerminologySyncFrequency.Daily);

        var due = await Evaluator(cache).IsDueAsync(config, Wednesday(hour: 5), CancellationToken.None);

        due.Should().BeTrue();
    }

    [Fact]
    public async Task IsDueAsync_weekly_is_due_only_on_monday()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Cvx:SchedulerEnabled", "True");
        cache.Set("Terminology:Cvx:Frequency", "Weekly");
        var config = new TerminologySyncScheduleConfig("Terminology:Cvx", "03:00");

        (await Evaluator(cache).IsDueAsync(config, Monday(hour: 4), CancellationToken.None)).Should().BeTrue();
        (await Evaluator(cache).IsDueAsync(config, Wednesday(hour: 4), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsDueAsync_monthly_is_due_only_on_the_first_of_the_month()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Cvx:SchedulerEnabled", "True");
        cache.Set("Terminology:Cvx:Frequency", "Monthly");
        var config = new TerminologySyncScheduleConfig("Terminology:Cvx", "03:00");

        var firstOfMonth = new DateTimeOffset(2026, 8, 1, 4, 0, 0, TimeSpan.Zero);
        var midMonth = new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero);

        (await Evaluator(cache).IsDueAsync(config, firstOfMonth, CancellationToken.None)).Should().BeTrue();
        (await Evaluator(cache).IsDueAsync(config, midMonth, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsDueAsync_fixed_cadence_ignores_any_frequency_setting()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Snomed:SchedulerEnabled", "True");
        var config = new TerminologySyncScheduleConfig(
            "Terminology:Snomed", "03:00", DefaultFrequency: null,
            FixedCadence: now => now.Day == 1 && (now.Month == 3 || now.Month == 9));

        var marchFirst = new DateTimeOffset(2026, 3, 1, 4, 0, 0, TimeSpan.Zero);
        var aprilFirst = new DateTimeOffset(2026, 4, 1, 4, 0, 0, TimeSpan.Zero);

        (await Evaluator(cache).IsDueAsync(config, marchFirst, CancellationToken.None)).Should().BeTrue();
        (await Evaluator(cache).IsDueAsync(config, aprilFirst, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsDueAsync_already_run_today_is_not_due_again()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Ndc:SchedulerEnabled", "True");
        cache.Set("Terminology:Ndc:Frequency", "Daily");
        var now = Wednesday(hour: 5);
        cache.Set("Terminology:Ndc:LastRunUtc", now.UtcDateTime.ToString("O"));
        var config = new TerminologySyncScheduleConfig("Terminology:Ndc", "04:00", TerminologySyncFrequency.Daily);

        var due = await Evaluator(cache).IsDueAsync(config, now, CancellationToken.None);

        due.Should().BeFalse();
    }

    [Fact]
    public async Task IsDueAsync_ran_yesterday_is_due_again_today()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Ndc:SchedulerEnabled", "True");
        cache.Set("Terminology:Ndc:Frequency", "Daily");
        var now = Wednesday(hour: 5);
        cache.Set("Terminology:Ndc:LastRunUtc", now.AddDays(-1).UtcDateTime.ToString("O"));
        var config = new TerminologySyncScheduleConfig("Terminology:Ndc", "04:00", TerminologySyncFrequency.Daily);

        var due = await Evaluator(cache).IsDueAsync(config, now, CancellationToken.None);

        due.Should().BeTrue();
    }

    [Fact]
    public async Task IsDueAsync_missing_settings_fall_back_to_config_defaults()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Cvx:SchedulerEnabled", "True");
        // No Frequency, ExecutionTime, or LastRunUtc rows set — must fall back to config defaults.
        var config = new TerminologySyncScheduleConfig("Terminology:Cvx", "03:00", TerminologySyncFrequency.Monthly);

        var firstOfMonth = new DateTimeOffset(2026, 8, 1, 4, 0, 0, TimeSpan.Zero);

        (await Evaluator(cache).IsDueAsync(config, firstOfMonth, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsDueAsync_unparsable_frequency_falls_back_to_config_default()
    {
        var cache = new FakeSystemSettingsCache();
        cache.Set("Terminology:Cvx:SchedulerEnabled", "True");
        cache.Set("Terminology:Cvx:Frequency", "not-a-real-value");
        var config = new TerminologySyncScheduleConfig("Terminology:Cvx", "03:00", TerminologySyncFrequency.Monthly);

        var firstOfMonth = new DateTimeOffset(2026, 8, 1, 4, 0, 0, TimeSpan.Zero);
        var midMonth = new DateTimeOffset(2026, 8, 15, 4, 0, 0, TimeSpan.Zero);

        (await Evaluator(cache).IsDueAsync(config, firstOfMonth, CancellationToken.None)).Should().BeTrue();
        (await Evaluator(cache).IsDueAsync(config, midMonth, CancellationToken.None)).Should().BeFalse();
    }

    private static DateTimeOffset Monday(int hour) => new(2026, 8, 24, hour, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Wednesday(int hour) => new(2026, 8, 26, hour, 0, 0, TimeSpan.Zero);

    private sealed class FakeSystemSettingsCache : ISystemSettingsCache
    {
        private readonly Dictionary<string, string> _values = new();

        public void Set(string key, string value) => _values[key] = value;

        public Task<string> GetStringAsync(string key, string defaultValue, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : defaultValue);

        public Task<bool> GetBoolAsync(string key, bool defaultValue, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
                ? parsed
                : defaultValue);

        public Task<int> GetIntAsync(string key, int defaultValue, CancellationToken cancellationToken) =>
            Task.FromResult(defaultValue);

        public Task<double> GetDoubleAsync(string key, double defaultValue, CancellationToken cancellationToken) =>
            Task.FromResult(defaultValue);

        public void Invalidate()
        {
        }
    }
}
