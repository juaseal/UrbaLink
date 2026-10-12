using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class ServiceScenarios
{
    public static async Task Run(string url)
    {
        using var admin = new HttpClient { BaseAddress = new Uri(url) };
        using var owner = new HttpClient { BaseAddress = new Uri(url) };
        using var alice = new HttpClient { BaseAddress = new Uri(url) };
        using var bob = new HttpClient { BaseAddress = new Uri(url) };
        using var outsider = new HttpClient { BaseAddress = new Uri(url) };
        async Task<T> Post<T>(HttpClient client, string path, object data)
        {
            using var response = await client.PostAsJsonAsync(path, data);
            if (!response.IsSuccessStatusCode) throw new Exception($"{path}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage response, HttpStatusCode code)
        { using (response) if (response.StatusCode != code) throw new Exception($"Esperado {code}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}"); }
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        foreach (var client in new[] { admin, owner, alice, bob, outsider })
        {
            var session = await Post<SessionDto>(client, "api/auth/register", new RegisterRequest("Vecino", $"{Guid.NewGuid():N}@example.test", "Service-test-password-123"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        }
        var community = await Post<CommunityDto>(admin, "api/communities", new CreateCommunityRequest("Servicios test"));
        foreach (var client in new[] { owner, alice, bob })
        {
            var invite = await Post<InvitationDto>(admin, $"api/communities/{community.Id}/invitations", new { });
            await Post<CommunityDto>(client, "api/communities/join", new JoinRequest(invite.Code));
        }
        var posts = $"api/communities/{community.Id}/posts";
        var service = await Post<PostDto>(owner, posts, new CreatePostRequest("Clases de matemáticas", "ESO y bachillerato", "Ofrezco"));
        var general = await Post<PostDto>(owner, posts, new CreatePostRequest("Noticias vecinales", "Hola", "General"));
        var recommendation = await Post<PostDto>(alice, posts, new CreatePostRequest("Buen fontanero", "Mi experiencia", "Recomendación"));
        await Post<PostDto>(bob, posts, new CreatePostRequest("Busco una gestoría", "Necesito ayuda", "Ayuda"));
        var group = (await alice.GetFromJsonAsync<PostPage>(posts + "?category=" + Uri.EscapeDataString(PostCategories.HelpGroup)))!;
        Check(group.Items.Length == 3 && group.Items.All(p => PostCategories.Help.Contains(p.Category)), "La sección mezcla categorías ajenas");
        var filtered = (await alice.GetFromJsonAsync<PostPage>(posts + "?category=" + Uri.EscapeDataString("Recomendación")))!;
        Check(filtered.Items.Length == 1 && filtered.Items[0].Id == recommendation.Id, "Filtro de recomendaciones incorrecto");
        await Post<CommentDto>(alice, $"{posts}/{recommendation.Id}/comments", new CreateCommentRequest("Gracias", Guid.NewGuid()));
        await Expect(await alice.GetAsync($"{posts}/{service.Id}/comments"), HttpStatusCode.NotFound);
        await Expect(await alice.PostAsJsonAsync($"{posts}/{service.Id}/comments", new CreateCommentRequest("No público", Guid.NewGuid())), HttpStatusCode.NotFound);
        var root = $"api/communities/{community.Id}/services/{service.Id}";
        await Expect(await outsider.GetAsync(root), HttpStatusCode.Forbidden);
        await Expect(await outsider.PostAsJsonAsync(root + "/conversations", new { }), HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync(root + "/conversations", new { }), HttpStatusCode.BadRequest);
        await Expect(await alice.GetAsync($"api/communities/{community.Id}/services/{general.Id}"), HttpStatusCode.NotFound);
        var twins = await Task.WhenAll(Post<MarketConversationDto>(alice, root + "/conversations", new { }), Post<MarketConversationDto>(alice, root + "/conversations", new { }));
        Check(twins[0].Id == twins[1].Id, "Se duplicó la conversación");
        var chat = twins[0];
        var other = await Post<MarketConversationDto>(bob, root + "/conversations", new { });
        Check(chat.Id != other.Id, "Se mezclaron interesados");
        var messages = $"{root}/conversations/{chat.Id}/messages";
        var message = new MarketMessageRequest(Guid.NewGuid(), "Consulta privada de Alicia");
        await Expect(await alice.PostAsJsonAsync(messages, message), HttpStatusCode.NoContent);
        await Expect(await alice.PostAsJsonAsync(messages, message), HttpStatusCode.NoContent);
        await Expect(await alice.PostAsJsonAsync(messages, message with { Body = "Otro texto" }), HttpStatusCode.Conflict);
        var history = (await owner.GetFromJsonAsync<MarketMessagePage>(messages))!;
        Check(history.Items.Length == 1, "Mensaje duplicado");
        var first = history.Items[0].Id;
        foreach (var client in new[] { admin, bob })
        {
            await Expect(await client.GetAsync(messages), HttpStatusCode.NotFound);
            await Expect(await client.PostAsJsonAsync(messages, new MarketMessageRequest(Guid.NewGuid(), "Intrusión")), HttpStatusCode.NotFound);
            await Expect(await client.PostAsJsonAsync(messages + "/read", new MessageReadRequest(first)), HttpStatusCode.NotFound);
        }
        Check((await owner.GetFromJsonAsync<ServiceDetail>(root))!.Conversations.Length == 2, "El propietario no ve sus consultas");
        Check((await alice.GetFromJsonAsync<ServiceDetail>(root))!.Conversations.Single().Id == chat.Id, "Alicia ve conversaciones ajenas");
        Check((await admin.GetFromJsonAsync<ServiceDetail>(root))!.Conversations.Length == 0, "El administrador ve conversaciones ajenas");
        var inbox = (await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!;
        Check(inbox.UnreadCount == 1 && inbox.Items.Single().Kind == "Service" && inbox.Items[0].ItemId == service.Id, "Notificación de servicio incorrecta");
        Check((await admin.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount == 0, "Notificación filtrada al administrador");
        await Expect(await owner.PostAsJsonAsync(messages, new MarketMessageRequest(Guid.NewGuid(), "Respuesta privada")), HttpStatusCode.NoContent);
        Check((await alice.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount == 1, "No se notificó la respuesta");
        await Expect(await alice.PostAsJsonAsync(messages, new MarketMessageRequest(Guid.NewGuid(), "Nuevo mensaje")), HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync(messages + "/read", new MessageReadRequest(first)), HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount == 1, "La lectura consumió mensajes posteriores");
        var last = (await owner.GetFromJsonAsync<MarketMessagePage>(messages))!.Items.Max(m => m.Id);
        await Expect(await owner.PostAsJsonAsync(messages + "/read", new MessageReadRequest(last)), HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync(messages + "/read", new MessageReadRequest(first)), HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount == 0, "La lectura retrocedió");
        var otherMessages = $"{root}/conversations/{other.Id}/messages";
        await Expect(await bob.PostAsJsonAsync(otherMessages + "/read", new MessageReadRequest(first)), HttpStatusCode.BadRequest);
        for (var i = 0; i < 20; i++) await Expect(await alice.PostAsJsonAsync(messages, new MarketMessageRequest(Guid.NewGuid(), "Mensaje " + i)), HttpStatusCode.NoContent);
        var page1 = (await owner.GetFromJsonAsync<MarketMessagePage>(messages))!;
        var page2 = (await owner.GetFromJsonAsync<MarketMessagePage>(messages + "?before=" + page1.NextBefore))!;
        Check(page1.Items.Length == 20 && page2.Items.Length == 3 && !page1.Items.Select(m => m.Id).Intersect(page2.Items.Select(m => m.Id)).Any(), "Paginación incorrecta");
        await Expect(await owner.DeleteAsync($"{posts}/{service.Id}"), HttpStatusCode.NoContent);
        await Expect(await alice.GetAsync(messages), HttpStatusCode.NotFound);
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount == 0, "Servicio retirado sigue notificando");
        await Expect(await admin.PostAsJsonAsync($"api/communities/{community.Id}/delete", new DeleteCommunityRequest(community.Name)), HttpStatusCode.NoContent);
        await Expect(await alice.GetAsync(root), HttpStatusCode.Forbidden);
        Console.WriteLine("OK: filtros, recomendaciones, privacidad de servicios, aislamiento entre interesados y administradores, reintentos, notificaciones, lectura, paginación y retirada.");
    }
}
