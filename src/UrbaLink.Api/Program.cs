using Npgsql;
using UrbaLink.Contracts;
using UrbaLink.Api;
using Microsoft.AspNetCore.Authentication;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.AddDeployment();
// Allows a 40 KB JPEG plus JSON escaping of the full object description.
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 128 * 1024);
// Console logging works locally and in containers without Windows Event Log permissions.
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DatabaseUnavailableHandler>();
builder.Services.AddSingleton<RequestAdmission>();
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthentication>("Session", null);
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    foreach (var (name, limit) in new[] { ("auth", 20), ("api", 120) })
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            name == "api" && context.User.Identity?.IsAuthenticated == true
                ? "user:" + Tokens.UserId(context.User)
                : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var connectionString = DeploymentConfiguration.DatabaseConnection(builder.Configuration);
if (builder.Configuration.GetValue<bool>("Database:MigrateOnly") && string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Configura ConnectionStrings:UrbaLink antes de ejecutar las migraciones.");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    var connection = new NpgsqlConnectionStringBuilder(connectionString);
    if (!connection.ContainsKey("Timeout")) connection.Timeout = 5;
    if (!connection.ContainsKey("Command Timeout")) connection.CommandTimeout = 10;
    if (string.IsNullOrWhiteSpace(connection.Options)) connection.Options = "-c lock_timeout=5000 -c statement_timeout=10000";
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connection.ConnectionString));
    builder.Services.AddHostedService<ExpiredSessionCleanup>();
}

var app = builder.Build();
app.UseExceptionHandler();
if (builder.Configuration.GetValue<bool>("Hosting:RailwayEdge"))
    app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
    // Internal readiness probes have no forwarded HTTPS headers.
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"),
        pipeline => pipeline.UseHttpsRedirection());
app.Use(async(context,next)=>
{
    if(context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/api/platform"))
    {
        context.Response.Headers.CacheControl="no-store";
        context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
        context.Response.Headers["X-Content-Type-Options"]="nosniff";
        context.Response.Headers["Referrer-Policy"]="no-referrer";
    }
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseMiddleware<RequestAdmission>();
app.UseAuthentication();
app.UseMiddleware<PlatformAccessGate>();
app.UseRateLimiter();
app.UseAuthorization();

if (app.Services.GetService<NpgsqlDataSource>() is { } database)
{
    if (builder.Configuration.GetValue<bool>("Database:MigrateOnly"))
    {
        await Database.MigrateAsync(database);
        return;
    }
    if (builder.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
        await Database.MigrateAsync(database);
    app.MapAccounts();
    app.MapPosts();
    app.MapComments();
    app.MapCommunityManagement();
    app.MapMarket();
    app.MapServiceChats();
    app.MapNotifications();
    app.MapRides();
    app.MapAnimalNotices();
    app.MapPlatform();
    app.MapReports();
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
        await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
        if (!await Database.SchemaReady(connection, timeout.Token))
            return Results.Json(new ServerStatus("UrbaLink", "database_schema_outdated", DateTimeOffset.UtcNow),
                statusCode: StatusCodes.Status503ServiceUnavailable);
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
