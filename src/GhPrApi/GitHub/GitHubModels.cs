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
    string BaseRefName,
    GitHubActor? Author,
    IReadOnlyList<string> Labels,
    GitHubPullRequestStatusDetails? StatusDetails = null);

public sealed record GitHubPullRequestStatusDetails(
    IReadOnlyList<GitHubStatusCheck> StatusChecks,
    IReadOnlySet<string> RequiredStatusCheckNames,
    bool RequiresStatusChecks);

public sealed record GitHubStatusCheck(
    string TypeName,
    string DisplayName,
    string? Status,
    string? Conclusion,
    string? State,
    bool IsRequired);
