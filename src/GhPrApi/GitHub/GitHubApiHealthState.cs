namespace GhPrApi.GitHub;

public sealed class GitHubApiHealthState
{
    private volatile bool _isHealthy = true;

    public bool IsHealthy => _isHealthy;

    public void RecordSuccess() => _isHealthy = true;

    public void RecordFailure() => _isHealthy = false;
}
