using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

namespace UrbaLink.Api;

internal static class DeploymentConfiguration
{
    public static string? DatabaseConnection(IConfiguration config)
    {
        var configured = config.GetConnectionString("UrbaLink");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (string.IsNullOrWhiteSpace(config["PGHOST"])) return null;
        string Required(string name) => !string.IsNullOrWhiteSpace(config[name]) ? config[name]!
            : throw new InvalidOperationException($"Falta configurar {name} para PostgreSQL.");
        return new NpgsqlConnectionStringBuilder
        {
            Host = Required("PGHOST"),
            Port = config.GetValue("PGPORT", 5432),
            Database = Required("PGDATABASE"),
            Username = Required("PGUSER"),
            Password = Required("PGPASSWORD"),
            // Password-based Railway PostgreSQL needs no Kerberos negotiation.
            GssEncryptionMode = GssEncryptionMode.Disable
        }.ConnectionString;
    }

    public static void AddDeployment(this WebApplicationBuilder builder)
    {
        // Railway terminates HTTPS and supplies X-Real-IP. Opt in only when this
        // service has HTTP ingress through Railway and no public TCP proxy.
        if (!builder.Configuration.GetValue<bool>("Hosting:RailwayEdge")) return;
        if (string.IsNullOrWhiteSpace(builder.Configuration["RAILWAY_PROJECT_ID"]))
            throw new InvalidOperationException("RailwayEdge requiere un entorno Railway.");
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardedForHeaderName = "X-Real-IP";
            options.ForwardLimit = 1;
            // Railway does not publish stable proxy IP ranges. Network isolation
            // is the trust boundary: all access to the container must be trusted.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
    }
}
