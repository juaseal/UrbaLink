using System.Net;
using System.Net.Http.Json;
using UrbaLink.Contracts;

var cases = new (string? Value, bool AllowHttp, bool Valid)[]
{
    ("http://192.168.1.50:5080", true, true),
    ("http://10.0.2.2:5080/", true, true),
    (" https://api.example.com ", false, true),
    ("http://192.168.1.50:5080", false, false),
    ("ftp://example.com", true, false),
    ("https://user:password@example.com", true, false),
    ("https://example.com/api", true, false),
    ("https://example.com?token=secret", true, false),
    ("https://example.com/#fragment", true, false),
    ("192.168.1.50:5080", true, false),
    (null, true, false)
};
foreach (var item in cases)
{
    if (ServerAddress.TryParse(item.Value, item.AllowHttp, out _) != item.Valid)
        throw new Exception($"Falló la validación de dirección: {item.Value}");
}
Console.WriteLine($"OK: {cases.Length} comprobaciones de direcciones.");

if (args.Length == 2 && args[1] == "web") { await WebScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "platform") { await PlatformScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "animals") { await AnimalScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "services") { await ServiceScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "architecture") { await ArchitectureScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "rides") { await RideScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "market") { await MarketScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "lifecycle") { await LifecycleScenarios.Run(args[0]); return; }
if (args.Length == 2 && args[1] == "comments")
{
    await CommentScenarios.Run(args[0]);
    return;
}

if (args.Length == 2 && args[1] == "posts")
{
    await PostScenarios.Run(args[0]);
    return;
}

if (args.Length == 2 && args[1] == "accounts")
{
    await AccountScenarios.Run(args[0]);
    return;
}

if (args.Length == 2)
{
    using var client = new HttpClient { BaseAddress = new Uri(args[0]), Timeout = TimeSpan.FromSeconds(10) };
    var live = await client.GetFromJsonAsync<ServerStatus>("health/live");
    if (live is not { Service: "UrbaLink", Status: "alive" })
        throw new Exception("La API no identifica correctamente su estado.");
    using var response = await client.GetAsync("health/ready");
    var ready = await response.Content.ReadFromJsonAsync<ServerStatus>();
    var expectedHttpStatus = args[1] == "ready" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
    if (response.StatusCode != expectedHttpStatus || ready?.Status != args[1])
        throw new Exception($"Estado inesperado: {response.StatusCode}, {ready?.Status}");
    Console.WriteLine($"OK: API activa y estado de PostgreSQL '{args[1]}'.");
}
