using GhPrApi.Services;
using Microsoft.Extensions.Options;

namespace GhPrApi.Tests;

internal sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;

    public FixedTimeProvider(DateTimeOffset now)
    {
        _now = now;
    }

    public override DateTimeOffset GetUtcNow() => _now;
}

internal sealed class FakeOptionsMonitor<T> : IOptionsMonitor<T>
{
    public FakeOptionsMonitor(T currentValue)
    {
        CurrentValue = currentValue;
    }

    public T CurrentValue { get; set; }

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}

internal static class TestSupport
{
    public static PullRequestReportBuilder CreateBuilder(DateTimeOffset now) => new(
        new ReviewNormalizer(),
        new CiNormalizer(),
        new BranchNormalizer(),
        new RobotDetector(),
        new DependencyNameDetector(),
        new SecurityDetector(),
        new FixedTimeProvider(now));
}
