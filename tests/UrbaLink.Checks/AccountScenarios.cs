using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        await Expect(await member.PostAsync("api/auth/logout", null), HttpStatusCode.NoContent);
        await Expect(await member.GetAsync("api/me"), HttpStatusCode.Unauthorized);
        Console.WriteLine("OK: registro, login, normalización, aislamiento, permisos, invitaciones atómicas y revocación.");
    }
}
