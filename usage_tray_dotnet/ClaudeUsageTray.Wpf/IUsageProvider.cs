namespace ClaudeUsageTray;

public interface IUsageProvider
{
    string Name { get; }
    string HomeUrl { get; }
    string LoginUrl { get; }
    string ProfileFolderName { get; }

    /// <summary>Runs the fetch script against the given host and parses the result.</summary>
    Task<UsageSnapshot> FetchAsync(WebViewUsageHost host);
}
