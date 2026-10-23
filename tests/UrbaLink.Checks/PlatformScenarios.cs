using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using UrbaLink.Contracts;

internal static class PlatformScenarios
{
    public static async Task Run(string url)
    {
        using var owner=new HttpClient{BaseAddress=new Uri(url)};
        using var member=new HttpClient{BaseAddress=new Uri(url)};
        using var stranger=new HttpClient{BaseAddress=new Uri(url)};
        using var platform=new HttpClient{BaseAddress=new Uri(url)};
        async Task<T> Post<T>(HttpClient c,string path,object body)
        {
            using var response=await c.PostAsJsonAsync(path,body);if(!response.IsSuccessStatusCode)throw new Exception($"{path}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        static void Check(bool test,string text){if(!test)throw new Exception(text);}
        async Task Expect(HttpResponseMessage r,HttpStatusCode code){using(r)if(r.StatusCode!=code)throw new Exception($"Esperado {code}, recibido {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");}
        var users=new List<SessionDto>();
        foreach(var client in new[]{owner,member,stranger})
        {
            var session=await Post<SessionDto>(client,"api/auth/register",new RegisterRequest("Vecino "+Guid.NewGuid().ToString("N"),$"{Guid.NewGuid():N}@example.test","Platform-test-password-123"));users.Add(session);client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
        }
        var registered=await Post<SessionDto>(platform,"api/auth/register",new RegisterRequest("Operador","operator.platform@example.test","Platform-test-password-123"));
        var fixedId=Guid.Parse("a7de3348-1f21-49c2-af1e-852d2d905541");
        await using var db=new NpgsqlConnection(Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION"));await db.OpenAsync();
        await using(var cmd=new NpgsqlCommand("DELETE FROM sessions WHERE user_id=$1;",db)){cmd.Parameters.AddWithValue(registered.Account.Id);await cmd.ExecuteNonQueryAsync();}
        await using(var cmd=new NpgsqlCommand("UPDATE users SET id=$1 WHERE id=$2",db)){cmd.Parameters.AddWithValue(fixedId);cmd.Parameters.AddWithValue(registered.Account.Id);await cmd.ExecuteNonQueryAsync();}
        var login=new{email=registered.Account.Email,password="Platform-test-password-123"};
        await Expect(await owner.GetAsync("api/platform/communities"),HttpStatusCode.Unauthorized);
        await Expect(await platform.PostAsJsonAsync("api/platform/session",login with{password="Incorrect-password-123"}),HttpStatusCode.Unauthorized);
        var auth=await Post<JsonElement>(platform,"api/platform/session",login);
        platform.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",auth.GetProperty("token").GetString());
        await Expect(await owner.PostAsJsonAsync("api/platform/session",new{email=users[0].Account.Email,password="Platform-test-password-123"}),HttpStatusCode.Unauthorized);
        var community=await Post<CommunityDto>(owner,"api/communities",new CreateCommunityRequest("Comunidad pendiente"));
        Check(community.State=="Pending","No exige aprobación");
        await Expect(await owner.GetAsync($"api/communities/{community.Id}/overview"),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync($"api/communities/{community.Id}/invitations",new{}),HttpStatusCode.Forbidden);
        var root=$"api/platform/communities/{community.Id}";
        await Expect(await owner.PostAsJsonAsync(root+"/state",new{state="Active",reason="Intento de escalada"}),HttpStatusCode.Unauthorized);
        await Expect(await platform.PostAsJsonAsync(root+"/state",new{state="Active",reason="Comunidad verificada"}),HttpStatusCode.NoContent);
        var invitation=await Post<InvitationDto>(owner,$"api/communities/{community.Id}/invitations",new{});
        await Post<CommunityDto>(member,"api/communities/join",new JoinRequest(invitation.Code));
        var list=await platform.GetFromJsonAsync<JsonElement>(root+"/users");Check(list.GetProperty("items").GetArrayLength()==2,"Miembros incorrectos");
        var posts=$"api/communities/{community.Id}/posts";
        var post=await Post<PostDto>(owner,posts,new CreatePostRequest("Contenido para revisar","<script>alert('xss')</script>","General"));
        var comment=await Post<CommentDto>(member,$"{posts}/{post.Id}/comments",new CreateCommentRequest("Comentario de prueba",Guid.NewGuid()));
        var reportInput=new ReportRequest(Guid.NewGuid(),community.Id,"Comment",comment.Id,"Contenido que requiere revisión");
        var reports=await Task.WhenAll(Post<ReportReceipt>(owner,"api/reports",reportInput),Post<ReportReceipt>(owner,"api/reports",reportInput));
        Check(reports[0].Id==reports[1].Id,"Denuncia duplicada");
        await Expect(await stranger.PostAsJsonAsync("api/reports",reportInput with{RequestId=Guid.NewGuid()}),HttpStatusCode.Forbidden);
        await Expect(await owner.GetAsync("api/platform/reports"),HttpStatusCode.Unauthorized);
        var report=await platform.GetFromJsonAsync<JsonElement>("api/platform/reports/"+reports[0].Id);
        Check(report.GetProperty("snapshot").GetProperty("body").GetString()==comment.Body,"Copia incorrecta");
        await Expect(await platform.PostAsJsonAsync(root+$"/content/Comment/{comment.Id}/remove",new{state="Withdrawn",reason="Retirada de prueba"}),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<CommentPage>($"{posts}/{post.Id}/comments"))!.Items.Length==0,"Comentario sigue visible");
        report=await platform.GetFromJsonAsync<JsonElement>("api/platform/reports/"+reports[0].Id);Check(report.GetProperty("snapshot").GetProperty("body").GetString()==comment.Body,"Se perdió la prueba tras retirada");
        await Expect(await platform.PostAsJsonAsync("api/platform/reports/"+reports[0].Id+"/resolve",new{state="Resolved",reason="Se retiró el comentario"}),HttpStatusCode.NoContent);
        var service=await Post<PostDto>(owner,posts,new CreatePostRequest("Servicio privado","Descripción pública","Ofrezco"));
        var chat=await Post<MarketConversationDto>(member,$"api/communities/{community.Id}/services/{service.Id}/conversations",new{});
        var messagePath=$"api/communities/{community.Id}/services/{service.Id}/conversations/{chat.Id}/messages";
        await Expect(await member.PostAsJsonAsync(messagePath,new MarketMessageRequest(Guid.NewGuid(),"Mensaje denunciado")),HttpStatusCode.NoContent);
        await Expect(await member.PostAsJsonAsync(messagePath,new MarketMessageRequest(Guid.NewGuid(),"Mensaje privado NO denunciado")),HttpStatusCode.NoContent);
        var messages=(await owner.GetFromJsonAsync<MarketMessagePage>(messagePath))!.Items;
        var message=messages.Single(m=>m.Body=="Mensaje denunciado");
        await Post<ReportReceipt>(owner,"api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Message",message.Id,"Mensaje inapropiado"));
                var strangerInvite=await Post<InvitationDto>(owner,$"api/communities/{community.Id}/invitations",new{});
        await Post<CommunityDto>(stranger,"api/communities/join",new JoinRequest(strangerInvite.Code));
        await Expect(await stranger.PostAsJsonAsync("api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Message",message.Id,"Intento de acceso al chat ajeno")),HttpStatusCode.NotFound);
        var content=await platform.GetStringAsync(root+"/content");Check(!content.Contains("Mensaje privado")&&!content.Contains("Mensaje denunciado"),"El panel filtra mensajes privados");
        await Expect(await platform.GetAsync(messagePath),HttpStatusCode.Unauthorized);
        await Expect(await platform.PostAsJsonAsync(root+$"/content/Message/{message.Id}/remove",new{state="Withdrawn",reason="No permitido"}),HttpStatusCode.BadRequest);
        var memberPath=root+"/users/"+users[1].Account.Id+"/state";
        await Expect(await platform.PostAsJsonAsync(memberPath,new{state="Blocked",reason="Bloqueo de prueba"}),HttpStatusCode.NoContent);
        await Expect(await member.GetAsync(posts),HttpStatusCode.Forbidden);
        await Expect(await member.GetAsync(messagePath),HttpStatusCode.Forbidden);
        var invite2=await Post<InvitationDto>(owner,$"api/communities/{community.Id}/invitations",new{});
        await Expect(await member.PostAsJsonAsync("api/communities/join",new JoinRequest(invite2.Code)),HttpStatusCode.Conflict);
        await Expect(await platform.PostAsJsonAsync(memberPath,new{state="Active",reason="Bloqueo revisado"}),HttpStatusCode.NoContent);
        await Expect(await member.GetAsync(messagePath),HttpStatusCode.OK);
        await Expect(await platform.PostAsJsonAsync(root+"/users/"+users[0].Account.Id+"/state",new{state="Blocked",reason="Último administrador"}),HttpStatusCode.Conflict);
        await Expect(await platform.PostAsJsonAsync(root+"/state",new{state="Suspended",reason="Revisión de comunidad"}),HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync(posts),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync($"api/communities/{community.Id}/restore",new{}),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync($"API/COMMUNITIES/{community.Id}/restore",new{}),HttpStatusCode.Forbidden);
        Check((await member.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==0,"Comunidad suspendida sigue notificando");
        await Expect(await platform.PostAsJsonAsync(root+"/state",new{state="Active",reason="Revisión completada"}),HttpStatusCode.NoContent);
        await Expect(await platform.PostAsJsonAsync("api/platform/users/"+users[1].Account.Id+"/state",new{state="Suspended",reason="Suspensión global de prueba"}),HttpStatusCode.NoContent);
        await Expect(await member.GetAsync("api/me"),HttpStatusCode.Unauthorized);
        await Expect(await platform.PostAsJsonAsync("api/platform/users/"+fixedId+"/state",new{state="Suspended",reason="Auto suspensión"}),HttpStatusCode.Conflict);
        var audit=await platform.GetStringAsync("api/platform/audit");Check(audit.Contains("ReadReport")&&audit.Contains("Member:Blocked"),"Auditoría ausente");
        using(var response=await owner.GetAsync("admin/")){Check(response.IsSuccessStatusCode,"Panel no servido");Check(response.Headers.Contains("Content-Security-Policy"),"Falta CSP");}
        await Expect(await platform.DeleteAsync("api/platform/session"),HttpStatusCode.NoContent);
        await Expect(await platform.GetAsync("api/platform/communities"),HttpStatusCode.Unauthorized);
        Console.WriteLine("OK: panel, contraseña y autorización global, aprobación, moderación, denuncias, privacidad, bloqueos, auditoría y cierre de sesión.");
    }
}

