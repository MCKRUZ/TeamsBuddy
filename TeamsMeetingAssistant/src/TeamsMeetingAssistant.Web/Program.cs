using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using TeamsMeetingAssistant.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages(options =>
{
    // Ensure the Privacy and Terms of Use pages are served
    options.Conventions.AddPageRoute("/Privacy", "privacy");
    options.Conventions.AddPageRoute("/Terms", "terms");
});

// Configure Blazor Server with dev tunnel specific settings
builder.Services.AddServerSideBlazor(options =>
{
    options.DetailedErrors = builder.Environment.IsDevelopment();
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    options.JSInteropDefaultCallTimeout = TimeSpan.FromMinutes(1);
    options.MaxBufferedUnacknowledgedRenderBatches = 10;
    
    // Dev tunnel specific configurations
    options.DisconnectedCircuitMaxRetained = 100;
}).AddHubOptions(options =>
{
    // Configure SignalR hub for dev tunnels
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.MaximumReceiveMessageSize = 32 * 1024;
    options.StreamBufferCapacity = 10;
});

// Add Response Compression for SignalR
builder.Services.AddResponseCompression(opts =>
{
    opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/octet-stream" });
});

builder.Services.AddSingleton<WeatherForecastService>();

// Add HttpClient for API calls
builder.Services.AddHttpClient("TeamsMeetingApi", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["ApiBaseUrl"] ?? "https://localhost:7128");
});

// Add CORS for dev tunnels
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "https://lv3nz16d-7251.use.devtunnels.ms",
                "https://localhost:7251",
                "http://localhost:7251"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed((host) => true); // Allow dev tunnels
    });
});

var app = builder.Build();

// Use response compression
app.UseResponseCompression();


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Configure static files with proper MIME types - simplified approach
app.UseStaticFiles();

// Add specific handling for framework files
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/_framework",
    OnPrepareResponse = ctx =>
    {
        // Force correct MIME type for all JavaScript files in _framework
        if (ctx.File.Name.EndsWith(".js"))
        {
            ctx.Context.Response.Headers.ContentType = "application/javascript";
        }
        
        // Add dev tunnel friendly headers
        ctx.Context.Response.Headers.Append("Cache-Control", "no-cache");
        ctx.Context.Response.Headers.Append("Access-Control-Allow-Origin", "*");
    }
});

app.UseRouting();

// Use CORS - MUST be after UseRouting() and before endpoint mapping
app.UseCors();

// Map Blazor Hub with dev tunnel optimized configuration
app.MapBlazorHub(options =>
{
    // Force LongPolling for dev tunnels as WebSockets can be unreliable
    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
    options.CloseOnAuthenticationExpiration = true;
    
    // Dev tunnel specific timeouts
    options.ApplicationMaxBufferSize = 64 * 1024;
    options.TransportMaxBufferSize = 64 * 1024;
}).RequireCors();

// Add diagnostic endpoint for dev tunnel debugging
app.MapGet("/debug/framework", () =>
{
    return Results.Json(new
    {
        Environment = app.Environment.EnvironmentName,
        BlazorFrameworkPath = "/_framework/blazor.server.js",
        ContentRoot = app.Environment.ContentRootPath,
        WebRoot = app.Environment.WebRootPath,
        Message = "Framework debug info"
    });
});

app.MapRazorPages();
app.MapFallbackToPage("/_Host");

app.Run();