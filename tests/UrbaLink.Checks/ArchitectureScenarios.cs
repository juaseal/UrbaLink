using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using UrbaLink.Contracts;

internal static class ArchitectureScenarios
{
    public static async Task Run(string url)
    {
        var connection = Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION") ?? throw new Exception("Ejecuta Test-Posts.ps1 -Suite architecture.");
        var settings = new NpgsqlConnectionStringBuilder(connection);
        if (!settings.Database!.StartsWith("urbalink_posts_test_")) throw new Exception("Solo se permiten bases temporales.");
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        async Task Sql(string sql, params object[] values)
        {
            await using var q = new NpgsqlCommand(sql, db);
            foreach (var value in values) q.Parameters.Add(new NpgsqlParameter { Value = value });
            await q.ExecuteNonQueryAsync();
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        async Task Expect(HttpResponseMessage response, HttpStatusCode expected)
        { using (response) Check(response.StatusCode == expected, $"Esperado {expected}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); }
        async Task<T> Post<T>(HttpClient client, string path, object value)
        {
            using var response = await client.PostAsJsonAsync(path, value);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        using var owner = new HttpClient { BaseAddress = new Uri(url) };
        using var alice = new HttpClient { BaseAddress = new Uri(url) };
        using var bob = new HttpClient { BaseAddress = new Uri(url) };
        foreach (var client in new[] { owner, alice, bob })
        {
            var session = await Post<SessionDto>(client, "api/auth/register", new RegisterRequest("Arquitectura "+Guid.NewGuid().ToString("N"), $"{Guid.NewGuid():N}@example.test", "Architecture-tests-123"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        }
        var community = await Post<CommunityDto>(owner, "api/communities", new CreateCommunityRequest("Arquitectura"));
        var root = $"api/communities/{community.Id}";
        foreach (var client in new[] { alice, bob })
        {
            var invite = await Post<InvitationDto>(owner, root + "/invitations", new { });
            await Post<CommunityDto>(client, "api/communities/join", new JoinRequest(invite.Code));
        }
        var service = await Post<PostDto>(owner, root + "/posts", new CreatePostRequest("Servicio", "Consultas privadas", "Ofrezco"));
        var chats = root + $"/services/{service.Id}/conversations";
        var a = await Post<MarketConversationDto>(alice, chats, new { });
        var b = await Post<MarketConversationDto>(bob, chats, new { });
        var am = chats + $"/{a.Id}/messages";
        var bm = chats + $"/{b.Id}/messages";

        // Hold a compatible SHARE lock on the service: reads and unrelated conversations must proceed.
        await using (var transaction = await db.BeginTransactionAsync())
        {
            await Sql("SELECT id FROM posts WHERE id=$1 FOR SHARE", service.Id);
            await Expect(await owner.GetAsync(root + $"/services/{service.Id}").WaitAsync(TimeSpan.FromSeconds(3)), HttpStatusCode.OK);
            await Expect(await bob.PostAsJsonAsync(bm, new MarketMessageRequest(Guid.NewGuid(), "Independiente")).WaitAsync(TimeSpan.FromSeconds(3)), HttpStatusCode.NoContent);
            await transaction.RollbackAsync();
        }
        // Block only A's conversation; B must not wait behind A (or behind a lock on the service).
        await using (var transaction = await db.BeginTransactionAsync())
        {
            await Sql("SELECT id FROM market_conversations WHERE id=$1 FOR UPDATE", a.Id);
            var pending = alice.PostAsJsonAsync(am, new MarketMessageRequest(Guid.NewGuid(), "Bloqueado"));
            try
            {
                await Task.Delay(150);
                Check(!pending.IsCompleted, "El envío no respetó el bloqueo de su conversación");
                await Expect(await bob.PostAsJsonAsync(bm, new MarketMessageRequest(Guid.NewGuid(), "Otra consulta")).WaitAsync(TimeSpan.FromSeconds(3)), HttpStatusCode.NoContent);
            }
            finally { await transaction.RollbackAsync(); }
            await Expect(await pending, HttpStatusCode.NoContent);
        }
        var repeated = new MarketMessageRequest(Guid.NewGuid(), "Un único envío");
        foreach (var response in await Task.WhenAll(alice.PostAsJsonAsync(am, repeated), alice.PostAsJsonAsync(am, repeated)))
            await Expect(response, HttpStatusCode.NoContent);
        var history = (await owner.GetFromJsonAsync<MarketMessagePage>(am))!;
        Check(history.Items.Count(m => m.Body == repeated.Body) == 1, "Duplicación al enviar concurrentemente");
        var count = (await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!;
        var inbox = (await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!;
        Check(count.UnreadCount == 4 && count.UnreadCount == inbox.UnreadCount, "Contador distinto de la bandeja");

        // A committed archive wins over a request that was waiting for the community gate.
        var inviteBeforeArchive = await Post<InvitationDto>(owner, root + "/invitations", new { });
        var general = await Post<PostDto>(owner, root + "/posts", new CreatePostRequest("Público", "Comentarios", "General"));
        await using (var transaction = await db.BeginTransactionAsync())
        {
            await Sql("UPDATE communities SET deleted_at=now() WHERE id=$1", community.Id);
            var post = alice.PostAsJsonAsync(root + "/posts", new CreatePostRequest("No aceptado", "Archivada", "General"));
            var invite = owner.PostAsJsonAsync(root + "/invitations", new { });
            var comment = alice.PostAsJsonAsync(root + $"/posts/{general.Id}/comments", new CreateCommentRequest("No aceptado", Guid.NewGuid()));
            var join = bob.PostAsJsonAsync("api/communities/join", new JoinRequest(inviteBeforeArchive.Code));
            await Task.Delay(200);
            await transaction.CommitAsync();
            await Expect(await post, HttpStatusCode.Forbidden);
            await Expect(await invite, HttpStatusCode.Forbidden);
            await Expect(await comment, HttpStatusCode.NotFound);
            await Expect(await join, HttpStatusCode.BadRequest);
        }
        Check((await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount == 0, "El contador incluye comunidades archivadas");
        await Expect(await owner.PostAsJsonAsync(root + "/restore", new { }), HttpStatusCode.NoContent);

        // The architecture suite runs with four admission slots. Saturation must reject
        // extra work promptly while keeping liveness reachable.
        await using (var transaction = await db.BeginTransactionAsync())
        {
            await Sql("SELECT id FROM communities WHERE id=$1 FOR UPDATE", community.Id);
            var requests = Enumerable.Range(0, 4).Select(_ => owner.GetAsync(root + $"/services/{service.Id}")).ToArray();
            try
            {
                var waiting = 0L;
                for (var attempt = 0; attempt < 40 && waiting < 4; attempt++)
                {
                    // Statistics are cached within this transaction; refresh the snapshot
                    // so requests reaching their locks after the first poll are visible.
                    await Sql("SELECT pg_stat_clear_snapshot()");
                    await using var query = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%FOR SHARE OF c%'", db);
                    waiting = (long)(await query.ExecuteScalarAsync())!;
                    if (waiting < 4) await Task.Delay(50);
                }
                Check(waiting == 4, "No se alcanzó la saturación controlada");
                using var rejected = await bob.GetAsync("api/me");
                Check(rejected.StatusCode == HttpStatusCode.ServiceUnavailable && rejected.Headers.RetryAfter is not null, "No se limitó el trabajo simultáneo");
                await Expect(await bob.GetAsync("health/live"), HttpStatusCode.OK);
            }
            finally { await transaction.RollbackAsync(); }
            foreach (var response in await Task.WhenAll(requests)) await Expect(response, HttpStatusCode.OK);
        }

        // A stalled lock returns an explicit temporary failure and releases its connection.
        await using (var transaction = await db.BeginTransactionAsync())
        {
            await Sql("SELECT id FROM market_conversations WHERE id=$1 FOR UPDATE", a.Id);
            await Expect(await alice.PostAsJsonAsync(am, new MarketMessageRequest(Guid.NewGuid(), "Timeout")), HttpStatusCode.ServiceUnavailable);
            await transaction.RollbackAsync();
        }
        await Expect(await alice.PostAsJsonAsync(am, new MarketMessageRequest(Guid.NewGuid(), "Recuperado")), HttpStatusCode.NoContent);

        // Readiness must reject a ledger missing a migration even when PostgreSQL answers SELECT 1.
        int latestVersion;
        await using (var query = new NpgsqlCommand("SELECT max(version) FROM schema_versions", db)) latestVersion = (int)(await query.ExecuteScalarAsync())!;
        await Sql("DELETE FROM schema_versions WHERE version=$1", latestVersion);
        try
        {
            using var response = await owner.GetAsync("health/ready");
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable && (await response.Content.ReadFromJsonAsync<ServerStatus>())!.Status == "database_schema_outdated", "Readiness no detecta esquema incompleto");
        }
        finally { await Sql("INSERT INTO schema_versions(version) VALUES($1)", latestVersion); }
        await Expect(await owner.GetAsync("health/ready"), HttpStatusCode.OK);

        // Accounts on one IP have independent API budgets. Auth throttling remains per IP.
        var throttled = false;
        for (var i = 0; i < 125; i++)
        {
            using var response = await alice.GetAsync("api/me");
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { throttled = true; break; }
            Check(response.IsSuccessStatusCode, "Fallo inesperado al comprobar el límite");
        }
        Check(throttled, "No se aplicó el límite por cuenta");
        await Expect(await bob.GetAsync("api/me"), HttpStatusCode.OK);
        Console.WriteLine("OK: bloqueos por conversación, consultas paralelas, idempotencia concurrente, archivado concurrente, timeout recuperable, readiness del esquema y límites independientes por cuenta.");
    }
}
