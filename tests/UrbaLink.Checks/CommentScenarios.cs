using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class CommentScenarios
{
    public static async Task Run(string url)
    {
        using var anonymous = new HttpClient { BaseAddress = new Uri(url) };
        var suffix = Guid.NewGuid().ToString("N");
        async Task<T> Post<T>(HttpClient client, string path, object data)
        {
            using var response = await client.PostAsJsonAsync(path, data);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        async Task<SessionDto> Register(string name) => await Post<SessionDto>(anonymous, "api/auth/register",
            new RegisterRequest(name, $"{name}.{suffix}@example.test", "Comment-test-password-123"));
        HttpClient Signed(SessionDto session)
        {
            var client = new HttpClient { BaseAddress = new Uri(url) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            return client;
        }
        static void Check(bool result, string message) { if (!result) throw new Exception(message); }
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
        var community = await Post<CommunityDto>(admin, "api/communities", new CreateCommunityRequest("Comentarios test"));
        var other = await Post<CommunityDto>(outsider, "api/communities", new CreateCommunityRequest("Comunidad ajena"));
        var invite = await Post<InvitationDto>(admin, $"api/communities/{community.Id}/invitations", new { });
        await Post<CommunityDto>(member, "api/communities/join", new JoinRequest(invite.Code));
        var postPath = $"api/communities/{community.Id}/posts";
        var post = await Post<PostDto>(member, postPath, new CreatePostRequest("Publicación con respuestas", "Texto", "General"));
        var second = await Post<PostDto>(admin, postPath, new CreatePostRequest("Otra publicación", "Texto", "General"));
        var path = $"{postPath}/{post.Id}/comments";
        var secondPath = $"{postPath}/{second.Id}/comments";
        var wrongPath = $"api/communities/{other.Id}/posts/{post.Id}/comments";
        var request = new CreateCommentRequest("Respuesta de prueba", Guid.NewGuid());

        await Expect(await anonymous.GetAsync(path), HttpStatusCode.Unauthorized);
        await Expect(await outsider.GetAsync(path), HttpStatusCode.NotFound);
        await Expect(await outsider.GetAsync(wrongPath), HttpStatusCode.NotFound);
        await Expect(await outsider.PostAsJsonAsync(path, request), HttpStatusCode.NotFound);
        await Expect(await outsider.PostAsJsonAsync(wrongPath, request), HttpStatusCode.NotFound);
        await Expect(await member.GetAsync(path + "?before=0"), HttpStatusCode.BadRequest);
        await Expect(await member.PostAsJsonAsync(path, new CreateCommentRequest(" ", Guid.NewGuid())), HttpStatusCode.BadRequest);
        await Expect(await member.PostAsJsonAsync(path, new CreateCommentRequest(new string('a',2001), Guid.NewGuid())), HttpStatusCode.BadRequest);
        await Expect(await member.PostAsJsonAsync(path, new CreateCommentRequest("Texto", Guid.Empty)), HttpStatusCode.BadRequest);
        var first = await Post<CommentDto>(member, path, new
        {
            Body = "  Respuesta de prueba  ", RequestId = request.RequestId,
            AuthorId = adminSession.Account.Id, PostId = second.Id
        });
        Check(first.AuthorId == memberSession.Account.Id && first.PostId == post.Id && first.Body == request.Body, "Autoría, publicación o normalización incorrectas.");
        var races = await Task.WhenAll(Post<CommentDto>(member,path,request),Post<CommentDto>(member,path,request));
        Check(races.All(c => c.Id == first.Id), "Se duplicó un envío repetido.");
        var raceRequest = new CreateCommentRequest("Envío simultáneo", Guid.NewGuid());
        var simultaneous = await Task.WhenAll(Post<CommentDto>(member,path,raceRequest),Post<CommentDto>(member,path,raceRequest));
        Check(simultaneous[0].Id == simultaneous[1].Id, "Se duplicó un envío concurrente nuevo.");
        var page = (await admin.GetFromJsonAsync<CommentPage>(path))!;
        Check(page.Items.Length == 2 && page.Post.Id == post.Id, "Comentarios incorrectos.");
        await Expect(await member.PostAsJsonAsync(path, request with { Body = "Texto distinto" }), HttpStatusCode.Conflict);
        await Expect(await outsider.DeleteAsync($"{path}/{first.Id}"), HttpStatusCode.NotFound);
        await Expect(await admin.DeleteAsync($"{secondPath}/{first.Id}"), HttpStatusCode.NotFound);

        var adminComment = await Post<CommentDto>(admin, path, new CreateCommentRequest("Mensaje de administración", Guid.NewGuid()));
        await Expect(await member.DeleteAsync($"{path}/{adminComment.Id}"), HttpStatusCode.NotFound);
        await Expect(await member.DeleteAsync($"{path}/{first.Id}"), HttpStatusCode.NoContent);
        await Expect(await member.PostAsJsonAsync(path, request), HttpStatusCode.Conflict);
        await Expect(await admin.DeleteAsync($"{path}/{simultaneous[0].Id}"), HttpStatusCode.NoContent);
        var unicode = await Post<CommentDto>(member, path, new CreateCommentRequest(new string('ñ',2000), Guid.NewGuid()));
        Check(unicode.Body.Length == 2000, "Falló el límite Unicode.");
        for (var i = 0; i < 21; i++)
            await Post<CommentDto>(member, path, new CreateCommentRequest($"Comentario {i}", Guid.NewGuid()));
        var firstPage = (await member.GetFromJsonAsync<CommentPage>(path))!;
        Check(firstPage.Items.Length == 20 && firstPage.NextBefore.HasValue, "Primera página incorrecta.");
        var latest = await Post<CommentDto>(admin, path, new CreateCommentRequest("Nuevo entre páginas", Guid.NewGuid()));
        var lastPage = (await member.GetFromJsonAsync<CommentPage>(path + $"?before={firstPage.NextBefore}"))!;
        Check(lastPage.Items.Length == 3 && lastPage.NextBefore is null, "Segunda página incorrecta.");
        Check(!firstPage.Items.Select(c=>c.Id).Intersect(lastPage.Items.Select(c=>c.Id)).Any() &&
            lastPage.Items.All(c=>c.Id < firstPage.Items.Last().Id && c.Id != latest.Id), "Cursor inestable.");
        Check(firstPage.Items.Concat(lastPage.Items).All(c=>c.Id != first.Id && c.Id != simultaneous[0].Id), "Se mostró un comentario retirado.");
        await Expect(await member.DeleteAsync($"{postPath}/{post.Id}"), HttpStatusCode.NoContent);
        await Expect(await admin.GetAsync(path), HttpStatusCode.NotFound);
        await Expect(await member.PostAsJsonAsync(path, new CreateCommentRequest("No debe publicarse",Guid.NewGuid())), HttpStatusCode.NotFound);
        await Expect(await admin.DeleteAsync($"{path}/{adminComment.Id}"), HttpStatusCode.NotFound);
        await Post<CommentDto>(member, secondPath, new CreateCommentRequest("Comentario persistente",Guid.NewGuid()));
        Console.WriteLine("OK: comentarios, permisos, autoría, validación, reintentos concurrentes, paginación y publicación retirada.");
    }
}
