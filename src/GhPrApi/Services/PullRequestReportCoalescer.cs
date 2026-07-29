using System.Collections.Concurrent;
using GhPrApi.Models;

namespace GhPrApi.Services;

public sealed class PullRequestReportCoalescer
{
    private readonly ConcurrentDictionary<string, Lazy<Task<PullRequestReport>>> _inFlight = new();

    public Task<PullRequestReport> GetOrAddAsync(string key, Func<Task<PullRequestReport>> factory)
    {
        var lazy = _inFlight.GetOrAdd(key, _ => new Lazy<Task<PullRequestReport>>(() => RunAndRemoveAsync(key, factory)));

        return lazy.Value;
    }

    private async Task<PullRequestReport> RunAndRemoveAsync(string key, Func<Task<PullRequestReport>> factory)
    {
        try
        {
            return await factory().ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }
}
