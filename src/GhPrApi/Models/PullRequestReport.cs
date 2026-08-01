using System.Text.Json.Serialization;

namespace GhPrApi.Models;

public sealed record PullRequestReport(
    string Owner,
    DateTimeOffset GeneratedAt,
    int TotalCount,
    IReadOnlyList<PullRequestGroup> Groups,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message = null,
    // Truncated and Degraded are independent and may both be true: Truncated means a configured
    // limit cut the data short, Degraded means some status lookups failed.
    bool Truncated = false,
    bool Degraded = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Unresolved = null);

public sealed record PullRequestGroup(
    string Key,
    string Title,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PullRequestItem>? PullRequests = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DependencyPullRequestGroup>? DependencyGroups = null)
{
    public bool HasContent()
    {
        if (PullRequests is { Count: > 0 })
        {
            return true;
        }

        return DependencyGroups?.Any(static dependencyGroup => dependencyGroup.PullRequests.Count > 0) ?? false;
    }
}

public sealed record DependencyPullRequestGroup(
    string DependencyName,
    IReadOnlyList<PullRequestItem> PullRequests);

public sealed record PullRequestItem(
    string Id,
    string Repo,
    int Number,
    string Title,
    string Author,
    string AuthorType,
    DateTimeOffset CreatedAt,
    string Age,
    int AgeDays,
    bool IsDraft,
    bool IsSecurity,
    bool IsRobot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DependencyName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DependencyConfidence,
    string Review,
    string Ci,
    string Branch,
    string HeadRefName,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Prefixes,
    string Url);
