using Npgsql;
using UrbaLink.Contracts;
using UrbaLink.Api;
using Microsoft.AspNetCore.Authentication;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
// Allows a 40 KB JPEG plus JSON escaping of the full object description.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);
// Console logging works locally and in containers without Windows Event Log permissions.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("Session", null);
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    foreach (var (name, limit) in new[] { ("auth", 20), ("api", 120) })
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var connectionString = builder.Configuration.GetConnectionString("UrbaLink");
if (!string.IsNullOrWhiteSpace(connectionString))
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

var app = builder.Build();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Services.GetService<NpgsqlDataSource>() is { } database)
{
    if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
        await Database.MigrateAsync(database);
    app.MapAccounts();
    app.MapPosts();
    app.MapComments();
    app.MapCommunityManagement();
    app.MapMarket();
    app.MapRides();
}

app.MapGet("/health/live", () => Results.Ok(
    new ServerStatus("UrbaLink", "alive", DateTimeOffset.UtcNow)));

app.MapGet("/health/ready", async (IServiceProvider services, ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var dataSource = services.GetService<NpgsqlDataSource>();
    if (dataSource is null)
        return Results.Json(new ServerStatus("UrbaLink", "database_not_configured", DateTimeOffset.UtcNow),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        await command.ExecuteScalarAsync(timeout.Token);
        return Results.Ok(new ServerStatus("UrbaLink", "ready", DateTimeOffset.UtcNow));
    }
    catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException)
    {
        logger.LogWarning("PostgreSQL no está disponible ({ErrorType}).", exception.GetType().Name);
        return Results.Json(new ServerStatus("UrbaLink", "database_unavailable", DateTimeOffset.UtcNow),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();



