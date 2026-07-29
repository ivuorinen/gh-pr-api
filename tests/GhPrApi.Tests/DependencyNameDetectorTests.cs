using GhPrApi.Models;
using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class DependencyNameDetectorTests
{
    private readonly DependencyNameDetector _detector = new();

    [Fact]
    public void Detect_reads_dependabot_dependency_from_branch()
    {
        var pr = TestPullRequests.Create(
            title: "Bump actions/checkout from 4 to 5",
            authorLogin: "dependabot[bot]",
            authorType: "Bot",
            headRefName: "dependabot/github_actions/actions/checkout-5");

        var result = _detector.Detect(pr, isRobot: true);

        Assert.Equal("actions/checkout", result.Name);
        Assert.Equal("high", result.Confidence);
    }

    [Fact]
    public void Detect_reads_renovate_dependency_from_branch()
    {
        var pr = TestPullRequests.Create(
            title: "chore(deps): update eslint-config-prettier to v10",
            authorLogin: "renovate[bot]",
            authorType: "Bot",
            headRefName: "renovate/eslint-config-prettier-10.0.0");

        var result = _detector.Detect(pr, isRobot: true);

        Assert.Equal("eslint-config-prettier", result.Name);
        Assert.Equal("high", result.Confidence);
    }

    [Fact]
    public void Detect_returns_multiple_dependencies_for_lockfile_maintenance()
    {
        var pr = TestPullRequests.Create(
            title: "chore(deps): lock file maintenance",
            authorLogin: "renovate[bot]",
            authorType: "Bot",
            headRefName: "renovate/lock-file-maintenance");

        var result = _detector.Detect(pr, isRobot: true);

        Assert.Equal(NormalizedValues.Dependency.MultipleDependencies, result.Name);
        Assert.Equal("high", result.Confidence);
    }

    [Fact]
    public void Detect_returns_none_for_human_pr()
    {
        var pr = TestPullRequests.Create(
            title: "Add feature",
            authorLogin: "ivuorinen",
            authorType: "User",
            headRefName: "feature/example");

        var result = _detector.Detect(pr, isRobot: false);

        Assert.Null(result.Name);
        Assert.Null(result.Confidence);
    }
}
