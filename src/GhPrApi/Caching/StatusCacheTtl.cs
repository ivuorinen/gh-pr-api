using GhPrApi.Models;

namespace GhPrApi.Caching;

/// <summary>
/// How long a per-pull-request status entry stays cached, given the CI verdict it produced.
/// </summary>
/// <remarks>
/// The cache key carries the head commit SHA, so a push invalidates by itself. What the key
/// cannot catch is a re-run against the same commit, and people only ever re-run red. So a
/// failing verdict stays volatile while a passing one is treated as final.
/// The decision reads the already-normalized CI string rather than re-deriving check states,
/// so the TTL can never disagree with what the report shows.
/// ponytail: a re-run of a passing check is masked until Settled expires. ?refresh=true is the
/// escape hatch; shorten Settled if that ever actually bites.
/// </remarks>
public static class StatusCacheTtl
{
    public static readonly TimeSpan Settled = TimeSpan.FromHours(6);

    public static TimeSpan For(string normalizedCi, TimeSpan pendingTtl)
    {
        ArgumentNullException.ThrowIfNull(normalizedCi);

        if (normalizedCi.Equals(NormalizedValues.Ci.Passing, StringComparison.OrdinalIgnoreCase))
        {
            return Settled;
        }

        if (normalizedCi.Equals(NormalizedValues.Ci.Failing, StringComparison.OrdinalIgnoreCase))
        {
            return pendingTtl * 2;
        }

        // Pending, and anything unrecognised: treat as still moving.
        return pendingTtl;
    }
}
