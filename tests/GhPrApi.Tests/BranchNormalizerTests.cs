using GhPrApi.Models;
using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class BranchNormalizerTests
{
    private readonly BranchNormalizer _normalizer = new();

    [Theory]
    [InlineData("CONFLICTING", "DIRTY", NormalizedValues.Branch.Conflict)]
    [InlineData("MERGEABLE", "BEHIND", NormalizedValues.Branch.Behind)]
    [InlineData("MERGEABLE", "CLEAN", NormalizedValues.Branch.UpToDate)]
    [InlineData("UNKNOWN", "UNKNOWN", NormalizedValues.Branch.Unknown)]
    [InlineData(null, null, NormalizedValues.Branch.Unknown)]
    public void Normalize_maps_branch_status(string? mergeable, string? mergeStateStatus, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(mergeable, mergeStateStatus));
    }
}
