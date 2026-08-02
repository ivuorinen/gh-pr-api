using System.Text;
using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class MarkdownReportFormatter
{
    public string Format(PullRequestReport report)
    {
        if (report.TotalCount == 0)
        {
            return "No open PRs.";
        }

        var builder = new StringBuilder();
        var firstSection = true;

        foreach (var group in report.Groups)
        {
            if (!group.HasContent())
            {
                continue;
            }

            AppendSectionSeparator(builder, ref firstSection);
            builder.Append("## ").AppendLine(group.Title);

            if (group.PullRequests is { Count: > 0 })
            {
                foreach (var pullRequest in group.PullRequests)
                {
                    AppendPullRequest(builder, pullRequest);
                }
            }

            if (group.DependencyGroups is { Count: > 0 })
            {
                var firstDependencyGroup = true;
                foreach (var dependencyGroup in group.DependencyGroups.Where(static dependencyGroup => dependencyGroup.PullRequests.Count > 0))
                {
                    if (!firstDependencyGroup || group.PullRequests is { Count: > 0 })
                    {
                        builder.AppendLine();
                    }

                    firstDependencyGroup = false;
                    builder.Append("### ").AppendLine(Escape(dependencyGroup.DependencyName));

                    foreach (var pullRequest in dependencyGroup.PullRequests)
                    {
                        AppendPullRequest(builder, pullRequest);
                    }
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSectionSeparator(StringBuilder builder, ref bool firstSection)
    {
        if (!firstSection)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        firstSection = false;
    }

    private static void AppendPullRequest(StringBuilder builder, PullRequestItem pullRequest)
    {
        builder.Append("- ");

        if (pullRequest.Prefixes.Count > 0)
        {
            builder.Append('[')
                .AppendJoin(' ', pullRequest.Prefixes)
                .Append("] ");
        }

        builder
            .Append(pullRequest.Id)
            .Append(": ")
            .Append(Escape(pullRequest.Title))
            .Append(" — ")
            // Not escaped: GitHub logins are alphanumerics and hyphens, plus the "[bot]" suffix
            // GitHub itself appends. The only thing "[bot]" could become is a shortcut reference
            // link, and that needs a "[bot]: url" definition, which the escaped title above can
            // no longer smuggle in. Escaping it would backslash every robot PR for nothing.
            .Append(pullRequest.Author)
            .Append(" — open ")
            .Append(pullRequest.Age)
            .Append(" — review: ")
            .Append(pullRequest.Review)
            .Append(" — ci: ")
            .Append(pullRequest.Ci)
            .Append(" — branch: ")
            .Append(pullRequest.Branch)
            .Append(" — ")
            .AppendLine(pullRequest.Url);
    }

    /// <summary>
    /// Neutralises GitHub-sourced text for Markdown output.
    /// </summary>
    /// <remarks>
    /// PR titles on public repositories are attacker-controlled -- any outside contributor picks
    /// one -- and most Markdown renderers pass raw HTML straight through, so an unescaped title
    /// is an XSS vector in whatever consumes this. The HTML formatter already encodes; this is
    /// the same guarantee for the other text format.
    /// Only the characters that introduce markup or break the documented one-PR-per-bullet shape
    /// are escaped. `*` and `_` are deliberately left alone: neither can smuggle HTML, intraword
    /// `_` is not emphasis in CommonMark, and escaping them would mangle ordinary package names.
    /// </remarks>
    private static string Escape(string value)
    {
        var escaped = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (character is '<' or '>' or '[' or ']' or '`' or '\\')
            {
                escaped.Append('\\');
            }

            escaped.Append(character is '\r' or '\n' ? ' ' : character);
        }

        return escaped.ToString();
    }
}
