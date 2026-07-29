using GhPrApi.Models;

namespace GhPrApi.Services;

public interface IPullRequestReportService
{
    Task<PullRequestReport> GetOpenPullRequestsAsync(
        string? ownerOverride,
        bool refresh,
        CancellationToken cancellationToken);
}
