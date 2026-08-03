using System.Net;
using System.Text;
using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class HtmlReportFormatter
{
    // Every colour is a custom property so the dark override sets values, not selectors --
    // duplicating the selector list in the media query is where a missed colour hides.
    // Ratios in the comments are WCAG relative luminance against the matching background;
    // text needs 4.5:1 (SC 1.4.3), the focus outline 3:1 as a non-text component (SC 1.4.11).
    private const string Style = """
        <style>
          :root {
            /* Lets the UA theme the scroll bar of the focusable .table-wrap region, and the
               canvas, instead of painting a bright bar against a dark table. */
            color-scheme: light dark;
            --bg: #ffffff;
            --fg: #1a1a1a;          /* 17.40:1 */
            --muted: #555555;       /* 7.46:1  */
            --border: #eeeeee;      /* decorative separator, no contrast requirement */
            --flag: #b3261e;        /* 6.54:1  */
            --link: #0645c8;        /* 7.80:1  */
            --link-visited: #6b21a8;/* 8.72:1  */
            --focus: #1a73e8;       /* 4.51:1  */
          }
          @media (prefers-color-scheme: dark) {
            :root {
              --bg: #12141a;
              --fg: #e6e6e6;          /* 14.75:1 */
              --muted: #a8b0bd;       /* 8.42:1  */
              --border: #2a2f3a;
              --flag: #ff8a80;        /* 8.06:1  */
              --link: #8ab4f8;        /* 8.74:1  */
              --link-visited: #d0bcff;/* 10.80:1 */
              --focus: #8ab4f8;       /* 8.74:1  */
            }
          }
          body { font-family: system-ui, sans-serif; margin: 2rem; background: var(--bg); color: var(--fg); }
          h1 { font-size: 1.4rem; }
          h2 { font-size: 1.15rem; margin-top: 1.75rem; }
          h3 { font-size: 1rem; color: var(--muted); margin-top: 1rem; }
          a { color: var(--link); }
          a:visited { color: var(--link-visited); }
          .table-wrap { overflow-x: auto; margin-top: 0.5rem; }
          .table-wrap:focus-visible { outline: 2px solid var(--focus); outline-offset: 2px; }
          /* Fixed, not auto: under the auto algorithm each table sizes its columns from its own
             content, so the tables on one page never line up with each other -- and width is
             only a hint the algorithm may override. */
          table { border-collapse: collapse; width: 100%; table-layout: fixed; }
          th, td { padding: 0.4rem 0.6rem; border-bottom: 1px solid var(--border); text-align: left; vertical-align: top; }
          /* A fixed layout clips rather than grows, so a pathological unbroken token in a title
             has to be allowed to break. */
          td { overflow-wrap: anywhere; }
          th { color: var(--muted); font-size: 0.85rem; text-transform: uppercase; letter-spacing: 0.02em; }
          /* Keyed to TableHeaders. Column 1 (PR) is deliberately unset: it takes the remainder. */
          th:nth-child(2) { width: 7.5rem; }  /* Flags  */
          th:nth-child(3) { width: 10rem; }   /* Author */
          th:nth-child(4) { width: 4rem; }    /* Age    */
          th:nth-child(5) { width: 9rem; }    /* Review */
          th:nth-child(6) { width: 5.5rem; }  /* CI     */
          th:nth-child(7) { width: 7.5rem; }  /* Branch */
          /* No nowrap: all four flags at once is "SECURITY FAILING STALE DRAFT", 28 characters,
             which would overflow the fixed column and undo the alignment above. */
          .prefixes { font-weight: 600; color: var(--flag); }
          .meta { color: var(--muted); }
          .empty, .error { color: var(--muted); }
        </style>
        """;

    // PR first, flags second: the identifier is what a reader scans down, and the flag column is
    // empty for most rows. The nth-child widths in the style block are keyed to this order.
    private static readonly string[] TableHeaders = ["PR", "Flags", "Author", "Age", "Review", "CI", "Branch"];

    public string Format(PullRequestReport report)
    {
        var builder = new StringBuilder();
        AppendHead(builder, $"Open Pull Requests — {Encode(report.Owner)}");
        builder.Append("<h1>Open Pull Requests — ").Append(Encode(report.Owner)).Append("</h1>\n");

        if (report.Truncated)
        {
            builder.Append("<p class=\"meta\">Results may be incomplete: a configured limit was reached.</p>\n");
        }

        if (report.Degraded)
        {
            builder.Append("<p class=\"meta\">Some pull requests could not be checked; their CI shows as unknown.</p>\n");
        }

        if (report.TotalCount == 0)
        {
            builder.Append("<p class=\"empty\">No open PRs.</p>\n");
        }
        else
        {
            foreach (var group in report.Groups)
            {
                if (!group.HasContent())
                {
                    continue;
                }

                builder.Append("<h2>").Append(Encode(group.Title)).Append("</h2>\n");

                if (group.PullRequests is { Count: > 0 })
                {
                    AppendTable(builder, group.PullRequests, group.Title);
                }

                if (group.DependencyGroups is { Count: > 0 })
                {
                    foreach (var dependencyGroup in group.DependencyGroups.Where(static d => d.PullRequests.Count > 0))
                    {
                        builder.Append("<h3>").Append(Encode(dependencyGroup.DependencyName)).Append("</h3>\n");
                        AppendTable(builder, dependencyGroup.PullRequests, dependencyGroup.DependencyName);
                    }
                }
            }
        }

        AppendFoot(builder);
        return builder.ToString();
    }

    public static string FormatError(string message)
    {
        var builder = new StringBuilder();
        AppendHead(builder, "Error");
        builder.Append("<p class=\"error\">").Append(Encode(message)).Append("</p>\n");
        AppendFoot(builder);
        return builder.ToString();
    }

    private static void AppendHead(StringBuilder builder, string title)
    {
        builder.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        builder.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        builder.Append("<title>").Append(title).Append("</title>\n");
        builder.Append(Style).Append('\n');
        builder.Append("</head>\n<body>\n");
    }

    private static void AppendFoot(StringBuilder builder) => builder.Append("</body>\n</html>\n");

    private static void AppendTable(StringBuilder builder, IReadOnlyList<PullRequestItem> pullRequests, string label)
    {
        // tabindex + role/aria-label, not a bare div: the only focusable content in a row is the
        // PR link in column 2 of 7, so on a viewport narrow enough to trigger the overflow a
        // keyboard user could never scroll to Review, CI or Branch -- WCAG 2.2 SC 2.1.1. The
        // label distinguishes the several identical-looking tables on one page.
        builder.Append("<div class=\"table-wrap\" tabindex=\"0\" role=\"region\" aria-label=\"")
            .Append(Encode(label))
            .Append("\">\n<table>\n<thead>\n<tr>");
        foreach (var header in TableHeaders)
        {
            builder.Append("<th scope=\"col\">").Append(header).Append("</th>");
        }

        builder.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var pullRequest in pullRequests)
        {
            builder.Append("<tr>");
            builder.Append("<td>").Append(Encode(pullRequest.Id)).Append(": ");
            builder.Append("<a href=\"").Append(Encode(pullRequest.Url)).Append("\">").Append(Encode(pullRequest.Title)).Append("</a></td>");
            builder.Append("<td class=\"prefixes\">").Append(Encode(string.Join(' ', pullRequest.Prefixes))).Append("</td>");
            builder.Append("<td>").Append(Encode(pullRequest.Author)).Append("</td>");
            builder.Append("<td>").Append(Encode(pullRequest.Age)).Append("</td>");
            builder.Append("<td>").Append(Encode(pullRequest.Review)).Append("</td>");
            builder.Append("<td>").Append(Encode(pullRequest.Ci)).Append("</td>");
            builder.Append("<td>").Append(Encode(pullRequest.Branch)).Append("</td>");
            builder.Append("</tr>\n");
        }

        builder.Append("</tbody>\n</table>\n</div>\n");
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
