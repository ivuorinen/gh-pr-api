using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class ReviewNormalizer
{
    public string Normalize(string? reviewDecision) => reviewDecision?.ToUpperInvariant() switch
    {
        "APPROVED" => NormalizedValues.Review.Approved,
        "CHANGES_REQUESTED" => NormalizedValues.Review.ChangesRequested,
        _ => NormalizedValues.Review.AwaitingReview,
    };
}
