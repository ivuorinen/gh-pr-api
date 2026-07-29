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
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "build" },
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

    private static PullRequestReportBuilder CreateBuilder(DateTimeOffset now) => TestSupport.CreateBuilder(now);
}
