namespace GhPrApi.Models;

public static class NormalizedValues
{
    public static class Review
    {
        public const string Approved = "approved";
        public const string ChangesRequested = "changes requested";
        public const string AwaitingReview = "awaiting review";
    }

    public static class Ci
    {
        public const string Passing = "passing";
        public const string Failing = "failing";
        public const string Pending = "pending";
    }

    public static class Branch
    {
        public const string UpToDate = "up to date";
        public const string Behind = "behind";
        public const string Conflict = "conflict";
        public const string Unknown = "unknown";
    }

    public static class Prefix
    {
        public const string Security = "SECURITY";
        public const string Failing = "FAILING";
        public const string Stale = "STALE";
        public const string Draft = "DRAFT";
    }

    public static class Group
    {
        public const string SecurityUpdatesKey = "security-updates";
        public const string SecurityUpdatesTitle = "Security updates";
        public const string HumanPullRequestsKey = "human-prs";
        public const string HumanPullRequestsTitle = "Human PRs";
        public const string RobotsKey = "robots";
        public const string RobotsTitle = "Robots";
    }

    public static class Dependency
    {
        public const string MultipleDependencies = "Multiple dependencies";
        public const string OtherRobotPullRequests = "Other robot PRs";
    }
}
