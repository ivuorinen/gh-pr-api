using GhPrApi.GitHub;
using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class PullRequestReportBuilder
{
    private readonly ReviewNormalizer _reviewNormalizer;
    private readonly CiNormalizer _ciNormalizer;
    private readonly BranchNormalizer _branchNormalizer;
    private readonly RobotDetector _robotDetector;
    private readonly DependencyNameDetector _dependencyNameDetector;
    private readonly SecurityDetector _securityDetector;
    private readonly TimeProvider _timeProvider;

    public PullRequestReportBuilder(
        ReviewNormalizer reviewNormalizer,
        CiNormalizer ciNormalizer,
        BranchNormalizer branchNormalizer,
        RobotDetector robotDetector,
        DependencyNameDetector dependencyNameDetector,
        SecurityDetector securityDetector,
        TimeProvider timeProvider)
    {
        _reviewNormalizer = reviewNormalizer;
        _ciNormalizer = ciNormalizer;
        _branchNormalizer = branchNormalizer;
        _robotDetector = robotDetector;
        _dependencyNameDetector = dependencyNameDetector;
        _securityDetector = securityDetector;
        _timeProvider = timeProvider;
    }

    public PullRequestReport Build(
        string owner,
        IReadOnlyList<GitHubPullRequest> pullRequests,
        bool truncated = false,
        IReadOnlyList<string>? unresolved = null)
    {
        var degraded = unresolved is { Count: > 0 };
        var unresolvedIds = degraded ? unresolved : null;
        var now = _timeProvider.GetUtcNow();
        var items = pullRequests
            .Select(pr => BuildItem(pr, now))
            .OrderBy(static item => item.CreatedAt)
            .ToArray();

        if (items.Length == 0)
        {
            return new PullRequestReport(
                owner,
                now,
                TotalCount: 0,
                Groups: [],
                Message: "No open PRs.",
                Truncated: truncated,
                Degraded: degraded,
                Unresolved: unresolvedIds);
        }

        var groups = new List<PullRequestGroup>();

        // An overlay, not a partition: these pull requests are listed again in their normal
        // group below, and TotalCount still counts each one once. GetSortRank is deliberately
        // not applied -- by construction nothing here is failing, so ranking would only push
        // stale ahead of fresh and break the oldest-first contract this section exists for.
        var easyWins = items
            .Where(IsEasyWin)
            .OrderBy(static item => item.CreatedAt)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();

        if (easyWins.Length > 0)
        {
            groups.Add(new PullRequestGroup(
                NormalizedValues.Group.EasyWinsKey,
                NormalizedValues.Group.EasyWinsTitle,
                PullRequests: easyWins));
        }

        var securityPullRequests = items
            .Where(static item => item.IsSecurity)
            .OrderBy(GetSortRank)
            .ThenBy(static item => item.CreatedAt)
            .ToArray();

        if (securityPullRequests.Length > 0)
        {
            groups.Add(new PullRequestGroup(
                NormalizedValues.Group.SecurityUpdatesKey,
                NormalizedValues.Group.SecurityUpdatesTitle,
                PullRequests: securityPullRequests));
        }

        var humanPullRequests = items
            .Where(static item => !item.IsSecurity && !item.IsRobot)
            .OrderBy(GetSortRank)
            .ThenBy(static item => item.CreatedAt)
            .ToArray();

        if (humanPullRequests.Length > 0)
        {
            groups.Add(new PullRequestGroup(
                NormalizedValues.Group.HumanPullRequestsKey,
                NormalizedValues.Group.HumanPullRequestsTitle,
                PullRequests: humanPullRequests));
        }

        var robotDependencyGroups = items
            .Where(static item => !item.IsSecurity && item.IsRobot)
            .GroupBy(static item => item.DependencyName ?? NormalizedValues.Dependency.OtherRobotPullRequests, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new DependencyPullRequestGroup(
                group.Key,
                group.OrderBy(static item => item.CreatedAt).ToArray()))
            .OrderBy(static group => group.PullRequests.Min(static item => item.CreatedAt))
            .ThenBy(static group => group.DependencyName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (robotDependencyGroups.Length > 0)
        {
            groups.Add(new PullRequestGroup(
                NormalizedValues.Group.RobotsKey,
                NormalizedValues.Group.RobotsTitle,
                DependencyGroups: robotDependencyGroups));
        }

        return new PullRequestReport(
            owner,
            now,
            items.Length,
            groups,
            Truncated: truncated,
            Degraded: degraded,
            Unresolved: unresolvedIds);
    }

    private PullRequestItem BuildItem(GitHubPullRequest pullRequest, DateTimeOffset now)
    {
        var isRobot = _robotDetector.IsRobot(pullRequest);
        var dependency = _dependencyNameDetector.Detect(pullRequest, isRobot);
        var isSecurity = _securityDetector.IsSecurity(pullRequest, dependency.Name);
        var review = _reviewNormalizer.Normalize(pullRequest.ReviewDecision);
        // An unresolved status is an absence of information, not a signal: it must not be
        // reported as pending, and GetSortRank leaves it at the default rank rather than
        // promoting it alongside failing.
        var ci = pullRequest.StatusUnresolved
            ? NormalizedValues.Ci.Unknown
            : _ciNormalizer.Normalize(pullRequest.StatusDetails);
        var branch = _branchNormalizer.Normalize(pullRequest.Mergeable, pullRequest.MergeStateStatus);
        var age = now - pullRequest.CreatedAt;
        var ageDays = Math.Max(0, (int)Math.Floor(age.TotalDays));
        var prefixes = BuildPrefixes(isSecurity, ci, age, pullRequest.IsDraft);

        return new PullRequestItem(
            Id: $"{pullRequest.RepositoryNameWithOwner}#{pullRequest.Number}",
            Repo: pullRequest.RepositoryNameWithOwner,
            Number: pullRequest.Number,
            Title: pullRequest.Title,
            Author: pullRequest.Author?.Login ?? "unknown",
            AuthorType: pullRequest.Author?.TypeName ?? "Unknown",
            CreatedAt: pullRequest.CreatedAt,
            Age: FormatAge(age),
            AgeDays: ageDays,
            IsDraft: pullRequest.IsDraft,
            IsSecurity: isSecurity,
            IsRobot: isRobot,
            DependencyName: dependency.Name,
            DependencyConfidence: dependency.Confidence,
            Review: review,
            Ci: ci,
            Branch: branch,
            HeadRefName: pullRequest.HeadRefName,
            Labels: pullRequest.Labels,
            Prefixes: prefixes,
            Url: pullRequest.Url.ToString());
    }

    private static IReadOnlyList<string> BuildPrefixes(bool isSecurity, string ci, TimeSpan age, bool isDraft)
    {
        var prefixes = new List<string>(capacity: 4);

        if (isSecurity)
        {
            prefixes.Add(NormalizedValues.Prefix.Security);
        }

        if (ci.Equals(NormalizedValues.Ci.Failing, StringComparison.OrdinalIgnoreCase))
        {
            prefixes.Add(NormalizedValues.Prefix.Failing);
        }

        if (age.TotalDays > 3)
        {
            prefixes.Add(NormalizedValues.Prefix.Stale);
        }

        if (isDraft)
        {
            prefixes.Add(NormalizedValues.Prefix.Draft);
        }

        return prefixes;
    }

    // Tested against Passing rather than "not Failing" on purpose: an unresolved status lookup
    // reports Unknown, and an absence of information must never be promoted to a green light.
    // Branch "behind" and "unknown" both qualify -- GitHub merges a behind-but-clean branch, and
    // "unknown" only means mergeability was not computed yet. Conflict is the sole blocker.
    private static bool IsEasyWin(PullRequestItem item) =>
        !item.IsDraft
        && item.Ci.Equals(NormalizedValues.Ci.Passing, StringComparison.OrdinalIgnoreCase)
        && !item.Branch.Equals(NormalizedValues.Branch.Conflict, StringComparison.OrdinalIgnoreCase)
        && !item.Review.Equals(NormalizedValues.Review.ChangesRequested, StringComparison.OrdinalIgnoreCase);

    private static int GetSortRank(PullRequestItem item)
    {
        if (item.Ci.Equals(NormalizedValues.Ci.Failing, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (item.Prefixes.Contains(NormalizedValues.Prefix.Stale, StringComparer.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 2;
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
        {
            return $"{Math.Floor(age.TotalDays):0}d";
        }

        if (age.TotalHours >= 1)
        {
            return $"{Math.Floor(age.TotalHours):0}h";
        }

        return $"{Math.Max(1, Math.Floor(age.TotalMinutes)):0}m";
    }
}
