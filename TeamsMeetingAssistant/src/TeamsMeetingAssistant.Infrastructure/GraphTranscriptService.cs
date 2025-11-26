using Microsoft.Graph;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.Retry;
using System.IdentityModel.Tokens.Jwt;

namespace TeamsMeetingAssistant.Infrastructure;

public class GraphTranscriptService : ITranscriptService
{
    private readonly GraphServiceClient _graphClient;
    private readonly VttTranscriptParser _vttParser;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    private readonly ILogger<GraphTranscriptService> _logger;
    private readonly IConfiguration _configuration;

    // Track delta links per meeting for incremental queries
    private static readonly Dictionary<string, string> _deltaLinks = new();
    
    // Store the current access token (from OBO flow - delegated permissions)
    private string? _delegatedAccessToken;
    
    // Store the user ID extracted from the delegated token
    private string? _currentUserId;
    
    // Store the user's display name extracted from the delegated token (for role assignment)
    private string? _currentUserDisplayName;
    
    // Cache for application token (for delta queries)
    private string? _applicationAccessToken;
    private DateTimeOffset _applicationTokenExpiry = DateTimeOffset.MinValue;

    public GraphTranscriptService(
        GraphServiceClient graphClient,
        ILogger<GraphTranscriptService> logger,
        IConfiguration configuration)
    {
        _logger = logger;
        _vttParser = new VttTranscriptParser();
        _graphClient = graphClient;
        _configuration = configuration;

        _retryPolicy = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: (retryAttempt, context) =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (outcome, timespan, retryAttempt, context) =>
                {
                    _logger.LogWarning(
                        "Graph API request failed. Retry {RetryAttempt} after {Delay}ms. Error: {Error}",
                        retryAttempt, timespan.TotalMilliseconds, outcome.Exception?.Message);
                });
    }

    /// <summary>
    /// Set the delegated access token (from SSO/OBO flow) to use for user-specific operations
    /// Also extracts the user ID and display name from the token claims for role assignment
    /// </summary>
    public void SetAccessToken(string accessToken)
    {
        _delegatedAccessToken = accessToken;
        
        // Extract user ID and display name from token claims
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwtToken = handler.ReadJwtToken(accessToken);

            // Try to get the OID (Object ID) claim - this is the user's unique ID in Azure AD
            _currentUserId = jwtToken.Claims.FirstOrDefault(c => c.Type == "oid")?.Value;
            
            // Extract display name from token - try multiple claim types
            // "name" claim typically contains the full display name (e.g., "Efrain Goyzueta")
            _currentUserDisplayName = jwtToken.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            
            // Fallback to "preferred_username" or "upn" if "name" is not available
            if (string.IsNullOrEmpty(_currentUserDisplayName))
            {
                _currentUserDisplayName = jwtToken.Claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value 
                    ?? jwtToken.Claims.FirstOrDefault(c => c.Type == "upn")?.Value;
            }
            
            if (!string.IsNullOrEmpty(_currentUserId))
            {
                _logger.LogInformation("Delegated access token set. User ID: {UserId}, Display Name: {DisplayName}", 
                    _currentUserId, _currentUserDisplayName ?? "(not available)");
            }
            else
            {
                _logger.LogWarning("Delegated access token set but could not extract user ID from claims");
            }
            
            if (string.IsNullOrEmpty(_currentUserDisplayName))
            {
                _logger.LogWarning("Could not extract display name from token claims for role assignment");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract user information from delegated access token");
            _currentUserId = null;
            _currentUserDisplayName = null;
        }
    }
    
    /// <summary>
    /// Get the user ID for API calls - from token claims or fallback to configuration
    /// </summary>
    private string GetUserId()
    {
        // Prefer user ID from token claims
        if (!string.IsNullOrEmpty(_currentUserId))
        {
            return _currentUserId;
        }
        
        // Fallback to configured default (for debugging when SSO is disabled)
        var fallbackUserId = _configuration["AzureAd:DefaultUserId"] ?? "aacdff60-4840-45f4-814e-4243bdd0636e";
        
        _logger.LogWarning("Using fallback user ID: {UserId} (token user ID not available)", fallbackUserId);
        
        return fallbackUserId;
    }
    
    /// <summary>
    /// Get an application-level access token for operations that don't support delegated context
    /// (like delta queries)
    /// </summary>
    private async Task<string?> GetApplicationTokenAsync(CancellationToken cancellationToken)
    {
        // Return cached token if still valid
        if (!string.IsNullOrEmpty(_applicationAccessToken) && DateTimeOffset.UtcNow < _applicationTokenExpiry.AddMinutes(-5))
        {
            _logger.LogDebug("Using cached application access token");
            return _applicationAccessToken;
        }

        try
        {
            _logger.LogDebug("Acquiring new application access token for delta queries");
            
            var scope = "https://graph.microsoft.com/.default";
            var tenantId = _configuration["AzureAd:TenantId"];
            var clientId = _configuration["AzureAd:ClientId"];
            var clientSecret = _configuration["AzureAd:ClientSecret"];

            var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

            using var httpClient = new HttpClient();

            var tokenRequest = new Dictionary<string, string>
            {
                {"client_id", clientId!},
                {"client_secret", clientSecret!},
                {"scope", scope},
                {"grant_type", "client_credentials"}
            };

            var tokenRequestContent = new FormUrlEncodedContent(tokenRequest);
            var tokenResponse = await httpClient.PostAsync(tokenEndpoint, tokenRequestContent, cancellationToken);

            if (tokenResponse.IsSuccessStatusCode)
            {
                var tokenResponseContent = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
                using var jsonDoc = System.Text.Json.JsonDocument.Parse(tokenResponseContent);

                if (jsonDoc.RootElement.TryGetProperty("access_token", out var accessTokenElement))
                {
                    _applicationAccessToken = accessTokenElement.GetString();

                    if (jsonDoc.RootElement.TryGetProperty("expires_in", out var expiresInElement))
                    {
                        var expiresIn = expiresInElement.GetInt32();
                        _applicationTokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
                        _logger.LogInformation("Successfully obtained application access token, expires in {ExpiresIn} seconds", expiresIn);
                    }

                    return _applicationAccessToken;
                }
            }
            else
            {
                var errorContent = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to obtain application access token: {StatusCode} - {Error}", 
                    tokenResponse.StatusCode, errorContent);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obtaining application access token");
            return null;
        }
    }

    public async Task<IEnumerable<TranscriptSegment>> GetNewTranscriptSegmentsAsync(
        string meetingId,
        DateTimeOffset since,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogDebug("Fetching transcripts for meeting {MeetingId} since {Since}", meetingId, since);

            var allSegments = new List<TranscriptSegment>();

            // Check if we have a delta link for this meeting
            if (_deltaLinks.TryGetValue(meetingId, out var deltaLink))
            {
                _logger.LogInformation("Using delta link for meeting {MeetingId} to fetch only changes", meetingId);

                // Use delta query to get only changed transcripts
                var deltaTranscripts = await GetTranscriptsDeltaAsync(meetingId, deltaLink, cancellationToken);

                if (deltaTranscripts != null)
                {
                    await ProcessTranscriptsAsync(deltaTranscripts.Transcripts, meetingId, since, allSegments, cancellationToken);

                    // Update delta link for next call
                    if (!string.IsNullOrEmpty(deltaTranscripts.DeltaLink))
                    {
                        _deltaLinks[meetingId] = deltaTranscripts.DeltaLink;
                        _logger.LogDebug("Updated delta link for meeting {MeetingId}", meetingId);
                    }
                }
            }
            else
            {
                _logger.LogInformation("No delta link found for meeting {MeetingId}, performing initial query", meetingId);

                // First call - get all transcripts and establish delta link
                var initialTranscripts = await GetTranscriptsInitialAsync(meetingId, cancellationToken);

                if (initialTranscripts != null)
                {
                    await ProcessTranscriptsAsync(initialTranscripts.Transcripts, meetingId, since, allSegments, cancellationToken);

                    // Store delta link for subsequent calls
                    if (!string.IsNullOrEmpty(initialTranscripts.DeltaLink))
                    {
                        _deltaLinks[meetingId] = initialTranscripts.DeltaLink;
                        _logger.LogInformation("Stored delta link for meeting {MeetingId}", meetingId);
                    }
                }
            }

            _logger.LogInformation("Found {Count} new transcript segments for meeting {MeetingId}",
                allSegments.Count, meetingId);

            return allSegments.OrderBy(s => s.Timestamp);
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex)
        {
            _logger.LogError("Graph API error fetching transcripts for meeting {MeetingId}: {Code} - {Message}",
                meetingId, ex.Error?.Code, ex.Error?.Message);

            return new List<TranscriptSegment>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching transcripts for meeting {MeetingId}", meetingId);
            return new List<TranscriptSegment>();
        }
    }

    private async Task<TranscriptDeltaResult?> GetTranscriptsInitialAsync(
        string meetingId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogDebug("Fetching initial transcripts with delta for meeting {MeetingId}", meetingId);

            // Get user ID from token claims or configuration
            var userId = GetUserId();

            // Get application token for delta query (getAllTranscripts requires application permissions)
            var accessToken = await GetApplicationTokenAsync(cancellationToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to get application access token for delta query");
                _logger.LogInformation("Falling back to regular (non-delta) transcript fetch");
                return await GetTranscriptsFallbackAsync(meetingId, cancellationToken);
            }

            // Build the delta query URL - getAllTranscripts doesn't support OData filters
            var deltaUrl = $"https://graph.microsoft.com/v1.0/users/{userId}/onlineMeetings/getAllTranscripts(meetingOrganizerUserId='{userId}')";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(deltaUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Delta query failed: {StatusCode} - {Error}", response.StatusCode, error);
                return await GetTranscriptsFallbackAsync(meetingId, cancellationToken);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var jsonDoc = System.Text.Json.JsonDocument.Parse(content);

            var matchingTranscripts = new List<(Microsoft.Graph.Models.CallTranscript Transcript, DateTimeOffset CreatedTime)>();

            // Process transcripts and filter by meetingId
            if (jsonDoc.RootElement.TryGetProperty("value", out var valueElement))
            {
                foreach (var transcriptElement in valueElement.EnumerateArray())
                {
                    // Check if this transcript belongs to our meeting
                    if (transcriptElement.TryGetProperty("meetingId", out var transcriptMeetingIdProp))
                    {
                        var transcriptMeetingId = transcriptMeetingIdProp.GetString();
                        if (transcriptMeetingId == meetingId)
                        {
                            var transcript = System.Text.Json.JsonSerializer.Deserialize<Microsoft.Graph.Models.CallTranscript>(
                                transcriptElement.GetRawText(),
                                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                            if (transcript != null && transcript.CreatedDateTime.HasValue)
                            {
                                matchingTranscripts.Add((transcript, transcript.CreatedDateTime.Value));
                                _logger.LogDebug("Found transcript {TranscriptId} created at {CreatedTime}",
                                    transcript.Id, transcript.CreatedDateTime.Value);
                            }
                        }
                    }
                }
            }

            // Get only the latest transcript by CreatedDateTime
            var latestTranscript = matchingTranscripts
                .OrderByDescending(t => t.CreatedTime)
                .Select(t => t.Transcript)
                .FirstOrDefault();

            var resultTranscripts = new List<Microsoft.Graph.Models.CallTranscript>();
            if (latestTranscript != null)
            {
                resultTranscripts.Add(latestTranscript);
                _logger.LogInformation("Selected latest transcript {TranscriptId} from {TotalCount} transcripts for meeting {MeetingId}",
                    latestTranscript.Id, matchingTranscripts.Count, meetingId);
            }
            else
            {
                _logger.LogInformation("No transcripts found for meeting {MeetingId}", meetingId);
            }

            // Get delta link from initial response
            string? deltaLink = null;
            if (jsonDoc.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement))
            {
                deltaLink = deltaLinkElement.GetString();
                _logger.LogInformation("Got delta link from initial response for meeting {MeetingId}", meetingId);
            }
            else if (jsonDoc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement))
            {
                _logger.LogWarning("Unexpected pagination in getAllTranscripts initial call");
                deltaLink = nextLinkElement.GetString();
            }

            return new TranscriptDeltaResult
            {
                Transcripts = resultTranscripts,
                DeltaLink = deltaLink
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching initial transcripts with delta");
            return await GetTranscriptsFallbackAsync(meetingId, cancellationToken);
        }
    }

    private async Task<TranscriptDeltaResult?> GetTranscriptsDeltaAsync(
        string meetingId,
        string deltaLink,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogDebug("Fetching delta transcripts for meeting {MeetingId} using delta link", meetingId);

            // Get application token for delta query
            var accessToken = await GetApplicationTokenAsync(cancellationToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to get application access token for delta query");
                _deltaLinks.Remove(meetingId);
                return null;
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var matchingTranscripts = new List<(Microsoft.Graph.Models.CallTranscript Transcript, DateTimeOffset CreatedTime)>();
            var currentUrl = deltaLink;
            string? newDeltaLink = null;

            // Follow pagination links until we get the new delta link
            while (!string.IsNullOrEmpty(currentUrl))
            {
                _logger.LogDebug("Executing delta query with URL: {Url}", currentUrl);

                var response = await httpClient.GetAsync(currentUrl, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning("Delta query with link failed: {StatusCode} - {Error}", response.StatusCode, error);

                    // Clear delta link and force re-initialization
                    _deltaLinks.Remove(meetingId);
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var jsonDoc = System.Text.Json.JsonDocument.Parse(content);

                // Process transcripts from this page
                if (jsonDoc.RootElement.TryGetProperty("value", out var valueElement))
                {
                    foreach (var transcriptElement in valueElement.EnumerateArray())
                    {
                        // Check if this is a deleted item
                        var isDeleted = transcriptElement.TryGetProperty("@removed", out var removedElement);

                        if (!isDeleted)
                        {
                            // Check if this transcript belongs to our meeting
                            if (transcriptElement.TryGetProperty("meetingId", out var transcriptMeetingIdProp))
                            {
                                var transcriptMeetingId = transcriptMeetingIdProp.GetString();
                                if (transcriptMeetingId == meetingId)
                                {
                                    var transcript = System.Text.Json.JsonSerializer.Deserialize<Microsoft.Graph.Models.CallTranscript>(
                                        transcriptElement.GetRawText(),
                                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                                    if (transcript != null && transcript.CreatedDateTime.HasValue)
                                    {
                                        matchingTranscripts.Add((transcript, transcript.CreatedDateTime.Value));
                                        _logger.LogDebug("Found changed transcript {TranscriptId} created at {CreatedTime}",
                                            transcript.Id, transcript.CreatedDateTime.Value);
                                    }
                                }
                            }
                        }
                        else
                        {
                            _logger.LogDebug("Skipping deleted transcript item");
                        }
                    }
                }

                // Check for delta link (final page)
                if (jsonDoc.RootElement.TryGetProperty("@odata.deltaLink", out var deltaLinkElement))
                {
                    newDeltaLink = deltaLinkElement.GetString();
                    _logger.LogInformation("Delta query complete: {Count} changed transcripts for meeting {MeetingId}, new delta link obtained",
                        matchingTranscripts.Count, meetingId);
                    break;
                }
                // Check for next link (more pages to fetch)
                else if (jsonDoc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement))
                {
                    currentUrl = nextLinkElement.GetString();
                    _logger.LogDebug("Following delta pagination, next link: {NextLink}", currentUrl);
                }
                else
                {
                    _logger.LogWarning("No new delta link in response, keeping previous delta link");
                    newDeltaLink = deltaLink;
                    break;
                }
            }

            // Get only the latest transcript by CreatedDateTime
            var latestTranscript = matchingTranscripts
                .OrderByDescending(t => t.CreatedTime)
                .Select(t => t.Transcript)
                .FirstOrDefault();

            var resultTranscripts = new List<Microsoft.Graph.Models.CallTranscript>();
            if (latestTranscript != null)
            {
                resultTranscripts.Add(latestTranscript);
                _logger.LogInformation("Selected latest transcript {TranscriptId} from {TotalCount} changed transcripts for meeting {MeetingId}",
                    latestTranscript.Id, matchingTranscripts.Count, meetingId);
            }
            else
            {
                _logger.LogDebug("No changed transcripts found for meeting {MeetingId}", meetingId);
            }

            return new TranscriptDeltaResult
            {
                Transcripts = resultTranscripts,
                DeltaLink = newDeltaLink ?? deltaLink
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching delta transcripts");
            _deltaLinks.Remove(meetingId);
            return null;
        }
    }

    private async Task<TranscriptDeltaResult?> GetTranscriptsFallbackAsync(
        string meetingId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogInformation("Using fallback method to fetch transcripts for meeting {MeetingId}", meetingId);

            var userId = GetUserId();

            // Use regular Graph SDK call with delegated token if available, otherwise application token
            var transcripts = await _graphClient.Users[userId]
                .OnlineMeetings[meetingId]
                .Transcripts
                .GetAsync(cancellationToken: cancellationToken);

            var transcriptList = new List<Microsoft.Graph.Models.CallTranscript>();

            if (transcripts?.Value != null)
            {
                transcriptList.AddRange(transcripts.Value);
                _logger.LogDebug("Fallback fetch returned {Count} transcripts", transcripts.Value.Count);
            }

            return new TranscriptDeltaResult
            {
                Transcripts = transcriptList,
                DeltaLink = null // No delta link with fallback method
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in fallback transcript fetch");
            return null;
        }
    }

    private async Task ProcessTranscriptsAsync(
        List<Microsoft.Graph.Models.CallTranscript> transcripts,
        string meetingId,
        DateTimeOffset since,
        List<TranscriptSegment> allSegments,
        CancellationToken cancellationToken
    )
    {
        if (!transcripts.Any())
        {
            _logger.LogDebug("No transcripts to process for meeting {MeetingId}", meetingId);
            return;
        }

        _logger.LogDebug("Processing {Count} transcript files for meeting {MeetingId}", transcripts.Count, meetingId);

        var userId = GetUserId();
        
        // Get the interviewer display name from OBO token for role assignment
        var interviewerDisplayName = _currentUserDisplayName;
        
        if (!string.IsNullOrEmpty(interviewerDisplayName))
        {
            _logger.LogInformation("Using OBO token display name for role assignment: '{DisplayName}'", interviewerDisplayName);
        }
        else
        {
            _logger.LogWarning("No OBO token display name available - role assignment will use first speaker fallback");
        }

        foreach (var transcript in transcripts)
        {
            try
            {
                _logger.LogDebug("Processing transcript {TranscriptId}", transcript.Id);

                var transcriptBaseTime = transcript.CreatedDateTime ?? DateTimeOffset.UtcNow;

                _logger.LogDebug("Using transcript base time: {BaseTime} for transcript {TranscriptId}",
                    transcriptBaseTime, transcript.Id);

                // Use the transcriptContentUrl from the transcript response if available
                if (!string.IsNullOrEmpty(transcript.TranscriptContentUrl))
                {
                    // Download transcript content using the URL (prefer delegated token if available)
                    var transcriptContent = await DownloadTranscriptContentAsync(transcript.TranscriptContentUrl, cancellationToken);

                    if (string.IsNullOrEmpty(transcriptContent))
                    {
                        _logger.LogWarning("Empty content for transcript {TranscriptId}", transcript.Id);
                        continue;
                    }

                    _logger.LogDebug("Processing transcript content of length {Length} for transcript {TranscriptId}",
                        transcriptContent.Length, transcript.Id);

                    var segments = _vttParser.Parse(transcriptContent, transcriptBaseTime);
                    
                    // Assign roles based on interviewer display name from OBO token
                    var segmentsWithRoles = AssignSpeakerRoles(segments, interviewerDisplayName, meetingId);
                    
                    var filteredSegments = segmentsWithRoles.Where(s => s.Timestamp > since).ToList();

                    if (filteredSegments.Any())
                    {
                        allSegments.AddRange(filteredSegments);
                        _logger.LogDebug("Added {Count} segments (filtered from {TotalCount}) from transcript {TranscriptId}",
                            filteredSegments.Count, segments.Count, transcript.Id);
                    }
                }
                else
                {
                    // Fallback to direct content access through Graph SDK
                    var transcriptContentStream = await _graphClient.Users[userId].OnlineMeetings[meetingId]
                        .Transcripts[transcript.Id]
                        .Content
                        .GetAsync(cancellationToken: cancellationToken);

                    if (transcriptContentStream == null)
                    {
                        _logger.LogWarning("No content stream for transcript {TranscriptId}", transcript.Id);
                        continue;
                    }

                    using var reader = new System.IO.StreamReader(transcriptContentStream);
                    var vttContent = await reader.ReadToEndAsync(cancellationToken);

                    if (string.IsNullOrEmpty(vttContent))
                    {
                        _logger.LogWarning("Empty content for transcript {TranscriptId}", transcript.Id);
                        continue;
                    }

                    var segments = _vttParser.Parse(vttContent, transcriptBaseTime);
                    
                    // Assign roles based on interviewer display name from OBO token
                    var segmentsWithRoles = AssignSpeakerRoles(segments, interviewerDisplayName, meetingId);
                    
                    var filteredSegments = segmentsWithRoles.Where(s => s.Timestamp > since).ToList();

                    if (filteredSegments.Any())
                    {
                        allSegments.AddRange(filteredSegments);
                        _logger.LogDebug("Added {Count} segments from transcript {TranscriptId}",
                            filteredSegments.Count, transcript.Id);
                    }
                }
            }
            catch (Exception transcriptEx)
            {
                _logger.LogError(transcriptEx, "Error processing transcript {TranscriptId}", transcript.Id);
            }
        }
    }

    /// <summary>
    /// Assigns speaker roles (Interviewer/Interviewee) based on display name matching
    /// POC implementation: If speaker's display name matches the OBO token display name -> Interviewer, else -> Candidate
    /// </summary>
    private List<TranscriptSegment> AssignSpeakerRoles(
        List<TranscriptSegment> segments, 
        string? interviewerDisplayName,
        string meetingId)
    {
        if (!segments.Any())
        {
            return segments;
        }

        // If no interviewer display name from OBO token, use first speaker as fallback
        var effectiveInterviewerName = interviewerDisplayName;
        
        if (string.IsNullOrEmpty(effectiveInterviewerName))
        {
            // Fallback: Use first speaker's name as interviewer
            effectiveInterviewerName = segments.First().SpeakerName;
            _logger.LogWarning("No OBO token display name available for meeting {MeetingId}. Using first speaker '{SpeakerName}' as interviewer (fallback)", 
                meetingId, effectiveInterviewerName);
        }

        var segmentsWithRoles = new List<TranscriptSegment>();
        
        foreach (var segment in segments)
        {
            // Compare speaker name/ID against interviewer display name (case-insensitive)
            // Note: Both SpeakerName and SpeakerId contain the display name from VTT
            bool isInterviewer = string.Equals(segment.SpeakerName, effectiveInterviewerName, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(segment.SpeakerId, effectiveInterviewerName, StringComparison.OrdinalIgnoreCase);
            
            var role = isInterviewer ? SpeakerRole.Interviewer : SpeakerRole.Interviewee;
            
            // Create new segment with assigned role
            var segmentWithRole = segment with { Role = role };
            segmentsWithRoles.Add(segmentWithRole);
            
            _logger.LogTrace("Assigned role {Role} to segment from speaker '{SpeakerName}' (Interviewer: '{Interviewer}')",
                role, segment.SpeakerName, effectiveInterviewerName);
        }

        _logger.LogDebug("Role assignment complete for {Count} segments. Interviewer: '{Interviewer}'",
            segmentsWithRoles.Count, effectiveInterviewerName);

        return segmentsWithRoles;
    }

    public async Task<MeetingInfo> GetMeetingInfoAsync(string joinWebUrl, CancellationToken cancellationToken)
    {
        try
        {
            var userId = GetUserId();
            
            _logger.LogDebug("Searching for meeting by Join URL: {JoinUrl} for user: {UserId}", joinWebUrl, userId);

            // Use delegated token via Graph SDK (better security posture)
            var meetings = await _graphClient.Users[userId].OnlineMeetings
                .GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Filter = $"joinWebUrl eq '{joinWebUrl}'";
                }, cancellationToken);

            var meeting = meetings?.Value?.FirstOrDefault();

            if (meeting == null)
            {
                _logger.LogWarning("No meeting found with join URL: {JoinUrl} for user: {UserId}", joinWebUrl, userId);
                return null;
            }

            _logger.LogInformation("✅ Successfully found meeting by Join URL for user: {UserId}", userId);

            return new MeetingInfo
            (
                meeting.Id ?? "",
                meeting.Participants?.Organizer?.Identity?.User?.DisplayName ?? "Unknown Organizer",
                meeting.StartDateTime ?? DateTimeOffset.UtcNow,
                meeting.EndDateTime,
                meeting.IsBroadcast ?? false
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find meeting by Join URL");
            throw;
        }
    }

    public void ClearDeltaLink(string meetingId)
    {
        if (_deltaLinks.Remove(meetingId))
        {
            _logger.LogInformation("Cleared delta link for meeting {MeetingId}", meetingId);
        }
    }

    public async Task<Subscription> SubscribeToTranscriptChangesAsync(
        string meetingId,
        string webhookUrl,
        CancellationToken cancellationToken
    )
    {
        try
        {
            _logger.LogDebug("Creating subscription for meeting {MeetingId}", meetingId);

            var subscription = new Microsoft.Graph.Models.Subscription
            {
                ChangeType = "created,updated",
                NotificationUrl = webhookUrl,
                Resource = $"/communications/onlineMeetings/{meetingId}/transcripts",
                ExpirationDateTime = DateTimeOffset.UtcNow.AddHours(1),
                ClientState = Guid.NewGuid().ToString()
            };

            var createdSubscription = await _graphClient.Subscriptions
                .PostAsync(subscription, cancellationToken: cancellationToken);

            _logger.LogInformation("Created subscription {SubscriptionId} for meeting {MeetingId}",
                createdSubscription?.Id, meetingId);

            return new Subscription(
                createdSubscription?.Id ?? "",
                createdSubscription?.ExpirationDateTime,
                createdSubscription?.Resource ?? "",
                createdSubscription?.ChangeType ?? ""
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating subscription for meeting {MeetingId}", meetingId);
            throw;
        }
    }

    public async Task RenewSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Renewing subscription {SubscriptionId}", subscriptionId);

            var subscription = new Microsoft.Graph.Models.Subscription
            {
                ExpirationDateTime = DateTimeOffset.UtcNow.AddHours(1)
            };

            await _graphClient.Subscriptions[subscriptionId]
                .PatchAsync(subscription, cancellationToken: cancellationToken);

            _logger.LogInformation("Renewed subscription {SubscriptionId}", subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error renewing subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Deleting subscription {SubscriptionId}", subscriptionId);

            await _graphClient.Subscriptions[subscriptionId]
                .DeleteAsync(cancellationToken: cancellationToken);

            _logger.LogInformation("Deleted subscription {SubscriptionId}", subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting subscription {SubscriptionId}", subscriptionId);
            throw;
        }
    }

    private async Task<string> DownloadTranscriptContentAsync(string transcriptContentUrl, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Downloading transcript content from URL: {Url}", transcriptContentUrl);

            // Prefer delegated token if available (better security), fallback to application token
            var accessToken = !string.IsNullOrEmpty(_delegatedAccessToken) 
                ? _delegatedAccessToken 
                : await GetApplicationTokenAsync(cancellationToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to obtain access token for transcript download");
                return string.Empty;
            }

            var tokenType = !string.IsNullOrEmpty(_delegatedAccessToken) ? "delegated" : "application";
            _logger.LogDebug("Using {TokenType} token for transcript content download", tokenType);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "TeamsMeetingAssistant/1.0");

            var response = await httpClient.GetAsync(transcriptContentUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Successfully downloaded transcript content ({TokenType} token), length: {Length}", 
                    tokenType, content.Length);
                return content;
            }
            else
            {
                _logger.LogWarning("Failed to download transcript: {StatusCode} - {ReasonPhrase}",
                    response.StatusCode, response.ReasonPhrase);
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading transcript content from URL: {Url}", transcriptContentUrl);
            return string.Empty;
        }
    }

    private class TranscriptDeltaResult
    {
        public List<Microsoft.Graph.Models.CallTranscript> Transcripts { get; set; } = new();
        public string? DeltaLink { get; set; }
    }
}
