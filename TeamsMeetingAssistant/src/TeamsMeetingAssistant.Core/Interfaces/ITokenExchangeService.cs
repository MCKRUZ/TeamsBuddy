namespace TeamsMeetingAssistant.Core.Interfaces;

/// <summary>
/// Service for exchanging Teams SSO tokens for Graph API access tokens
/// using On-Behalf-Of (OBO) flow
/// </summary>
public interface ITokenExchangeService
{
    /// <summary>
    /// Exchange a user's ID token for a Graph API access token with delegated permissions
    /// </summary>
    /// <param name="idToken">ID token from Teams SSO</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Access token for Microsoft Graph API</returns>
    Task<string> ExchangeTokenAsync(string idToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate an ID token from Teams
    /// </summary>
    /// <param name="idToken">ID token to validate</param>
    /// <returns>True if token is valid</returns>
    Task<bool> ValidateTokenAsync(string idToken);

    /// <summary>
    /// Extract user information from ID token
    /// </summary>
    /// <param name="idToken">ID token from Teams SSO</param>
    /// <returns>User information</returns>
    Task<UserInfo> GetUserInfoFromTokenAsync(string idToken);

    /// <summary>
    /// Get an access token using app-only permissions (fallback for debugging)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>App-only access token</returns>
    Task<string> GetAppOnlyTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// User information extracted from ID token
/// </summary>
public record UserInfo(
    string UserId,
    string TenantId,
    string? UserPrincipalName,
    string? Email,
    string? Name
);
