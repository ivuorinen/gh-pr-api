using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class BranchNormalizer
{
    public string Normalize(string? mergeable, string? mergeStateStatus)
    {
        var normalizedMergeable = mergeable?.ToUpperInvariant();
        var normalizedMergeStateStatus = mergeStateStatus?.ToUpperInvariant();

        if (normalizedMergeable is "CONFLICTING" || normalizedMergeStateStatus is "DIRTY")
        {
            return NormalizedValues.Branch.Conflict;
        }

        if (normalizedMergeStateStatus is "BEHIND")
        {
            return NormalizedValues.Branch.Behind;
        }

        if (normalizedMergeable is "MERGEABLE")
        {
            return NormalizedValues.Branch.UpToDate;
        }

        return NormalizedValues.Branch.Unknown;
    }
}
