using GhPrApi.Models;
using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class ReviewNormalizerTests
{
    private readonly ReviewNormalizer _normalizer = new();

    [Theory]
    [InlineData("APPROVED", NormalizedValues.Review.Approved)]
    [InlineData("CHANGES_REQUESTED", NormalizedValues.Review.ChangesRequested)]
    [InlineData("REVIEW_REQUIRED", NormalizedValues.Review.AwaitingReview)]
    [InlineData(null, NormalizedValues.Review.AwaitingReview)]
    public void Normalize_maps_review_decision(string? input, string expected)
    {
        Assert.Equal(expected, _normalizer.Normalize(input));
    }
}
