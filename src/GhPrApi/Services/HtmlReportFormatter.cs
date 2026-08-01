using System.Net;
using System.Text;
using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class HtmlReportFormatter
{
    private const string Style = """
        <style>
          body { font-family: system-ui, sans-serif; margin: 2rem; color: #1a1a1a; }
          h1 { font-size: 1.4rem; }
          h2 { font-size: 1.15rem; margin-top: 1.75rem; }
          h3 { font-size: 1rem; color: #555; margin-top: 1rem; }
          .table-wrap { overflow-x: auto; margin-top: 0.5rem; }
          table { border-collapse: collapse; width: 100%; }
          th, td { padding: 0.4rem 0.6rem; border-bottom: 1px solid #eee; text-align: left; vertical-align: top; }
          th { color: #555; font-size: 0.85rem; text-transform: uppercase; letter-spacing: 0.02em; }
          .prefixes { font-weight: 600; color: #b3261e; white-space: nowrap; }
          .meta { color: #555; }
          .empty, .error { color: #555; }
        </style>
        """;

    private static readonly string[] TableHeaders = ["Flags", "PR", "Author", "Age", "Review", "CI", "Branch"];

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
                    AppendTable(builder, group.PullRequests);
                }

                if (group.DependencyGroups is { Count: > 0 })
                {
                    foreach (var dependencyGroup in group.DependencyGroups.Where(static d => d.PullRequests.Count > 0))
                    {
                        builder.Append("<h3>").Append(Encode(dependencyGroup.DependencyName)).Append("</h3>\n");
                        AppendTable(builder, dependencyGroup.PullRequests);
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

    private static void AppendTable(StringBuilder builder, IReadOnlyList<PullRequestItem> pullRequests)
    {
        builder.Append("<div class=\"table-wrap\">\n<table>\n<thead>\n<tr>");
        foreach (var header in TableHeaders)
        {
            builder.Append("<th scope=\"col\">").Append(header).Append("</th>");
        }

        builder.Append("</tr>\n</thead>\n<tbody>\n");

        foreach (var pullRequest in pullRequests)
        {
            builder.Append("<tr>");
            builder.Append("<td class=\"prefixes\">").Append(Encode(string.Join(' ', pullRequest.Prefixes))).Append("</td>");
            builder.Append("<td>").Append(Encode(pullRequest.Id)).Append(": ");
            builder.Append("<a href=\"").Append(Encode(pullRequest.Url)).Append("\">").Append(Encode(pullRequest.Title)).Append("</a></td>");
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
