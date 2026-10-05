using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class PostScenarios
{
    public static async Task Run(string url)
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(url) };
        var suffix = Guid.NewGuid().ToString("N");
        async Task<T> Post<T>(HttpClient client, string path, object value)
        {
            using var response = await client.PostAsJsonAsync(path, value);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        async Task<SessionDto> Register(string name) => await Post<SessionDto>(anonymous, "api/auth/register",
            new RegisterRequest(name, $"{name}.{suffix}@example.test", "Post-test-password-123"));
        HttpClient Signed(SessionDto session)
        {
            var client = new HttpClient { BaseAddress = new Uri(url) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            return client;
        }
        static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static async Task Expect(HttpResponseMessage response, HttpStatusCode status)
        {
            using (response)
                if (response.StatusCode != status)
                    throw new Exception($"Esperado {status}, recibido {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }
        var adminSession = await Register("Admin");
        var memberSession = await Register("Member");
        var outsiderSession = await Register("Outsider");
        using var admin = Signed(adminSession);
        using var member = Signed(memberSession);
        using var outsider = Signed(outsiderSession);
        var community = await Post<CommunityDto>(admin, "api/communities", new CreateCommunityRequest("Tablón test"));
        var other = await Post<CommunityDto>(outsider, "api/communities", new CreateCommunityRequest("Otra comunidad"));
        var invite = await Post<InvitationDto>(admin, $"api/communities/{community.Id}/invitations", new { });
        await Post<CommunityDto>(member, "api/communities/join", new JoinRequest(invite.Code));
        var path = $"api/communities/{community.Id}/posts";
        var otherPath = $"api/communities/{other.Id}/posts";

        await Expect(await anonymous.GetAsync(path), HttpStatusCode.Unauthorized);
        await Expect(await outsider.GetAsync(path), HttpStatusCode.Forbidden);
        await Expect(await outsider.PostAsJsonAsync(path, new CreatePostRequest("Prueba", "Texto", "General")), HttpStatusCode.Forbidden);
        await Expect(await member.PostAsJsonAsync(path, new CreatePostRequest("Aviso", "Texto", "Aviso oficial")), HttpStatusCode.Forbidden);
        await Expect(await member.PostAsJsonAsync(path, new CreatePostRequest(" ", "Texto", "General")), HttpStatusCode.BadRequest);
        await Expect(await member.PostAsJsonAsync(path, new CreatePostRequest("Título", " ", "General")), HttpStatusCode.BadRequest);
        await Expect(await member.PostAsJsonAsync(path, new CreatePostRequest("Título", new string('a',4001), "General")), HttpStatusCode.BadRequest);
        await Expect(await member.PostAsJsonAsync(path, new CreatePostRequest("Título", "Texto", "Inventada")), HttpStatusCode.BadRequest);
        await Expect(await member.GetAsync(path + "?before=-1"), HttpStatusCode.BadRequest);
        await Expect(await member.GetAsync(path + "?category=Inventada"), HttpStatusCode.BadRequest);

        var post = await Post<PostDto>(member, path, new
        {
            Title = "  Necesito un taladro  ", Body = "¿Me lo prestas el sábado?", Category = "Material",
            AuthorId = adminSession.Account.Id, CommunityId = other.Id
        });
        Assert(post.AuthorId == memberSession.Account.Id && post.CommunityId == community.Id, "Se aceptó autor o comunidad falsificados.");
        Assert(post.Title == "Necesito un taladro", "No se normalizó el título.");
        var memberPage = (await admin.GetFromJsonAsync<PostPage>(path))!;
        Assert(memberPage.Items.Single().Id == post.Id, "La publicación no aparece a otro miembro.");
        Assert((await outsider.GetFromJsonAsync<PostPage>(otherPath))!.Items.Length == 0, "Se filtró contenido entre comunidades.");
        await Expect(await outsider.DeleteAsync($"{path}/{post.Id}"), HttpStatusCode.Forbidden);
        await Expect(await outsider.DeleteAsync($"{otherPath}/{post.Id}"), HttpStatusCode.NotFound);
        var official = await Post<PostDto>(admin, path, new CreatePostRequest("Aviso de la junta", "Reunión vecinal", "Aviso oficial"));
        await Expect(await member.DeleteAsync($"{path}/{official.Id}"), HttpStatusCode.NotFound);

        var large = await Post<PostDto>(member, path, new CreatePostRequest("Texto Unicode", new string('ñ',4000), "General"));
        Assert(large.Body.Length == 4000, "No se admitió el límite de texto Unicode.");
        await Expect(await member.DeleteAsync($"{path}/{large.Id}"), HttpStatusCode.NoContent);
        await Expect(await admin.DeleteAsync($"{path}/{post.Id}"), HttpStatusCode.NoContent);
        var afterDelete = (await member.GetFromJsonAsync<PostPage>(path))!;
        Assert(afterDelete.Items.All(p => p.Id != post.Id && p.Id != large.Id), "Se mostraron publicaciones retiradas.");

        for (var i = 0; i < 22; i++)
            await Post<PostDto>(member, path, new CreatePostRequest($"Petición {i}", "Texto de prueba", "Ayuda"));
        var first = (await member.GetFromJsonAsync<PostPage>(path + "?category=Ayuda"))!;
        Assert(first.Items.Length == 20 && first.NextBefore.HasValue, "Primera página incorrecta.");
        var latest = await Post<PostDto>(admin, path, new CreatePostRequest("Nueva entre páginas", "Texto", "Ayuda"));
        var second = (await member.GetFromJsonAsync<PostPage>(path + $"?category=Ayuda&before={first.NextBefore}"))!;
        Assert(second.Items.Length == 2 && second.NextBefore is null, "Segunda página incorrecta.");
        Assert(!first.Items.Select(p => p.Id).Intersect(second.Items.Select(p => p.Id)).Any(), "Paginación con duplicados.");
        Assert(second.Items.All(p => p.Id < first.Items.Last().Id && p.Id != latest.Id), "El cursor no conserva el orden.");
        var notices = (await member.GetFromJsonAsync<PostPage>(path + "?category=" + Uri.EscapeDataString("Aviso oficial")))!;
        Assert(notices.Items.Length == 1 && notices.Items[0].Id == official.Id, "Filtro de avisos incorrecto.");
        await Expect(await member.GetAsync("api/communities/" + Guid.NewGuid() + "/posts"), HttpStatusCode.Forbidden);
        Console.WriteLine("OK: tablón, aislamiento, roles, autoría, validación, Unicode, retirada y paginación estable.");
    }
}
