using GhPrApi.GitHub;

namespace GhPrApi.Services;

public sealed class RobotDetector
{
    private static readonly string[] KnownRobotIndicators =
    [
        "[bot]",
        "dependabot",
        "renovate",
        "github-actions",
        "github-actions[bot]",
        "mergify",
        "pre-commit-ci",
    ];

    public bool IsRobot(GitHubPullRequest pullRequest)
    {
        var author = pullRequest.Author;
        if (author is null)
        {
            return false;
        }

        if (author.TypeName.Equals("Bot", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return KnownRobotIndicators.Any(indicator =>
            author.Login.Contains(indicator, StringComparison.OrdinalIgnoreCase));
    }
}
