using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class LifecycleScenarios
{
    public static async Task Run(string url)
    {
        using var admin = new HttpClient { BaseAddress = new Uri(url) };
        using var member = new HttpClient { BaseAddress = new Uri(url) };
        async Task<T> Post<T>(HttpClient c, string p, object data)
        {
            using var r = await c.PostAsJsonAsync(p,data); r.EnsureSuccessStatusCode();
            return (await r.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage r, HttpStatusCode status)
        {
            using (r) if (r.StatusCode != status) throw new Exception($"Expected {status}: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        }
        foreach (var c in new[] { admin, member })
        {
            var session = await Post<SessionDto>(c,"api/auth/register",new RegisterRequest("Prueba",$"{Guid.NewGuid():N}@example.test","Lifecycle-password-123"));
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",session.Token);
        }
        var first = await Post<CommunityDto>(admin,"api/communities",new CreateCommunityRequest("carambolo"));
        var other = await Post<CommunityDto>(admin,"api/communities",new CreateCommunityRequest("Carambolo"));
        var path = $"api/communities/{first.Id}";
        var invite = await Post<InvitationDto>(admin,path+"/invitations",new {});
        await Post<CommunityDto>(member,"api/communities/join",new JoinRequest(invite.Code));
        var unused = await Post<InvitationDto>(admin,path+"/invitations",new {});
        var post = await Post<PostDto>(member,path+"/posts",new CreatePostRequest("Objeto compartido","Texto","Material"));
        var comments = path+$"/posts/{post.Id}/comments";
        await Post<CommentDto>(member,comments,new CreateCommentRequest("Respuesta",Guid.NewGuid()));
        var overview = (await admin.GetFromJsonAsync<CommunityOverview>(path+"/overview"))!;
        if (overview.MemberCount != 2 || overview.SharedItems != 1) throw new Exception("Resumen incorrecto");
        await Expect(await member.PostAsJsonAsync(path+"/delete",new DeleteCommunityRequest("carambolo")),HttpStatusCode.Conflict);
        await Expect(await admin.PostAsJsonAsync(path+"/delete",new DeleteCommunityRequest("Carambolo")),HttpStatusCode.Conflict);
        await Expect(await admin.PostAsJsonAsync(path+"/delete",new DeleteCommunityRequest("carambolo")),HttpStatusCode.NoContent);
        var active = (await admin.GetFromJsonAsync<CommunityDto[]>("api/communities"))!;
        if (active.Length != 1 || active[0].Id != other.Id) throw new Exception("Comunidad equivocada eliminada");
        if ((await admin.GetFromJsonAsync<CommunityDto[]>("api/communities/trash"))!.Length != 1) throw new Exception("Papelera vacía");
        if ((await member.GetFromJsonAsync<CommunityDto[]>("api/communities/trash"))!.Length != 0) throw new Exception("Papelera accesible sin permisos");
        await Expect(await member.GetAsync(path+"/overview"),HttpStatusCode.Forbidden);
        await Expect(await member.GetAsync(path+"/posts"),HttpStatusCode.Forbidden);
        await Expect(await member.GetAsync(comments),HttpStatusCode.NotFound);
        await Expect(await admin.PostAsJsonAsync(path+"/invitations",new {}),HttpStatusCode.Forbidden);
        await Expect(await member.PostAsJsonAsync(path+"/restore",new {}),HttpStatusCode.NotFound);
        await Expect(await admin.PostAsJsonAsync(path+"/restore",new {}),HttpStatusCode.NoContent);
        await Expect(await member.GetAsync(comments),HttpStatusCode.OK);
        await Expect(await admin.PostAsJsonAsync("api/communities/join",new JoinRequest(unused.Code)),HttpStatusCode.BadRequest);
        Console.WriteLine("OK: resumen, permisos, nombre exacto, aislamiento, papelera, restauración e invitaciones invalidadas.");
    }
}
