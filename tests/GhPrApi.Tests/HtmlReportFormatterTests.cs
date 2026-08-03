using System.Net;
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
    public void Format_puts_the_pr_column_first_and_flags_second()
    {
        // Nothing asserted column order before this, so the reorder would have gone green
        // silently. Both the header row and the body row are pinned: emitting them out of step
        // with each other is the failure this guards, and the nth-child widths in the style
        // block are keyed to this exact order.
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = TestSupport.CreateBuilder(now);
        var pullRequest = TestPullRequests.Create(
            number: 1,
            title: "Add feature",
            createdAt: now.AddDays(-4),
            authorLogin: "ivuorinen");
        var report = builder.Build("ivuorinen", [pullRequest]);

        var html = new HtmlReportFormatter().Format(report);

        Assert.Contains(
            "<tr><th scope=\"col\">PR</th><th scope=\"col\">Flags</th><th scope=\"col\">Author</th>"
            + "<th scope=\"col\">Age</th><th scope=\"col\">Review</th><th scope=\"col\">CI</th>"
            + "<th scope=\"col\">Branch</th></tr>",
            html,
            StringComparison.Ordinal);

        // Expected flags are read off the built report rather than written in literally. This
        // test's subject is which cell the value lands in, so hard-coding "STALE" would make a
        // change to the stale threshold fail a column-order test -- a failure pointing at the
        // wrong cause. Derived this way it still proves the value reached the prefixes cell, and
        // it keeps proving it if the flag rules change.
        var item = report.Groups.SelectMany(static g => g.PullRequests ?? []).First(static i => i.Number == 1);
        var expectedFlags = string.Join(' ', item.Prefixes);

        Assert.Contains(
            "<tr><td>ivuorinen/example#1: <a href=\"https://github.com/ivuorinen/example/pull/1\">Add feature</a></td>"
            + $"<td class=\"prefixes\">{WebUtility.HtmlEncode(expectedFlags)}</td>",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Format_fixes_the_table_layout_so_separate_tables_line_up()
    {
        // Widths alone do not align anything: under the auto algorithm each table sizes its
        // columns from its own content and may override width outright. table-layout: fixed is
        // the load-bearing declaration, and the widths are keyed to column position.
        var report = new PullRequestReport(
            "ivuorinen",
            new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
            TotalCount: 0,
            Groups: []);

        var html = new HtmlReportFormatter().Format(report);

        Assert.Contains("table-layout: fixed", html, StringComparison.Ordinal);
        Assert.Contains("th:nth-child(2) { width: 7.5rem; }", html, StringComparison.Ordinal);
        Assert.Contains("th:nth-child(7) { width: 7.5rem; }", html, StringComparison.Ordinal);

        // Column 1 is PR and must stay unset so it absorbs the remaining width.
        Assert.DoesNotContain("th:nth-child(1)", html, StringComparison.Ordinal);

        // All four flags at once is 28 characters; nowrap would overflow the fixed column.
        Assert.DoesNotContain("white-space: nowrap", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_follows_the_browser_light_dark_preference()
    {
        // The page set no link colour and inherited the user agent's, which is 1.96:1 against
        // this dark background (1.67:1 visited). A dark mode that flipped only background and
        // text would therefore ship a contrast regression, so the link colours are asserted
        // explicitly rather than left to the UA.
        var report = new PullRequestReport(
            "ivuorinen",
            new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero),
            TotalCount: 0,
            Groups: []);

        var html = new HtmlReportFormatter().Format(report);

        Assert.Contains("color-scheme: light dark;", html, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme: dark)", html, StringComparison.Ordinal);
        Assert.Contains("--link: #8ab4f8;", html, StringComparison.Ordinal);
        Assert.Contains("--link-visited: #d0bcff;", html, StringComparison.Ordinal);

        // Every colour routes through a custom property; a literal left behind in a themed
        // selector is a value the dark override silently misses.
        foreach (var themed in new[] { "background: var(--bg)", "color: var(--fg)", "color: var(--link)", "solid var(--focus)" })
        {
            Assert.Contains(themed, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Format_makes_each_scrolling_table_keyboard_reachable_and_named()
    {
        // The overflow container holds no focusable content past the PR link, so without
        // tabindex a keyboard user cannot scroll to the CI and Branch columns (WCAG 2.2 SC
        // 2.1.1). The label separates the several identical-looking tables on one page.
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var builder = TestSupport.CreateBuilder(now);
        var pullRequest = TestPullRequests.Create(
            number: 2,
            createdAt: now.AddDays(-5),
            authorLogin: "renovate[bot]",
            authorType: "Bot",
            headRefName: "renovate/eslint-10.0.0");
        var report = builder.Build("ivuorinen", [pullRequest]);

        var html = new HtmlReportFormatter().Format(report);

        // Labels are read off the report, not written in literally: "Easy wins" appears here
        // only because this PR happens to satisfy the easy-wins rule, so hard-coding it would
        // make a change to that rule fail an accessibility test. Walking the groups also makes
        // the assertion stronger -- every rendered table must have a labelled region, not just
        // the two this fixture happens to produce.
        var expectedLabels = report.Groups
            .SelectMany(static group => group.PullRequests is { Count: > 0 }
                ? [group.Title]
                : (group.DependencyGroups ?? [])
                    .Where(static dependencyGroup => dependencyGroup.PullRequests.Count > 0)
                    .Select(static dependencyGroup => dependencyGroup.DependencyName))
            .ToArray();

        Assert.NotEmpty(expectedLabels);

        foreach (var label in expectedLabels)
        {
            // Encoded to match the formatter. A dependency name is derived from a head ref via
            // an unrestricted capture, so it can legitimately contain & < or " -- comparing
            // against the raw value would fail on correct markup.
            Assert.Contains(
                $"<div class=\"table-wrap\" tabindex=\"0\" role=\"region\" aria-label=\"{WebUtility.HtmlEncode(label)}\">",
                html,
                StringComparison.Ordinal);
        }

        // One labelled region per rendered table, so a table can never slip out unlabelled.
        Assert.Equal(
            expectedLabels.Length,
            html.Split("<div class=\"table-wrap\"", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Format_html_encodes_the_region_label()
    {
        // A dependency name comes from the head ref through an unrestricted capture
        // (^dependabot/[^/]+/(?<dependency>.+)$), so a fork PR can put markup characters into a
        // group label. The label reaches an attribute value, which nothing covered before.
        var now = new DateTimeOffset(2026, 7, 6, 12, 0, 0, TimeSpan.Zero);
        var pullRequest = TestPullRequests.Create(
            number: 1,
            createdAt: now,
            authorLogin: "dependabot[bot]",
            authorType: "Bot",
            headRefName: "dependabot/npm_and_yarn/\"><script>");
        var report = TestSupport.CreateBuilder(now).Build("ivuorinen", [pullRequest]);

        var html = new HtmlReportFormatter().Format(report);

        var dependencyName = report.Groups
            .SelectMany(static group => group.DependencyGroups ?? [])
            .Select(static dependencyGroup => dependencyGroup.DependencyName)
            .Single();

        // Guards the premise: if detection stops producing a name needing encoding, this test
        // would otherwise keep passing while covering nothing.
        Assert.Contains("<", dependencyName, StringComparison.Ordinal);

        Assert.DoesNotContain($"aria-label=\"{dependencyName}\"", html, StringComparison.Ordinal);
        Assert.Contains($"aria-label=\"{WebUtility.HtmlEncode(dependencyName)}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
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
