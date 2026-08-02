using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class MarkdownReportFormatterTests
{
    [Fact]
    public void Format_returns_exact_no_open_prs_message_when_empty()
    {
        var formatter = new MarkdownReportFormatter();
        var report = new PullRequestReport(
            "ivuorinen",
            new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
            TotalCount: 0,
            Groups: [],
            Message: "No open PRs.");

        var markdown = formatter.Format(report);

        Assert.Equal("No open PRs.", markdown);
    }

    [Fact]
    public void Format_outputs_compact_markdown_with_groups_dependency_groups_and_prefixes()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = CreateBuilder(now);
        var statusDetails = new GitHubPullRequestStatusDetails(
            [new GitHubStatusCheck("CheckRun", "build", "COMPLETED", "FAILURE", State: null, IsRequired: true)],
            ["build"],
            RequiresStatusChecks: true);
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
                createdAt: now.AddDays(-5),
                authorLogin: "renovate[bot]",
                authorType: "Bot",
                headRefName: "renovate/eslint-10.0.0",
                statusDetails: statusDetails),
        };
        var report = builder.Build("ivuorinen", pullRequests);
        var formatter = new MarkdownReportFormatter();

        var markdown = formatter.Format(report);

        Assert.Contains("## Human PRs", markdown);
        Assert.Contains("- ivuorinen/example#1: Add feature — ivuorinen — open 1d — review: awaiting review — ci: passing — branch: up to date — https://github.com/ivuorinen/example/pull/1", markdown);
        Assert.Contains("## Robots", markdown);
        Assert.Contains("### eslint", markdown);
        Assert.Contains("- [FAILING STALE] ivuorinen/example#2: chore(deps): update eslint to v10 — renovate[bot] — open 5d — review: awaiting review — ci: failing — branch: up to date — https://github.com/ivuorinen/example/pull/2", markdown);
    }

    [Fact]
    public void Format_puts_easy_wins_first()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = CreateBuilder(now);
        var pullRequests = new[]
        {
            TestPullRequests.Create(number: 1, title: "Add feature", createdAt: now.AddDays(-1)),
            TestPullRequests.Create(
                number: 2,
                title: "fix CVE-2026-12345",
                createdAt: now.AddDays(-2),
                authorLogin: "dependabot[bot]",
                authorType: "Bot",
                headRefName: "dependabot/npm_and_yarn/example-1.2.3"),
        };
        var report = builder.Build("ivuorinen", pullRequests);
        var formatter = new MarkdownReportFormatter();

        var markdown = formatter.Format(report);

        Assert.StartsWith("## Easy wins", markdown);
        Assert.True(markdown.IndexOf("## Easy wins", StringComparison.Ordinal) < markdown.IndexOf("## Security updates", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("## Security updates", StringComparison.Ordinal) < markdown.IndexOf("## Human PRs", StringComparison.Ordinal));
    }

    private static PullRequestReportBuilder CreateBuilder(DateTimeOffset now) => TestSupport.CreateBuilder(now);
}
