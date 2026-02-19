using TeamsMeetingAssistant.Core.Interfaces;
using TeamsMeetingAssistant.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var origins = (builder.Configuration["AllowedOrigins"] ?? "http://localhost:5002")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        policy.WithOrigins(origins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
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
builder.Services.AddSingleton<IMeetingSessionStore, InMemoryMeetingSessionStore>();

// Register infrastructure dependencies
builder.Services.AddSingleton<TeamsMeetingAssistant.Infrastructure.VttTranscriptParser>();

// Real Graph API service - requires AzureAd config in appsettings
builder.Services.AddScoped<ITranscriptService, TeamsMeetingAssistant.Infrastructure.GraphTranscriptService>();

// Option 2: Azure OpenAI Assistants (per-meeting document knowledge)
if (builder.Configuration.GetValue<bool>("AzureOpenAI:AssistantsEnabled"))
{
    builder.Services.AddScoped<IMeetingDocumentService, AssistantsDocumentService>();
    builder.Services.AddScoped<IQuestionGenerationService, AzureOpenAIAssistantsQuestionService>();
}
else if (!string.IsNullOrEmpty(builder.Configuration["AzureOpenAI:Endpoint"]))
{
    builder.Services.AddScoped<IMeetingDocumentService, NullMeetingDocumentService>();
    builder.Services.AddScoped<IQuestionGenerationService, AzureOpenAIQuestionService>();
}
else
{
    builder.Services.AddScoped<IMeetingDocumentService, NullMeetingDocumentService>();
    builder.Services.AddScoped<IQuestionGenerationService, MockOpenAIQuestionService>();
}

// Option 3: Azure AI Search (org-wide knowledge base)
if (!string.IsNullOrEmpty(builder.Configuration["AzureAISearch:Endpoint"]))
{
    builder.Services.AddScoped<IOrgKnowledgeService, AzureSearchOrgKnowledgeService>();
}
else
{
    builder.Services.AddScoped<IOrgKnowledgeService, NullOrgKnowledgeService>();
}

builder.Services.AddScoped<ISignalRService, TeamsMeetingAssistant.Api.SignalRHubService>();

// Add background services
builder.Services.AddHostedService<TeamsMeetingAssistant.Api.TranscriptPollingService>();
builder.Services.AddHostedService<TeamsMeetingAssistant.Api.SubscriptionRenewalService>();

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
