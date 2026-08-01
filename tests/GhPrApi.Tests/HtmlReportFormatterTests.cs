using GhPrApi.GitHub;
using GhPrApi.Models;
using GhPrApi.Services;
using Xunit;

namespace GhPrApi.Tests;

public sealed class HtmlReportFormatterTests
{
    [Fact]
    public void Format_notes_a_degraded_report()
    {
        var formatter = new HtmlReportFormatter();
        var report = new PullRequestReport(
            "ivuorinen",
            new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
            TotalCount: 0,
            Groups: [],
            Message: "No open PRs.",
            Truncated: false,
            Degraded: true,
            Unresolved: ["ivuorinen/example#1"]);

        var html = formatter.Format(report);

        Assert.Contains("Some pull requests could not be checked", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_returns_no_open_prs_paragraph_when_empty()
    {
        var formatter = new HtmlReportFormatter();
        var report = new PullRequestReport(
            "ivuorinen",
            new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
            TotalCount: 0,
            Groups: [],
            Message: "No open PRs.");

        var html = formatter.Format(report);

        Assert.Contains("<p class=\"empty\">No open PRs.</p>", html);
        Assert.StartsWith("<!DOCTYPE html>", html);
    }

    [Fact]
    public void Format_renders_groups_dependency_groups_and_links()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = TestSupport.CreateBuilder(now);
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
                headRefName: "renovate/eslint-10.0.0"),
        };
        var report = builder.Build("ivuorinen", pullRequests);
        var formatter = new HtmlReportFormatter();

        var html = formatter.Format(report);

        Assert.Contains("<h2>Human PRs</h2>", html);
        Assert.Contains("<h2>Robots</h2>", html);
        Assert.Contains("<h3>eslint</h3>", html);
        Assert.Contains("<a href=\"https://github.com/ivuorinen/example/pull/1\">Add feature</a>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<th scope=\"col\">Author</th>", html);
        Assert.Contains("<td>ivuorinen</td>", html);
    }

    [Fact]
    public void Format_html_encodes_untrusted_github_content()
    {
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = TestSupport.CreateBuilder(now);
        var pullRequest = TestPullRequests.Create(
            title: "<script>alert(1)</script>",
            createdAt: now,
            authorLogin: "attacker\"<img src=x>");
        var report = builder.Build("ivuorinen", [pullRequest]);
        var formatter = new HtmlReportFormatter();

        var html = formatter.Format(report);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<img src=x>", html);
    }

    [Fact]
    public void FormatError_returns_an_html_page_with_the_encoded_message()
    {
        var html = HtmlReportFormatter.FormatError("Unable to query GitHub.");

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<p class=\"error\">Unable to query GitHub.</p>", html);
    }
}
