using GhPrApi.Caching;
using GhPrApi.Models;
using Xunit;

namespace GhPrApi.Tests;

public sealed class StatusCacheTtlTests
{
    private static readonly TimeSpan Pending = TimeSpan.FromSeconds(30);

    [Fact]
    public void Pending_checks_use_the_configured_short_ttl()
    {
        Assert.Equal(Pending, StatusCacheTtl.For(NormalizedValues.Ci.Pending, Pending));
    }

    [Fact]
    public void Failing_checks_use_double_the_short_ttl_because_a_rerun_keeps_the_same_sha()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), StatusCacheTtl.For(NormalizedValues.Ci.Failing, Pending));
    }

    [Fact]
    public void Passing_checks_are_settled_and_cached_for_hours()
    {
        Assert.Equal(StatusCacheTtl.Settled, StatusCacheTtl.For(NormalizedValues.Ci.Passing, Pending));
    }

    [Fact]
    public void An_unrecognised_value_is_treated_as_volatile_rather_than_settled()
    {
        Assert.Equal(Pending, StatusCacheTtl.For("something-else", Pending));
    }

    [Fact]
    public void Settled_is_six_hours()
    {
        Assert.Equal(TimeSpan.FromHours(6), StatusCacheTtl.Settled);
    }
}
