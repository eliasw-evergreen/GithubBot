namespace GithubBot.Services;

public class ConfigUiTokenService
{
    private string? _pendingToken;

    public string GenerateToken()
    {
        _pendingToken = Guid.NewGuid().ToString("N");
        return _pendingToken;
    }

    public bool ConsumeToken(string token)
    {
        if (_pendingToken == null || !_pendingToken.Equals(token, StringComparison.Ordinal))
            return false;
        _pendingToken = null;
        return true;
    }
}
