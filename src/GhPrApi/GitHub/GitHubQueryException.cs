namespace GhPrApi.GitHub;

public sealed class GitHubQueryException : Exception
{
    public GitHubQueryException(string message)
        : base(message)
    {
    }

    public GitHubQueryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
