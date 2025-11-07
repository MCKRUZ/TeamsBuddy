using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using System;

public static class GraphClientExtensions
{
    public static IServiceCollection AddGraphClient(this IServiceCollection services, IConfiguration configuration)
    {
        var clientId = configuration["AzureAd:ClientId"];
        var tenantId = configuration["AzureAd:TenantId"];
        var clientSecret = configuration["AzureAd:ClientSecret"];

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientSecret))
        {
            throw new InvalidOperationException("Azure AD configuration is missing. Check AzureAd:ClientId, AzureAd:TenantId, and AzureAd:ClientSecret in configuration.");
        }

        // Define the scopes required. For application permissions, use the default scope.
        // The specific onlineMeetings permissions must be granted in the Azure AD app registration portal.
        var scopes = new[] { "https://graph.microsoft.com/.default" };

        // Create the credential using Azure.Identity
        var clientSecretCredential = new ClientSecretCredential(
            tenantId,
            clientId,
            clientSecret);

        // Register the GraphServiceClient as a singleton, passing the credential and scopes.
        // The GraphServiceClient internally handles token acquisition/refresh using the credential.
        services.AddSingleton<GraphServiceClient>(serviceProvider =>
        {
            // Note: The new GraphServiceClient constructor automatically manages the HttpClient lifecycle 
            // and authentication flow using the provided credential. Logging integration might require 
            // a custom approach if the default implementation doesn't suffice.

            var graphClient = new GraphServiceClient(clientSecretCredential, scopes);
            return graphClient;
        });

        return services;
    }
}
