namespace ScreenSorter.Core.Vision;

/// <summary>
/// Decides whether screenshots may be sent to an LM Studio address. Screenshots can show chats, documents or
/// passwords, so they stay on this machine unless the user allows another computer, and then only over https.
/// </summary>
public static class LmStudioAddress
{
    /// <summary>Checks an address against the rule.</summary>
    /// <param name="url">The configured <c>LmStudio:BaseUrl</c>.</param>
    /// <param name="allowRemote">The user's consent to another computer, <c>LmStudio:AllowRemote</c>.</param>
    /// <returns>
    /// <see langword="null"/> when the address may be used: this machine (<c>localhost</c>, <c>127.x.x.x</c>,
    /// <c>::1</c>) over http or https, or another computer over https with consent. Otherwise a sentence for the user.
    /// </returns>
    public static string? Problem(string? url, bool allowRemote)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "The LM Studio URL must look like http://localhost:1234/v1.";
        if (uri.IsLoopback)
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps)
            return "An LM Studio address on another computer must use https, or screenshots would cross the network unencrypted.";
        if (!allowRemote)
            return "The LM Studio address is on another computer. Turn on \"Allow another computer\" to send screenshots there.";
        return null;
    }
}
