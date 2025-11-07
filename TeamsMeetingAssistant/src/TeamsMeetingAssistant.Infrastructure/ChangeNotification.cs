using System.Text.Json.Serialization;

namespace TeamsMeetingAssistant.Infrastructure;

public class ChangeNotificationCollection
{
    [JsonPropertyName("value")]
    public List<ChangeNotification>? Value { get; set; }
}

public class ChangeNotification
{
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("changeType")]
    public string? ChangeType { get; set; }

    [JsonPropertyName("clientState")]
    public string? ClientState { get; set; }

    [JsonPropertyName("subscriptionExpirationDateTime")]
    public DateTimeOffset? SubscriptionExpirationDateTime { get; set; }

    [JsonPropertyName("resource")]
    public string? Resource { get; set; }

    [JsonPropertyName("resourceData")]
    public ResourceData? ResourceData { get; set; }
}

public class ResourceData
{
    [JsonPropertyName("@odata.type")]
    public string? ODataType { get; set; }

    [JsonPropertyName("@odata.id")]
    public string? ODataId { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }
}