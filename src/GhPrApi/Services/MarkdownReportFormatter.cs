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
            if (!GroupHasContent(group))
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
                    builder.Append("### ").AppendLine(dependencyGroup.DependencyName);

                    foreach (var pullRequest in dependencyGroup.PullRequests)
                    {
                        AppendPullRequest(builder, pullRequest);
                    }
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool GroupHasContent(PullRequestGroup group)
    {
        if (group.PullRequests is { Count: > 0 })
        {
            return true;
        }

        if (group.DependencyGroups is null || group.DependencyGroups.Count == 0)
        {
            return false;
        }

        return group.DependencyGroups.Any(static dependencyGroup => dependencyGroup.PullRequests.Count > 0);
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
            .Append(pullRequest.Title)
            .Append(" — ")
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
}
