namespace GhPrApi.Options;

public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";

    public string Owner { get; set; } = string.Empty;

    public string? Token { get; set; }

    public int CacheTtlSeconds { get; set; } = 300;

    public int RepositoryLimit { get; set; } = 1_000;

    public int PullRequestLimitPerRepository { get; set; } = 100;

    public int StatusCheckLimitPerPullRequest { get; set; } = 100;
}
