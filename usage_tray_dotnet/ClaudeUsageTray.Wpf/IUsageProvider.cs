namespace ClaudeUsageTray;

public interface IUsageProvider
{
    string Name { get; }
    string HomeUrl { get; }
    string LoginUrl { get; }
    string ProfileFolderName { get; }

    /// <summary>
    /// False for providers that can never succeed. Without this,
    /// RefreshAllAsync's "not ok and not already showing login" check
    /// pops the login window open again on every single refresh cycle
    /// forever, since there's no login that could ever make it succeed.
    /// </summary>
    bool SupportsLogin => true;

    /// <summary>Runs the fetch script against the given host and parses the result.</summary>
    Task<UsageSnapshot> FetchAsync(WebViewUsageHost host);
}
