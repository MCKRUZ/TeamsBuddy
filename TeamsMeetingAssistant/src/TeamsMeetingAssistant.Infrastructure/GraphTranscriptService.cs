using Microsoft.Graph;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.Retry;
using Microsoft.Kiota.Abstractions;

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

    public async Task<IEnumerable<TranscriptSegment>> GetNewTranscriptSegmentsAsync(
        string meetingId,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Fetching transcripts for meeting {MeetingId} since {Since}", meetingId, since);

            // Get user ID from configuration or use default
            var userId = _configuration["AzureAd:DefaultUserId"] ?? "aacdff60-4840-45f4-814e-4243bdd0636";

            var allSegments = new List<TranscriptSegment>();

            // Check if we have a delta link for this meeting
            if (_deltaLinks.TryGetValue(meetingId, out var deltaLink))
            {
                _logger.LogInformation("Using delta link for meeting {MeetingId} to fetch only changes", meetingId);

                // Use delta query to get only changed transcripts
                var deltaTranscripts = await GetTranscriptsDeltaAsync(userId, meetingId, deltaLink, cancellationToken);

                if (deltaTranscripts != null)
                {
                    await ProcessTranscriptsAsync(deltaTranscripts.Transcripts, meetingId, userId, since, allSegments, cancellationToken);

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
                var initialTranscripts = await GetTranscriptsInitialAsync(userId, meetingId, cancellationToken);

                if (initialTranscripts != null)
                {
                    await ProcessTranscriptsAsync(initialTranscripts.Transcripts, meetingId, userId, since, allSegments, cancellationToken);

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
        string userId,
        string meetingId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Fetching initial transcripts with delta for meeting {MeetingId}", meetingId);

            // Build the delta query URL - getAllTranscripts doesn't support OData filters
            var deltaUrl = $"https://graph.microsoft.com/v1.0/users/{userId}/onlineMeetings/getAllTranscripts(meetingOrganizerUserId='{userId}')";

            using var httpClient = new HttpClient();
            var accessToken = await GetAccessTokenWithScopesAsync(cancellationToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to get access token for delta query");
                return null;
            }

            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            var response = await httpClient.GetAsync(deltaUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Delta query failed: {StatusCode} - {Error}", response.StatusCode, error);
                return await GetTranscriptsFallbackAsync(userId, meetingId, cancellationToken);
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
            return await GetTranscriptsFallbackAsync(userId, meetingId, cancellationToken);
        }
    }

    private async Task<TranscriptDeltaResult?> GetTranscriptsDeltaAsync(
        string userId,
        string meetingId,
        string deltaLink,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Fetching delta transcripts for meeting {MeetingId} using delta link", meetingId);

            using var httpClient = new HttpClient();
            var accessToken = await GetAccessTokenWithScopesAsync(cancellationToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to get access token for delta query");
                return null;
            }

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
                    _logger.LogInformation("Delta query complete: {Count} changed transcripts for meeting {MeetingId}, new delta link obtained with updated skipToken",
                        matchingTranscripts.Count, meetingId);
                    break; // We've reached the end
                }
                // Check for next link (more pages to fetch)
                else if (jsonDoc.RootElement.TryGetProperty("@odata.nextLink", out var nextLinkElement))
                {
                    currentUrl = nextLinkElement.GetString();
                    _logger.LogDebug("Following delta pagination, next link: {NextLink}", currentUrl);
                }
                else
                {
                    // No more pages and no delta link - keep the old delta link
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
                DeltaLink = newDeltaLink ?? deltaLink // Keep old delta link if no new one
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching delta transcripts, will retry with initial query");

            // If delta query fails, clear the delta link and retry with initial query
            _deltaLinks.Remove(meetingId);
            return null;
        }
    }

    private async Task<TranscriptDeltaResult?> GetTranscriptsFallbackAsync(
        string userId,
        string meetingId,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Using fallback method to fetch transcripts for meeting {MeetingId}", meetingId);

            // Use regular Graph SDK call
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
        string userId,
        DateTimeOffset since,
        List<TranscriptSegment> allSegments,
        CancellationToken cancellationToken)
    {
        if (!transcripts.Any())
        {
            _logger.LogDebug("No transcripts to process for meeting {MeetingId}", meetingId);
            return;
        }

        _logger.LogDebug("Processing {Count} transcript files for meeting {MeetingId}", transcripts.Count, meetingId);

        foreach (var transcript in transcripts)
        {
            try
            {
                _logger.LogDebug("Processing transcript {TranscriptId}", transcript.Id);

                // Use transcript creation time as the base time for segment timestamps
                // This ensures segment timestamps are absolute, not relative to 'since'
                var transcriptBaseTime = transcript.CreatedDateTime ?? DateTimeOffset.UtcNow;

                _logger.LogDebug("Using transcript base time: {BaseTime} for transcript {TranscriptId}",
                    transcriptBaseTime, transcript.Id);

                // Use the transcriptContentUrl from the transcript response if available
                if (!string.IsNullOrEmpty(transcript.TranscriptContentUrl))
                {
                    // Download transcript content using the URL
                    var transcriptContent = await DownloadTranscriptContentAsync(transcript.TranscriptContentUrl, cancellationToken);

                    if (string.IsNullOrEmpty(transcriptContent))
                    {
                        _logger.LogWarning("Empty content for transcript {TranscriptId}", transcript.Id);
                        continue;
                    }

                    _logger.LogDebug("Processing transcript content of length {Length} for transcript {TranscriptId}",
                        transcriptContent.Length, transcript.Id);

                    // Parse with transcript creation time as base
                    var segments = _vttParser.Parse(transcriptContent, transcriptBaseTime);

                    // Filter segments that are newer than 'since' timestamp
                    var filteredSegments = segments
                        .Where(s => s.Timestamp > since)
                        .ToList();

                    if (filteredSegments.Any())
                    {
                        allSegments.AddRange(filteredSegments);

                        _logger.LogDebug("Added {Count} segments (filtered from {TotalCount}) from transcript {TranscriptId}",
                            filteredSegments.Count, segments.Count, transcript.Id);
                    }
                    else
                    {
                        _logger.LogDebug("No new segments after filtering by since={Since} for transcript {TranscriptId}",
                            since, transcript.Id);
                    }
                }
                else
                {
                    // Fallback to direct content access through user context
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

                    _logger.LogDebug("Processing VTT content of length {Length} for transcript {TranscriptId}",
                        vttContent.Length, transcript.Id);

                    // Parse with transcript creation time as base
                    var segments = _vttParser.Parse(vttContent, transcriptBaseTime);

                    // Filter segments that are newer than 'since' timestamp
                    var filteredSegments = segments
                        .Where(s => s.Timestamp > since)
                        .ToList();

                    if (filteredSegments.Any())
                    {
                        allSegments.AddRange(filteredSegments);

                        _logger.LogDebug("Added {Count} segments (filtered from {TotalCount}) from transcript {TranscriptId}",
                            filteredSegments.Count, segments.Count, transcript.Id);
                    }
                    else
                    {
                        _logger.LogDebug("No new segments after filtering by since={Since} for transcript {TranscriptId}",
                            since, transcript.Id);
                    }
                }
            }
            catch (Exception transcriptEx)
            {
                _logger.LogError(transcriptEx, "Error processing transcript {TranscriptId}", transcript.Id);
                // Continue with other transcripts
            }
        }
    }

    public async Task<MeetingInfo> GetMeetingInfoAsync(string joinWebUrl, string userId, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Searching for meeting by Join URL: {JoinUrl} for user: {UserId}", joinWebUrl, userId);

            // Use provided user ID or fall back to configured default
            var effectiveUserId = userId ?? _configuration["AzureAd:DefaultUserId"] ?? "aacdff60-4840-45f4-814e-4243bdd0636";

            // Use the correct user context endpoint as discovered in Graph Explorer
            var meetings = await _graphClient.Users[effectiveUserId].OnlineMeetings
                .GetAsync(requestConfiguration =>
                {
                    requestConfiguration.QueryParameters.Filter = $"joinWebUrl eq '{joinWebUrl}'";
                }, cancellationToken);

            var meeting = meetings?.Value?.FirstOrDefault();

            if (meeting == null)
            {
                _logger.LogWarning("No meeting found with join URL: {JoinUrl} for user: {UserId}", joinWebUrl, effectiveUserId);
                return null;
            }

            _logger.LogInformation("✅ Successfully found meeting by Join URL for user: {UserId}", effectiveUserId);

            return new MeetingInfo
            (
                meeting.Id ?? "", // This is the ID we'll use for transcript retrieval
                meeting.Participants?.Organizer?.Identity?.User?.DisplayName ?? "Unknown Organizer",
                meeting.StartDateTime ?? DateTimeOffset.UtcNow,
                meeting.EndDateTime,
                meeting.IsBroadcast ?? false
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find meeting by Join URL.");
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
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Creating subscription for meeting {MeetingId}", meetingId);

            // Note: This subscription should trigger webhook notifications that cause
            // GetNewTranscriptSegmentsAsync to be called, which executes delta queries.
            // The delta query automatically handles skipToken updates in the deltaLink.
            // Each delta query response contains an updated @odata.deltaLink with a new skipToken.
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

            _logger.LogInformation("Created subscription {SubscriptionId} for meeting {MeetingId}. Webhook notifications will trigger delta queries that automatically refresh the deltaLink with updated skipToken.",
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

            var updatedSubscription = await _graphClient.Subscriptions[subscriptionId]
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

            // Get an access token with the required scopes for transcript access
            var accessToken = await GetAccessTokenWithScopesAsync(cancellationToken);

            if (string.IsNullOrEmpty(accessToken))
            {
                _logger.LogError("Failed to obtain access token for transcript download");
                return string.Empty;
            }

            using var httpClient = new HttpClient();

            // Add the Bearer token for authentication
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            httpClient.DefaultRequestHeaders.Add("User-Agent", "TeamsMeetingAssistant/1.0");

            var response = await httpClient.GetAsync(transcriptContentUrl, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogDebug("Successfully downloaded authenticated transcript content, length: {Length}", content.Length);
                return content;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogError("Unauthorized access to transcript URL. Token may be invalid or missing required scopes.");
                _logger.LogInformation("Required scopes: Calendars.Read CallRecordings.Read.All CallTranscripts.Read.All OnlineMeetingArtifact.Read.All OnlineMeetingRecording.Read.All OnlineMeetings.Read OnlineMeetings.ReadWrite OnlineMeetingTranscript.Read.All");
                return string.Empty;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogError("Forbidden access to transcript URL. User may not have permission to access this transcript.");
                return string.Empty;
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

    private async Task<string?> GetAccessTokenWithScopesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // For Application Permissions (client credentials flow), you need to use the ".default" scope
            // The actual permissions are configured in Azure AD app registration, not in the token request
            var scope = "https://graph.microsoft.com/.default";

            _logger.LogDebug("Attempting to obtain access token with application permissions scope: {Scope}", scope);

            // Use client credentials flow with the existing client secret
            // TODO: These should come from configuration/appsettings
            var tenantId = "TBD"; // TODO: Get from configuration
            var clientId = "TBD"; // TODO: Get from configuration  
            var clientSecret = "TBD"; // TODO: Get from configuration

            var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

            using var httpClient = new HttpClient();

            var tokenRequest = new Dictionary<string, string>
            {
                {"client_id", clientId},
                {"client_secret", clientSecret},
                {"scope", scope}, // Use .default scope for application permissions
                {"grant_type", "client_credentials"}
            };

            var tokenRequestContent = new FormUrlEncodedContent(tokenRequest);

            _logger.LogDebug("Making token request to: {TokenEndpoint}", tokenEndpoint);

            var tokenResponse = await httpClient.PostAsync(tokenEndpoint, tokenRequestContent, cancellationToken);

            if (tokenResponse.IsSuccessStatusCode)
            {
                var tokenResponseContent = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);

                // Parse the JSON response to extract the access token
                using var jsonDoc = System.Text.Json.JsonDocument.Parse(tokenResponseContent);

                if (jsonDoc.RootElement.TryGetProperty("access_token", out var accessTokenElement))
                {
                    var accessToken = accessTokenElement.GetString();

                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        _logger.LogInformation("Successfully obtained application access token");

                        // Optionally log token expiration
                        if (jsonDoc.RootElement.TryGetProperty("expires_in", out var expiresInElement))
                        {
                            var expiresIn = expiresInElement.GetInt32();
                            _logger.LogDebug("Token expires in {ExpiresIn} seconds", expiresIn);
                        }

                        // Log information about required Azure AD app permissions
                        _logger.LogInformation("Ensure your Azure AD app registration has the following Application Permissions:");
                        _logger.LogInformation("- Calendars.Read.All");
                        _logger.LogInformation("- OnlineMeetings.Read.All");
                        _logger.LogInformation("- OnlineMeetingTranscript.Read.All");
                        _logger.LogInformation("- CallRecords.Read.All");
                        _logger.LogInformation("- User.Read.All");
                        _logger.LogInformation("And that admin consent has been granted for these permissions.");

                        return accessToken;
                    }
                }

                _logger.LogError("Access token not found in response");
                return null;
            }
            else
            {
                var errorContent = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Failed to obtain access token: {StatusCode} - {ReasonPhrase}. Response: {ErrorContent}",
                    tokenResponse.StatusCode, tokenResponse.ReasonPhrase, errorContent);

                // Try to parse error details from the response
                try
                {
                    using var errorDoc = System.Text.Json.JsonDocument.Parse(errorContent);
                    if (errorDoc.RootElement.TryGetProperty("error", out var errorElement))
                    {
                        var error = errorElement.GetString();
                        _logger.LogError("OAuth2 Error: {Error}", error);

                        if (errorDoc.RootElement.TryGetProperty("error_description", out var errorDescElement))
                        {
                            var errorDescription = errorDescElement.GetString();
                            _logger.LogError("OAuth2 Error Description: {ErrorDescription}", errorDescription);

                            // Provide specific guidance for common errors
                            if (errorDescription?.Contains("AADSTS70011") == true)
                            {
                                _logger.LogError("Error AADSTS70011 indicates invalid scope. For application permissions, use 'https://graph.microsoft.com/.default'");
                            }
                            else if (errorDescription?.Contains("AADSTS65001") == true)
                            {
                                _logger.LogError("Error AADSTS65001 indicates the app registration doesn't have the required permissions or admin consent is missing.");
                            }
                        }
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                    // Ignore JSON parsing errors for error response
                }

                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obtaining access token");
            return null;
        }
    }

    // Helper method to get user ID (for testing purposes)
    public async Task<string> GetMyUserIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            // This requires delegated permissions, but useful for finding your user ID
            var me = await _graphClient.Me.GetAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("Current user ID: {UserId} ({DisplayName})", me?.Id, me?.DisplayName);
            return me?.Id ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get current user ID");
            return "";
        }
    }

    private class TranscriptDeltaResult
    {
        public List<Microsoft.Graph.Models.CallTranscript> Transcripts { get; set; } = new();
        public string? DeltaLink { get; set; }
    }
}
