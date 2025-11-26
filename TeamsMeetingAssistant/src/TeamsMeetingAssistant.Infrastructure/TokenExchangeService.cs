using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Infrastructure;

/// <summary>
/// Handles token exchange for Teams SSO using On-Behalf-Of (OBO) flow
/// </summary>
public class TokenExchangeService : ITokenExchangeService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TokenExchangeService> _logger;
    private readonly IConfidentialClientApplication _confidentialClient;
    private readonly bool _enableSso;

    public TokenExchangeService(
        IConfiguration configuration,
        ILogger<TokenExchangeService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        
        // Parse boolean value from configuration
        var ssoConfigValue = _configuration["AzureAd:EnableSso"];
        _enableSso = string.IsNullOrEmpty(ssoConfigValue) || bool.Parse(ssoConfigValue);

        var tenantId = _configuration["AzureAd:TenantId"];
        var clientId = _configuration["AzureAd:ClientId"];
        var clientSecret = _configuration["AzureAd:ClientSecret"];

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException("Azure AD configuration is incomplete. Check TenantId, ClientId, and ClientSecret.");
        }

        // Build confidential client for both OBO and app-only flows
        _confidentialClient = ConfidentialClientApplicationBuilder
            .Create(clientId)
            .WithClientSecret(clientSecret)
            .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenantId}"))
            .WithLegacyCacheCompatibility(false)
            .Build();

        _logger.LogInformation("TokenExchangeService initialized. SSO Enabled: {SsoEnabled}", _enableSso);
    }

    public async Task<string> ExchangeTokenAsync(string idToken, CancellationToken cancellationToken = default)
    {
        if (!_enableSso)
        {
            _logger.LogWarning("SSO is disabled. Falling back to app-only token.");
            return await GetAppOnlyTokenAsync(cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new ArgumentException("ID token cannot be null or empty", nameof(idToken));
        }

        try
        {
            _logger.LogInformation("Exchanging ID token for Graph API access token using OBO flow");

            // Create user assertion from the ID token
            var userAssertion = new UserAssertion(idToken);

            // Define the delegated scopes we need
            var scopes = new[]
            {
                "https://graph.microsoft.com/OnlineMeetings.Read",
                "https://graph.microsoft.com/OnlineMeetingTranscript.Read.All",
                "https://graph.microsoft.com/User.Read"
            };

            // Acquire token on behalf of the user
            var result = await _confidentialClient
                .AcquireTokenOnBehalfOf(scopes, userAssertion)
                .ExecuteAsync(cancellationToken);

            _logger.LogInformation("Successfully acquired access token via OBO flow. Expires: {ExpiresOn}", result.ExpiresOn);

            return result.AccessToken;
        }
        catch (MsalServiceException ex)
        {
            _logger.LogError(ex, "MSAL service error during OBO flow. Error: {Error}, Status: {StatusCode}", 
                ex.ErrorCode, ex.StatusCode);
            
            if (ex.ErrorCode == "invalid_grant" || ex.ErrorCode == "interaction_required")
            {
                throw new UnauthorizedAccessException(
                    "User consent is required. The user needs to consent to the required permissions.", ex);
            }

            throw;
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "MSAL error during token exchange: {ErrorCode}", ex.ErrorCode);
            throw new InvalidOperationException($"Token exchange failed: {ex.Message}", ex);
        }
    }

    public Task<bool> ValidateTokenAsync(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return Task.FromResult(false);
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(idToken);

            // Basic validation
            var tenantId = _configuration["AzureAd:TenantId"];
            var clientId = _configuration["AzureAd:ClientId"];

            _logger.LogInformation("Validating token. Issuer: {Issuer}, Audiences: {Audiences}", 
                jwtToken.Issuer, string.Join(", ", jwtToken.Audiences));

            // Check issuer - Teams tokens use sts.windows.net, not login.microsoftonline.com
            // Both v1.0 and v2.0 formats are valid
            var validIssuers = new[]
            {
                $"https://sts.windows.net/{tenantId}/",  // v1.0 endpoint (what Teams uses)
                $"https://login.microsoftonline.com/{tenantId}/v2.0"  // v2.0 endpoint
            };

            if (!validIssuers.Any(issuer => jwtToken.Issuer.Equals(issuer, StringComparison.OrdinalIgnoreCase)))
            {
                _logger.LogWarning("Token issuer mismatch. Expected one of: {Expected}, Got: {Actual}", 
                    string.Join(" or ", validIssuers), jwtToken.Issuer);
                return Task.FromResult(false);
            }

            // Check audience - Teams SSO tokens use format: api://hostname/clientId
            // The audience should match our configured App ID URI
            var audiences = jwtToken.Audiences.ToList();
            var validAudience = audiences.Any(aud => 
                aud.Contains(clientId, StringComparison.OrdinalIgnoreCase));

            if (!validAudience)
            {
                _logger.LogWarning("Token audience mismatch. ClientId: {ClientId}, Got: {Audiences}", 
                    clientId, string.Join(", ", audiences));
                return Task.FromResult(false);
            }

            // Check expiration
            if (jwtToken.ValidTo < DateTime.UtcNow)
            {
                _logger.LogWarning("Token has expired. Valid until: {ValidTo}, Current time: {Now}", 
                    jwtToken.ValidTo, DateTime.UtcNow);
                return Task.FromResult(false);
            }

            // Check not before (nbf)
            if (jwtToken.ValidFrom > DateTime.UtcNow)
            {
                _logger.LogWarning("Token not yet valid. Valid from: {ValidFrom}, Current time: {Now}", 
                    jwtToken.ValidFrom, DateTime.UtcNow);
                return Task.FromResult(false);
            }

            _logger.LogInformation("Token validation successful. User: {UserId}, Tenant: {TenantId}", 
                jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value,
                jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value);
            
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating token");
            return Task.FromResult(false);
        }
    }

    public Task<UserInfo> GetUserInfoFromTokenAsync(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            throw new ArgumentException("ID token cannot be null or empty", nameof(idToken));
        }

        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(idToken);

            var userId = jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value
                         ?? jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;

            var tenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;

            var upn = jwtToken.Claims.FirstOrDefault(c => c.Type == "upn")?.Value
                      ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value;

            var email = jwtToken.Claims.FirstOrDefault(c => c.Type == "email")?.Value
                        ?? jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;

            var name = jwtToken.Claims.FirstOrDefault(c => c.Type == "name")?.Value
                       ?? jwtToken.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            if (string.IsNullOrEmpty(userId))
            {
                throw new InvalidOperationException("Could not extract user ID from token");
            }

            _logger.LogInformation("Extracted user info - UserId: {UserId}, UPN: {Upn}", userId, upn);

            return Task.FromResult(new UserInfo(
                userId ?? string.Empty,
                tenantId ?? string.Empty,
                upn,
                email,
                name
            ));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error extracting user info from token");
            throw new InvalidOperationException("Failed to extract user information from token", ex);
        }
    }

    public async Task<string> GetAppOnlyTokenAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Acquiring app-only access token (Client Credentials flow)");

            var scopes = new[] { "https://graph.microsoft.com/.default" };

            var result = await _confidentialClient
                .AcquireTokenForClient(scopes)
                .ExecuteAsync(cancellationToken);

            _logger.LogInformation("Successfully acquired app-only access token. Expires: {ExpiresOn}", result.ExpiresOn);

            return result.AccessToken;
        }
        catch (MsalException ex)
        {
            _logger.LogError(ex, "Failed to acquire app-only token: {ErrorCode}", ex.ErrorCode);
            throw new InvalidOperationException($"App-only token acquisition failed: {ex.Message}", ex);
        }
    }
}
