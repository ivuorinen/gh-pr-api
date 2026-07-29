using System.Text.RegularExpressions;
using GhPrApi.GitHub;
using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed partial class DependencyNameDetector
{
    public DependencyDetection Detect(GitHubPullRequest pullRequest, bool isRobot)
    {
        if (!isRobot)
        {
            return DependencyDetection.None;
        }

        if (IsMultiDependencyPullRequest(pullRequest))
        {
            return new DependencyDetection(NormalizedValues.Dependency.MultipleDependencies, "high");
        }

        var branchCandidate = DetectFromBranchName(pullRequest.HeadRefName);
        if (!string.IsNullOrWhiteSpace(branchCandidate))
        {
            return new DependencyDetection(branchCandidate, "high");
        }

        var titleCandidate = DetectFromTitle(pullRequest.Title);
        if (!string.IsNullOrWhiteSpace(titleCandidate))
        {
            return new DependencyDetection(titleCandidate, "medium");
        }

        return new DependencyDetection(NormalizedValues.Dependency.OtherRobotPullRequests, "low");
    }

    private static bool IsMultiDependencyPullRequest(GitHubPullRequest pullRequest)
    {
        var text = $"{pullRequest.Title} {pullRequest.HeadRefName}";
        return text.Contains("multiple dependencies", StringComparison.OrdinalIgnoreCase)
            || text.Contains("update all", StringComparison.OrdinalIgnoreCase)
            || text.Contains("update dependencies", StringComparison.OrdinalIgnoreCase)
            || text.Contains("lock file maintenance", StringComparison.OrdinalIgnoreCase)
            || text.Contains("lockfile maintenance", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DetectFromBranchName(string branchName)
    {
        var normalized = branchName.Trim();

        var dependabotMatch = DependabotBranchRegex().Match(normalized);
        if (dependabotMatch.Success)
        {
            return NormalizeDependencyCandidate(dependabotMatch.Groups["dependency"].Value);
        }

        var renovateMatch = RenovateBranchRegex().Match(normalized);
        if (renovateMatch.Success)
        {
            return NormalizeDependencyCandidate(renovateMatch.Groups["dependency"].Value);
        }

        return null;
    }

    private static string? DetectFromTitle(string title)
    {
        foreach (var regex in new[] { UpdateDependencyTitleRegex(), BumpDependencyTitleRegex(), BacktickDependencyTitleRegex() })
        {
            var match = regex.Match(title);
            if (!match.Success)
            {
                continue;
            }

            var candidate = NormalizeDependencyCandidate(match.Groups["dependency"].Value);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? NormalizeDependencyCandidate(string candidate)
    {
        var value = candidate.Trim().Trim('`', '\'', '"');
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Replace("%2f", "/", StringComparison.OrdinalIgnoreCase);
        value = value.Replace("_", "-", StringComparison.Ordinal);
        value = KnownRenovatePrefixRegex().Replace(value, string.Empty);
        value = VersionSuffixRegex().Replace(value, string.Empty);
        value = value.Trim('-', '/', ' ');

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    [GeneratedRegex("^dependabot/[^/]+/(?<dependency>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DependabotBranchRegex();

    [GeneratedRegex("^renovate/(?<dependency>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RenovateBranchRegex();

    [GeneratedRegex("^(major|minor|patch|digest|pin)-", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownRenovatePrefixRegex();

    [GeneratedRegex("-(?:v?\\d+(?:\\.\\d+)*(?:[.-][a-z0-9]+)?|v?\\d+x|\\d+\\.x|latest)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionSuffixRegex();

    [GeneratedRegex("(?:update|upgrade) (?:dependency )?(?<dependency>@?[a-z0-9][a-z0-9._/@-]+) (?:to|from)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UpdateDependencyTitleRegex();

    [GeneratedRegex("bump (?<dependency>@?[a-z0-9][a-z0-9._/@-]+) from", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BumpDependencyTitleRegex();

    [GeneratedRegex("`(?<dependency>@?[a-z0-9][a-z0-9._/@-]+)`", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BacktickDependencyTitleRegex();
}

public sealed record DependencyDetection(string? Name, string? Confidence)
{
    public static DependencyDetection None { get; } = new(null, null);
}
