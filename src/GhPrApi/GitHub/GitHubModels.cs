namespace GhPrApi.GitHub;

public sealed record GitHubActor(
    string Login,
    string TypeName);

public sealed record GitHubOpenPullRequestsResult(
    IReadOnlyList<GitHubPullRequest> PullRequests,
    bool Truncated);

public sealed record GitHubPullRequest(
    string Id,
    string RepositoryNameWithOwner,
    string RepositoryOwner,
    string RepositoryName,
    int Number,
    string Title,
    Uri Url,
    DateTimeOffset CreatedAt,
    bool IsDraft,
    string? ReviewDecision,
    string? MergeStateStatus,
    string? Mergeable,
    string HeadRefName,
    string HeadRefOid,
    string BaseRefName,
    GitHubActor? Author,
    IReadOnlyList<string> Labels,
    GitHubPullRequestStatusDetails? StatusDetails = null,
    bool StatusUnresolved = false);

// RequiredStatusCheckNames is a list, not a set: HybridCache's L2 serializes every cached
// value and System.Text.Json cannot deserialize IReadOnlySet<T>. De-duplication still happens
// in the HashSet inside GitHubGraphQlClient before the conversion.
public sealed record GitHubPullRequestStatusDetails(
    IReadOnlyList<GitHubStatusCheck> StatusChecks,
    IReadOnlyList<string> RequiredStatusCheckNames,
    bool RequiresStatusChecks);

public sealed record GitHubStatusCheck(
    string TypeName,
    string DisplayName,
    string? Status,
    string? Conclusion,
    string? State,
    bool IsRequired);
