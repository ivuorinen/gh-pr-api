using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class PullRequestReportBuilderTests
{
    [Fact]
    public void Build_marks_an_unresolved_pull_request_as_unknown_ci()
    {
        var builder = TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var pullRequest = TestPullRequests.Create(number: 1) with { StatusUnresolved = true };

        var report = builder.Build("ivuorinen", [pullRequest], false, ["ivuorinen/example#1"]);

        var item = report.Groups.SelectMany(static g => g.PullRequests ?? []).Single();
        Assert.Equal(NormalizedValues.Ci.Unknown, item.Ci);
        Assert.True(report.Degraded);
        Assert.Equal(["ivuorinen/example#1"], report.Unresolved);
    }

    [Fact]
    public void Build_is_not_degraded_when_nothing_is_unresolved()
    {
        var builder = TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));

        var report = builder.Build("ivuorinen", [TestPullRequests.Create(number: 1)]);

        Assert.False(report.Degraded);
        Assert.Null(report.Unresolved);
    }

    [Fact]
    public void Unknown_ci_does_not_get_promoted_above_failing_or_stale()
    {
        var builder = TestSupport.CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));
        var unresolved = TestPullRequests.Create(number: 1) with { StatusUnresolved = true };
        var failing = TestPullRequests.Create(number: 2) with
        {
            StatusDetails = new GitHubPullRequestStatusDetails(
                [new GitHubStatusCheck("CheckRun", "build", "COMPLETED", "FAILURE", null, true)],
                [],
                RequiresStatusChecks: true),
        };

        var report = builder.Build("ivuorinen", [unresolved, failing]);

        var items = report.Groups.SelectMany(static g => g.PullRequests ?? []).ToArray();
        Assert.Equal(2, items[0].Number);
        Assert.Equal(NormalizedValues.Ci.Failing, items[0].Ci);
        Assert.Equal(NormalizedValues.Ci.Unknown, items[1].Ci);
    }

    [Fact]
    public void Build_returns_no_open_prs_message_when_empty()
    {
        var builder = CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));

        var report = builder.Build("ivuorinen", []);

        Assert.Equal(0, report.TotalCount);
        Assert.Empty(report.Groups);
        Assert.Equal("No open PRs.", report.Message);
    }

    [Fact]
    public void Build_groups_security_before_human_and_robots()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = CreateBuilder(now);
        var pullRequests = new[]
        {
            TestPullRequests.Create(
                number: 1,
                title: "Add feature",
                createdAt: now.AddDays(-1),
                authorLogin: "ivuorinen",
                authorType: "User"),
            TestPullRequests.Create(
                number: 2,
                title: "chore(deps): update eslint to v10",
                createdAt: now.AddDays(-2),
                authorLogin: "renovate[bot]",
                authorType: "Bot",
                headRefName: "renovate/eslint-10.0.0"),
            TestPullRequests.Create(
                number: 3,
                title: "fix CVE-2026-12345",
                createdAt: now.AddDays(-4),
                authorLogin: "dependabot[bot]",
                authorType: "Bot",
                headRefName: "dependabot/npm_and_yarn/example-1.2.3"),
        };

        var report = builder.Build("ivuorinen", pullRequests);

        Assert.Equal(3, report.TotalCount);
        Assert.Collection(
            report.Groups,
            security => Assert.Equal(NormalizedValues.Group.SecurityUpdatesKey, security.Key),
            human => Assert.Equal(NormalizedValues.Group.HumanPullRequestsKey, human.Key),
            robots => Assert.Equal(NormalizedValues.Group.RobotsKey, robots.Key));
    }

    [Fact]
    public void Build_combines_prefixes_in_required_order()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = CreateBuilder(now);
        var statusDetails = new GitHubPullRequestStatusDetails(
            [new GitHubStatusCheck("CheckRun", "build", "COMPLETED", "FAILURE", State: null, IsRequired: true)],
            ["build"],
            RequiresStatusChecks: true);
        var pullRequest = TestPullRequests.Create(
            title: "fix CVE-2026-12345",
            createdAt: now.AddDays(-4),
            isDraft: true,
            statusDetails: statusDetails);

        var report = builder.Build("ivuorinen", [pullRequest]);
        var item = Assert.Single(report.Groups[0].PullRequests!);

        Assert.Equal(
            new[] { NormalizedValues.Prefix.Security, NormalizedValues.Prefix.Failing, NormalizedValues.Prefix.Stale, NormalizedValues.Prefix.Draft },
            item.Prefixes);
    }

    [Fact]
    public void Build_groups_same_dependency_across_repositories()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = CreateBuilder(now);
        var first = TestPullRequests.Create(
            repositoryNameWithOwner: "ivuorinen/one",
            number: 1,
            authorLogin: "renovate[bot]",
            authorType: "Bot",
            headRefName: "renovate/eslint-10.0.0");
        var second = TestPullRequests.Create(
            repositoryNameWithOwner: "ivuorinen/two",
            number: 2,
            authorLogin: "renovate[bot]",
            authorType: "Bot",
            headRefName: "renovate/eslint-10.0.0");

        var report = builder.Build("ivuorinen", [first, second]);
        var robots = Assert.Single(report.Groups);
        var dependencyGroup = Assert.Single(robots.DependencyGroups!);

        Assert.Equal("eslint", dependencyGroup.DependencyName);
        Assert.Equal(2, dependencyGroup.PullRequests.Count);
    }

    [Fact]
    public void Build_passes_truncated_flag_through_to_the_report()
    {
        var builder = CreateBuilder(new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero));

        var truncatedEmpty = builder.Build("ivuorinen", [], truncated: true);
        var truncatedNonEmpty = builder.Build("ivuorinen", [TestPullRequests.Create()], truncated: true);

        Assert.True(truncatedEmpty.Truncated);
        Assert.True(truncatedNonEmpty.Truncated);
    }

    private static PullRequestReportBuilder CreateBuilder(DateTimeOffset now) => TestSupport.CreateBuilder(now);
}
