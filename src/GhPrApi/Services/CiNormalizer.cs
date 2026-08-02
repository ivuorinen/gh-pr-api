using GhPrApi.GitHub;
using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class CiNormalizer
{
    private static readonly HashSet<string> FailedCheckRunConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION_REQUIRED",
        "CANCELLED",
        "CANCELED",
        "FAILURE",
        "STARTUP_FAILURE",
        "TIMED_OUT",
        "TIMEOUT",
    };

    private static readonly HashSet<string> SuccessfulCheckRunConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SUCCESS",
        "NEUTRAL",
        "SKIPPED",
    };

    private static readonly HashSet<string> PendingCheckRunStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXPECTED",
        "IN_PROGRESS",
        "PENDING",
        "QUEUED",
        "REQUESTED",
        "WAITING",
    };

    private static readonly HashSet<string> FailedStatusContextStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "ERROR",
        "FAILURE",
    };

    private static readonly HashSet<string> PendingStatusContextStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "EXPECTED",
        "PENDING",
    };

    public string Normalize(GitHubPullRequestStatusDetails? statusDetails)
    {
        if (statusDetails is null)
        {
            return NormalizedValues.Ci.Pending;
        }

        var requiredChecks = SelectRequiredChecks(statusDetails).ToArray();
        if (requiredChecks.Length == 0)
        {
            if (statusDetails.RequiresStatusChecks)
            {
                return NormalizedValues.Ci.Pending;
            }

            // No branch protection: nothing is required, so nothing blocks a merge. That is not
            // the same as green. Reporting "passing" while failed checks sit in StatusChecks is
            // a claim the Easy wins section acts on, and branch protection is off by default on
            // new repositories -- exactly the long tail this service is most useful for. Judge
            // whatever actually ran; only a repository with no checks at all is passing.
            return statusDetails.StatusChecks.Count == 0
                ? NormalizedValues.Ci.Passing
                : Verdict(statusDetails.StatusChecks);
        }

        return Verdict(requiredChecks);
    }

    private static string Verdict(IReadOnlyList<GitHubStatusCheck> checks)
    {
        if (checks.Any(IsFailing))
        {
            return NormalizedValues.Ci.Failing;
        }

        if (checks.Any(IsPending))
        {
            return NormalizedValues.Ci.Pending;
        }

        return checks.All(IsSuccessful)
            ? NormalizedValues.Ci.Passing
            : NormalizedValues.Ci.Pending;
    }

    private static IEnumerable<GitHubStatusCheck> SelectRequiredChecks(GitHubPullRequestStatusDetails statusDetails)
    {
        if (statusDetails.RequiredStatusCheckNames.Count == 0)
        {
            return statusDetails.StatusChecks.Where(static check => check.IsRequired);
        }

        var checksByName = statusDetails.StatusChecks
            .GroupBy(static check => check.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var requiredChecks = new List<GitHubStatusCheck>();
        foreach (var requiredName in statusDetails.RequiredStatusCheckNames)
        {
            if (checksByName.TryGetValue(requiredName, out var matchingChecks))
            {
                requiredChecks.AddRange(matchingChecks);
            }
            else
            {
                requiredChecks.Add(new GitHubStatusCheck(
                    "MissingRequiredCheck",
                    requiredName,
                    Status: null,
                    Conclusion: null,
                    State: null,
                    IsRequired: true));
            }
        }

        return requiredChecks;
    }

    private static bool IsFailing(GitHubStatusCheck check)
    {
        if (check.TypeName.Equals("StatusContext", StringComparison.OrdinalIgnoreCase))
        {
            return check.State is not null && FailedStatusContextStates.Contains(check.State);
        }

        return check.Conclusion is not null && FailedCheckRunConclusions.Contains(check.Conclusion);
    }

    private static bool IsPending(GitHubStatusCheck check)
    {
        if (check.TypeName.Equals("MissingRequiredCheck", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (check.TypeName.Equals("StatusContext", StringComparison.OrdinalIgnoreCase))
        {
            return check.State is null || PendingStatusContextStates.Contains(check.State);
        }

        if (check.Status is null)
        {
            return true;
        }

        if (!check.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return check.Conclusion is null || !SuccessfulCheckRunConclusions.Contains(check.Conclusion);
    }

    private static bool IsSuccessful(GitHubStatusCheck check)
    {
        if (check.TypeName.Equals("StatusContext", StringComparison.OrdinalIgnoreCase))
        {
            return check.State?.Equals("SUCCESS", StringComparison.OrdinalIgnoreCase) == true;
        }

        return check.Status?.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) == true
            && check.Conclusion is not null
            && SuccessfulCheckRunConclusions.Contains(check.Conclusion);
    }
}
