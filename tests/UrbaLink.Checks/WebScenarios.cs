using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using UrbaLink.Contracts;

internal static class WebScenarios
{
    public static async Task Run(string url)
    {
        using var ownerHandler=new HttpClientHandler{CookieContainer=new CookieContainer()};
        using var owner=new HttpClient(ownerHandler){BaseAddress=new Uri(url)};
        using var member=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new Uri(url)};
        using var stranger=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new Uri(url)};
        using var anonymous=new HttpClient{BaseAddress=new Uri(url)};
        using var android=new HttpClient{BaseAddress=new Uri(url)};
        async Task<T> Post<T>(HttpClient c,string path,object body){using var r=await c.PostAsJsonAsync(path,body);if(!r.IsSuccessStatusCode)throw new Exception($"{path}: {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");return (await r.Content.ReadFromJsonAsync<T>())!;}
        async Task Expect(HttpResponseMessage r,HttpStatusCode code){using(r)if(r.StatusCode!=code)throw new Exception($"Esperado {code}, recibido {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");}
        static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
        await Expect(await anonymous.PostAsJsonAsync("api/web/register",new RegisterRequest("Intruso","intruso@example.test","Web-test-password-123")),HttpStatusCode.Forbidden);
        foreach(var client in new[]{owner,member,stranger})client.DefaultRequestHeaders.Add("X-UrbaLink-Web","1");
        var register=new RegisterRequest("Alba","vecino.web@example.test","Web-test-password-123");
        AccountDto account;
        using(var r=await owner.PostAsJsonAsync("api/web/register",register))
        {
            Check(r.IsSuccessStatusCode,"No permite registro web");
            var raw=await r.Content.ReadAsStringAsync();Check(!raw.Contains("token",StringComparison.OrdinalIgnoreCase),"La respuesta web expone el token");
            account=(await r.Content.ReadFromJsonAsync<AccountDto>())!;
            var cookie=r.Headers.GetValues("Set-Cookie").Single();Check(cookie.Contains("httponly",StringComparison.OrdinalIgnoreCase)&&cookie.Contains("samesite=strict",StringComparison.OrdinalIgnoreCase)&&cookie.Contains("path=/api"),"Cookie no protegida");
        }
        await Post<AccountDto>(member,"api/web/register",new RegisterRequest("Marcos","marcos.web@example.test","Web-test-password-123"));
        await Post<AccountDto>(stranger,"api/web/register",new RegisterRequest("Lucía","lucia.web@example.test","Web-test-password-123"));
        Check((await owner.GetFromJsonAsync<AccountDto>("api/me"))!.Id==account.Id,"La cookie no autentica");
        var originalCookie=ownerHandler.CookieContainer.GetCookies(new Uri(url+"/api"))["UrbaLink.WebSession"]!.Value;
        using var noHeader=new HttpClient(new HttpClientHandler{UseCookies=false}){BaseAddress=new Uri(url)};
        noHeader.DefaultRequestHeaders.Add("Cookie","UrbaLink.WebSession="+originalCookie);
        await Expect(await noHeader.GetAsync("api/me"),HttpStatusCode.Unauthorized);
        await Expect(await noHeader.PostAsJsonAsync("api/auth/logout",new{}),HttpStatusCode.Unauthorized);
        owner.DefaultRequestHeaders.Add("Origin","https://foreign.example.test");
        await Expect(await owner.GetAsync("api/me"),HttpStatusCode.Unauthorized);
        await Expect(await owner.PostAsJsonAsync("api/web/session",new LoginRequest(register.Email,register.Password)),HttpStatusCode.Forbidden);
        owner.DefaultRequestHeaders.Remove("Origin");
        await Expect(await owner.PostAsJsonAsync("api/web/session",new LoginRequest(register.Email,"Wrong-password-123")),HttpStatusCode.Unauthorized);
        Check((await owner.GetFromJsonAsync<AccountDto>("api/me"))!.Id==account.Id,"Un error de acceso perdió la sesión existente");
        var native=await Post<SessionDto>(android,"api/auth/login",new LoginRequest(register.Email,register.Password));android.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",native.Token);
        Check((await android.GetFromJsonAsync<AccountDto>("api/me"))!.Id==account.Id,"Se rompió el acceso Android");
        var c=await Post<CommunityDto>(owner,"api/communities",new CreateCommunityRequest("Los Olivos"));
        foreach(var client in new[]{member,stranger}){var invite=await Post<InvitationDto>(owner,$"api/communities/{c.Id}/invitations",new{});await Post<CommunityDto>(client,"api/communities/join",new JoinRequest(invite.Code));}
        var posts=$"api/communities/{c.Id}/posts";
        var post=await Post<PostDto>(owner,posts,new CreatePostRequest("¿Conocéis un buen jardinero?","Busco ayuda para poner a punto las plantas de la terraza. ¿Tenéis alguna recomendación?","General"));
        var input=new CreateCommentRequest("Yo puedo recomendarte a quien cuida nuestro jardín.",Guid.NewGuid());
        await Post<CommentDto>(member,$"{posts}/{post.Id}/comments",input);await Post<CommentDto>(member,$"{posts}/{post.Id}/comments",input);
        Check((await owner.GetFromJsonAsync<CommentPage>($"{posts}/{post.Id}/comments"))!.Items.Length==1,"Reintento web duplica comentario");
        var service=await Post<PostDto>(owner,posts,new CreatePostRequest("Clases de inglés para vecinos","Conversación y apoyo escolar por las tardes. Escríbeme para concretar.","Ofrezco"));
        var path=$"api/communities/{c.Id}/services/{service.Id}";
        var chat=await Post<MarketConversationDto>(member,path+"/conversations",new{});var messages=path+$"/conversations/{chat.Id}/messages";
        var message=new MarketMessageRequest(Guid.NewGuid(),"Hola Alba, ¿tienes disponibilidad los martes?");
        await Expect(await member.PostAsJsonAsync(messages,message),HttpStatusCode.NoContent);await Expect(await member.PostAsJsonAsync(messages,message),HttpStatusCode.NoContent);
        var inbox=(await owner.GetFromJsonAsync<ConversationInbox>("api/conversations"))!;
        Check(inbox.Items.Single().Id==chat.Id&&inbox.Items[0].UnreadCount==1&&inbox.Items[0].Kind=="Service","Bandeja web incorrecta");
        Check((await stranger.GetFromJsonAsync<ConversationInbox>("api/conversations"))!.Items.Length==0,"La bandeja filtra conversaciones ajenas");
        await Expect(await stranger.GetAsync(messages),HttpStatusCode.NotFound);
        var history=(await owner.GetFromJsonAsync<MarketMessagePage>(messages))!;
        Check(history.Items.Length==1,"Reintento web duplica mensaje");
        var unread=(await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!;
        Check(unread.UnreadCount==1&&unread.LatestMessageId==history.Items[0].Id,"El aviso no identifica el mensaje nuevo");
        Check((await stranger.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.LatestMessageId==0,"El aviso expone actividad ajena");
        await Expect(await owner.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items[0].Id)),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<ConversationInbox>("api/conversations"))!.Items.Single().UnreadCount==0,"Se pierde el chat tras leerlo");
        Check((await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==0,"La lectura no actualiza la campana");
        await Expect(await owner.GetAsync("api/platform/communities"),HttpStatusCode.Unauthorized);
        await using(var db=new NpgsqlConnection(Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION")))
        {
            await db.OpenAsync();await using var q=new NpgsqlCommand("UPDATE sessions SET expires_at=now()+interval '1 hour' WHERE token_hash=$1",db);q.Parameters.AddWithValue(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(originalCookie))));await q.ExecuteNonQueryAsync();
        }
        using(var response=await owner.GetAsync("api/me")){Check(response.IsSuccessStatusCode&&response.Headers.Contains("Set-Cookie"),"La sesión renovada no prolonga su cookie");}
        await Expect(await owner.PostAsJsonAsync("api/auth/logout",new{}),HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync("api/me"),HttpStatusCode.Unauthorized);
        noHeader.DefaultRequestHeaders.Add("X-UrbaLink-Web","1");await Expect(await noHeader.GetAsync("api/me"),HttpStatusCode.Unauthorized);
        Check((await android.GetFromJsonAsync<AccountDto>("api/me"))!.Id==account.Id,"Cerrar la web revocó la sesión Android");
        await Post<AccountDto>(owner,"api/web/session",new LoginRequest(register.Email,register.Password));
        Check((await owner.GetFromJsonAsync<AccountDto>("api/me"))!.Id==account.Id,"No permite volver a entrar por web");
        using(var response=await anonymous.GetAsync("")){Check(response.IsSuccessStatusCode,"La web no está en la raíz");Check(response.Headers.Contains("Content-Security-Policy"),"La web no tiene CSP");Check((await response.Content.ReadAsStringAsync()).Contains("/assets/app.js"),"No entrega la web de vecinos");}
        await Expect(await anonymous.GetAsync("admin/"),HttpStatusCode.OK);
        await Expect(await anonymous.GetAsync("assets/app.js"),HttpStatusCode.OK);
        Console.WriteLine("OK: web, cookie HttpOnly, origen y CSRF, cuentas compartidas, comentarios, mensajes privados, lectura, renovación, cierre de sesión y panel admin.");
    }
}
