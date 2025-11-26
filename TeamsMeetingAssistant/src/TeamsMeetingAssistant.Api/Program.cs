using TeamsMeetingAssistant.Core.Interfaces;
using TeamsMeetingAssistant.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add HttpClient for API calls
builder.Services.AddHttpClient();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(builder.Configuration["AllowedOrigins"] ?? "http://localhost:5002")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials()
              .SetIsOriginAllowed((host) => true); // Allow any origin for development
    });
});

// Configure SignalR
var signalRConnectionString = builder.Configuration["Azure:SignalR:ConnectionString"];
if (!string.IsNullOrEmpty(signalRConnectionString))
{
    builder.Services.AddSignalR().AddAzureSignalR(signalRConnectionString);
}
else
{
    builder.Services.AddSignalR();
}

// Register application services
builder.Services.AddGraphClient(builder.Configuration);
builder.Services.AddSingleton<IMeetingSessionStore, InMemoryMeetingSessionStore>();

// Register Conversation Store for chat memory
builder.Services.AddSingleton<IConversationStore, InMemoryConversationStore>();

// Register Token Exchange Service for SSO
builder.Services.AddScoped<ITokenExchangeService, TokenExchangeService>();

// Register Chat Service - it will receive IConversationStore through DI
builder.Services.AddScoped<IChatService, AzureOpenAIChatService>();

// Use mock services for demonstration - replace with real services in production
var useMockServices = builder.Configuration.GetValue<bool>("UseMockServices", false);

if (useMockServices)
{
    // Mock service not implemented - using real service
    builder.Services.AddScoped<ITranscriptService, GraphTranscriptService>();
}
else
{
    builder.Services.AddScoped<ITranscriptService, GraphTranscriptService>();
}

// Register real services
builder.Services.AddScoped<IQuestionGenerationService, OpenAIQuestionService>();
builder.Services.AddScoped<IQAEvaluationService, OpenAIQAEvaluationService>();
builder.Services.AddScoped<ISignalRService, TeamsMeetingAssistant.Api.SignalRHubService>();

// Register SubscriptionRenewalService as singleton (both for DI and as hosted service)
builder.Services.AddSingleton<TeamsMeetingAssistant.Api.SubscriptionRenewalService>();

// Register background services
// NOTE: TranscriptPollingService is DISABLED to avoid duplicate processing
// Real-time monitoring is handled by MeetingController.StartRealTimeTranscriptMonitoringAsync
// builder.Services.AddHostedService<TranscriptPollingService>();

builder.Services.AddHostedService<TeamsMeetingAssistant.Api.SubscriptionRenewalService>(sp => 
    sp.GetRequiredService<TeamsMeetingAssistant.Api.SubscriptionRenewalService>());

// Add basic health checks
builder.Services.AddHealthChecks()
    .AddCheck("mock_health", () => HealthCheckResult.Healthy("All mock services are running"));

// Add logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

// Map SignalR hub
app.MapHub<TeamsMeetingAssistant.Api.TranscriptHub>("/transcripthub");

// Add simple health check endpoint
app.MapGet("/", () => "Teams Meeting Assistant API is running!");

app.Run();