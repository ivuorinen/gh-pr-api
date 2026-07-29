using System.Text.RegularExpressions;
using GhPrApi.GitHub;

namespace GhPrApi.Services;

public sealed partial class SecurityDetector
{
    public bool IsSecurity(GitHubPullRequest pullRequest, string? dependencyName)
    {
        var searchableText = string.Join(
            " ",
            pullRequest.Title,
            pullRequest.HeadRefName,
            pullRequest.Author?.Login,
            pullRequest.Author?.TypeName,
            dependencyName,
            string.Join(" ", pullRequest.Labels));

        return SecurityKeywordRegex().IsMatch(searchableText)
            || CveRegex().IsMatch(searchableText)
            || GhsaRegex().IsMatch(searchableText);
    }

    [GeneratedRegex("(?:vulnerab|security|advisory|dependabot security update|renovate vulnerability alert)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecurityKeywordRegex();

    [GeneratedRegex("\\bCVE-\\d{4}-\\d{4,}\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CveRegex();

    [GeneratedRegex("\\bGHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GhsaRegex();
}
