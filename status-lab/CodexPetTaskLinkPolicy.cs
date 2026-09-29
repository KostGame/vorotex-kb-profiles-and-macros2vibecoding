namespace Vorotex.K15.StatusLab;

internal static class CodexPetTaskLinkPolicy
{
    internal static bool TryLaunch(string? deepLink, bool canFocus, Action<string> launch)
    {
        if (!canFocus || launch is null || !IsExactCodexThreadLink(deepLink)) return false;
        launch(deepLink!);
        return true;
    }

    internal static bool IsExactCodexThreadLink(string? deepLink)
    {
        if (string.IsNullOrWhiteSpace(deepLink) || deepLink.Length > 1024 ||
            !Uri.TryCreate(deepLink, UriKind.Absolute, out var uri) ||
            uri.Scheme != "codex" || uri.Host != "threads" || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.Length <= 1)
            return false;

        try
        {
            var threadId = Uri.UnescapeDataString(uri.AbsolutePath[1..]);
            return !string.IsNullOrWhiteSpace(threadId) && threadId.Length <= 256 &&
                   !threadId.Any(char.IsControl) &&
                   string.Equals(deepLink, "codex://threads/" + Uri.EscapeDataString(threadId),
                       StringComparison.Ordinal);
        }
        catch (UriFormatException)
        {
            return false;
        }
    }
}
