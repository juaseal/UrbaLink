using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using UrbaLink.Contracts;

internal static class AccountScenarios
{
    public static async Task Run(string url)
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(url) };
        var suffix = Guid.NewGuid().ToString("N");
        const string password = "Prueba-Segura-12345";
        async Task Expect(HttpResponseMessage response, HttpStatusCode status)
        {
            using (response)
            {
                if (response.StatusCode != status)
                    throw new Exception($"Se esperaba {status}, recibido {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            }
        }
        async Task<SessionDto> Register(string name)
        {
            using var response = await anonymous.PostAsJsonAsync("api/auth/register",
                new RegisterRequest(name, $"{name}.{suffix}@example.test", password));
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<SessionDto>())!;
        }
        HttpClient Signed(SessionDto session)
        {
            var client = new HttpClient { BaseAddress = new Uri(url) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            return client;
        }
        async Task<T> Post<T>(HttpClient client, string path, object value)
        {
            using var response = await client.PostAsJsonAsync(path, value);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }

        await Expect(await anonymous.GetAsync("api/communities"), HttpStatusCode.Unauthorized);
        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",
            new RegisterRequest("No", "invalid", "short")), HttpStatusCode.BadRequest);
        var adminSession = await Register("Admin");
        var memberSession = await Register("Member");
        var otherSession = await Register("Other");
        var fourthSession = await Register("Fourth");
        using var admin = Signed(adminSession);
        using var member = Signed(memberSession);
        using var other = Signed(otherSession);
        using var fourth = Signed(fourthSession);

        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",
            new RegisterRequest("Duplicate", adminSession.Account.Email.ToUpperInvariant(), password)), HttpStatusCode.Conflict);
        await Expect(await anonymous.PostAsJsonAsync("api/auth/login",
            new LoginRequest(adminSession.Account.Email, "wrong")), HttpStatusCode.Unauthorized);
        var login = await Post<SessionDto>(anonymous, "api/auth/login",
            new LoginRequest(" " + adminSession.Account.Email.ToUpperInvariant() + " ", password));
        if (login.Account.Id != adminSession.Account.Id) throw new Exception("No se normalizó el correo.");
        if (login.ExpiresAt < DateTimeOffset.UtcNow.AddDays(179) || adminSession.ExpiresAt < DateTimeOffset.UtcNow.AddDays(179))
            throw new Exception("El registro o el login no crean una sesión persistente.");

        var community = await Post<CommunityDto>(admin, "api/communities", new CreateCommunityRequest("Comunidad " + suffix));
        if (community.Role != "Admin") throw new Exception("El creador no es administrador.");
        if ((await other.GetFromJsonAsync<CommunityDto[]>("api/communities"))!.Length != 0)
            throw new Exception("Se filtraron comunidades a otra cuenta.");
        var invitePath = $"api/communities/{community.Id}/invitations";
        await Expect(await other.PostAsync(invitePath, null), HttpStatusCode.Forbidden);
        var invitation = await Post<InvitationDto>(admin, invitePath, new { });
        var joined = await Post<CommunityDto>(member, "api/communities/join", new JoinRequest(invitation.Code.ToLowerInvariant()));
        if (joined.Id != community.Id || joined.Role != "Member") throw new Exception("Membresía incorrecta.");
        await Expect(await member.PostAsync(invitePath, null), HttpStatusCode.Forbidden);
        await Expect(await other.PostAsJsonAsync("api/communities/join", new JoinRequest(invitation.Code)), HttpStatusCode.BadRequest);

        var second = await Post<InvitationDto>(admin, invitePath, new { });
        await Expect(await member.PostAsJsonAsync("api/communities/join", new JoinRequest(second.Code)), HttpStatusCode.Conflict);
        var races = await Task.WhenAll(
            other.PostAsJsonAsync("api/communities/join", new JoinRequest(second.Code)),
            fourth.PostAsJsonAsync("api/communities/join", new JoinRequest(second.Code)));
        try
        {
            if (races.Count(r => r.StatusCode == HttpStatusCode.OK) != 1 ||
                races.Count(r => r.StatusCode == HttpStatusCode.BadRequest) != 1)
                throw new Exception("Una invitación se pudo consumir más de una vez.");
        }
        finally { foreach (var response in races) response.Dispose(); }
        await CheckCollectiveInvitations(admin, member, other, fourth, Expect);
        await Expect(await member.PostAsync("api/auth/logout", null), HttpStatusCode.NoContent);
        await Expect(await member.GetAsync("api/me"), HttpStatusCode.Unauthorized);
        await CheckPersistentSessions(login, adminSession, Signed, Expect);
        Console.WriteLine("OK: registro, login, normalización, aislamiento, permisos, invitaciones atómicas y revocación.");
    }

    private static async Task CheckCollectiveInvitations(HttpClient admin, HttpClient member, HttpClient other, HttpClient fourth,
        Func<HttpResponseMessage, HttpStatusCode, Task> expect)
    {
        async Task<T> Post<T>(HttpClient client, string path, object body)
        {
            using var response = await client.PostAsJsonAsync(path, body);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        var community = await Post<CommunityDto>(admin, "api/communities", new CreateCommunityRequest("Invitaciones colectivas"));
        var path = $"api/communities/{community.Id}/invitations";
        await expect(await admin.PostAsync(path+"?maxUses=0", null), HttpStatusCode.BadRequest);
        await expect(await admin.PostAsync(path+"?maxUses=1001", null), HttpStatusCode.BadRequest);
        await expect(await other.PostAsync(path+"?maxUses=2", null), HttpStatusCode.Forbidden);
        var invitation = await Post<InvitationDto>(admin, path+"?maxUses=2", new {});
        if (invitation.MaxUses != 2 || invitation.Id == Guid.Empty) throw new Exception("Invitación colectiva incorrecta.");
        await Post<CommunityDto>(member, "api/communities/join", new JoinRequest(invitation.Code));
        await expect(await member.PostAsJsonAsync("api/communities/join", new JoinRequest(invitation.Code)), HttpStatusCode.Conflict);
        var summaries = (await admin.GetFromJsonAsync<InvitationSummary[]>(path))!;
        if (summaries.Single().Uses != 1) throw new Exception("Un vecino repetido consume otro uso.");
        await expect(await member.GetAsync(path), HttpStatusCode.Forbidden);
        await expect(await member.DeleteAsync(path+"/"+invitation.Id), HttpStatusCode.Forbidden);
        var race = await Task.WhenAll(other.PostAsJsonAsync("api/communities/join", new JoinRequest(invitation.Code)),
            fourth.PostAsJsonAsync("api/communities/join", new JoinRequest(invitation.Code)));
        try
        {
            if(race.Count(r=>r.StatusCode==HttpStatusCode.OK)!=1 || race.Count(r=>r.StatusCode==HttpStatusCode.BadRequest)!=1)
                throw new Exception("Las entradas simultáneas exceden el límite colectivo.");
        }
        finally { foreach(var response in race) response.Dispose(); }
        if ((await admin.GetFromJsonAsync<InvitationSummary[]>(path))!.Length != 0)
            throw new Exception("Una invitación agotada aparece activa.");
        var revocable = await Post<InvitationDto>(admin, path+"?maxUses=100", new {});
        var otherCommunity = await Post<CommunityDto>(admin, "api/communities", new CreateCommunityRequest("Otra comunidad colectiva"));
        await expect(await admin.DeleteAsync($"api/communities/{otherCommunity.Id}/invitations/{revocable.Id}"), HttpStatusCode.NotFound);
        await expect(await admin.DeleteAsync(path+"/"+revocable.Id), HttpStatusCode.NoContent);
        await expect(await other.PostAsJsonAsync("api/communities/join", new JoinRequest(revocable.Code)), HttpStatusCode.BadRequest);
        if ((await member.GetFromJsonAsync<CommunityDto[]>("api/communities"))!.All(c=>c.Id!=community.Id))
            throw new Exception("Revocar una invitación expulsa a los vecinos existentes.");
        var expired = await Post<InvitationDto>(admin, path+"?maxUses=2", new {});
        var connection = Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION");
        if (new NpgsqlConnectionStringBuilder(connection).Database?.StartsWith("urbalink_posts_test_") != true)
            throw new Exception("Solo se permiten bases temporales.");
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE invitations SET expires_at=now()-interval '1 second' WHERE id=$1", db);
        command.Parameters.AddWithValue(expired.Id);
        await command.ExecuteNonQueryAsync();
        await expect(await other.PostAsJsonAsync("api/communities/join", new JoinRequest(expired.Code)), HttpStatusCode.BadRequest);
        Console.WriteLine("OK: invitaciones colectivas, límites concurrentes, usos únicos, permisos, aislamiento, revocación y caducidad.");
    }

    private static async Task CheckPersistentSessions(SessionDto login, SessionDto original,
        Func<SessionDto, HttpClient> signed, Func<HttpResponseMessage, HttpStatusCode, Task> expect)
    {
        var connection = Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION")
            ?? throw new Exception("Ejecuta Test-Posts.ps1 -Suite accounts.");
        if (new NpgsqlConnectionStringBuilder(connection).Database?.StartsWith("urbalink_posts_test_") != true)
            throw new Exception("Solo se permiten bases temporales.");
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(login.Token)));
        async Task<object?> Sql(string sql)
        {
            await using var command = new NpgsqlCommand(sql, db);
            command.Parameters.Add(new NpgsqlParameter { Value = hash });
            return await command.ExecuteScalarAsync();
        }
        // Existing, still-valid seven-day credentials upgrade without entering a password.
        await Sql("UPDATE sessions SET expires_at=now()+interval '7 days' WHERE token_hash=$1");
        using (var reopened = signed(login))
            await expect(await reopened.GetAsync("api/me"), HttpStatusCode.OK);
        var renewed = (DateTime)(await Sql("SELECT expires_at FROM sessions WHERE token_hash=$1"))!;
        if (renewed < DateTime.UtcNow.AddDays(179)) throw new Exception("La sesión antigua no se renovó.");
        using var device = signed(login);
        foreach (var response in await Task.WhenAll(device.GetAsync("api/me"), device.GetAsync("api/me")))
            await expect(response, HttpStatusCode.OK);
        if ((DateTime)(await Sql("SELECT expires_at FROM sessions WHERE token_hash=$1"))! != renewed)
            throw new Exception("Se escribe la sesión innecesariamente en cada petición.");

        await Sql("UPDATE sessions SET expires_at=now()-interval '1 second' WHERE token_hash=$1");
        await expect(await device.GetAsync("api/me"), HttpStatusCode.Unauthorized);
        if ((bool)(await Sql("SELECT expires_at>now() FROM sessions WHERE token_hash=$1"))!)
            throw new Exception("Se resucitó una sesión caducada.");

        // Revoke while authentication waits to renew. The UPDATE must not recreate access.
        await Sql("UPDATE sessions SET expires_at=now()+interval '7 days' WHERE token_hash=$1");
        await using (var transaction = await db.BeginTransactionAsync())
        {
            await Sql("SELECT token_hash FROM sessions WHERE token_hash=$1 FOR UPDATE");
            var pending = device.GetAsync("api/me");
            await Task.Delay(200);
            if (pending.IsCompleted) throw new Exception("La renovación no esperó el bloqueo de la sesión.");
            await Sql("DELETE FROM sessions WHERE token_hash=$1");
            await transaction.CommitAsync();
            await expect(await pending, HttpStatusCode.Unauthorized);
        }
        using var otherDevice = signed(original);
        await expect(await otherDevice.GetAsync("api/me"), HttpStatusCode.OK);
        await expect(await otherDevice.PostAsync("api/auth/logout", null), HttpStatusCode.NoContent);
        await expect(await otherDevice.GetAsync("api/me"), HttpStatusCode.Unauthorized);
        Console.WriteLine("OK: acceso persistente, renovación de sesiones antiguas, reutilización sin contraseña, caducidad y revocación concurrente por dispositivo.");
    }
}
