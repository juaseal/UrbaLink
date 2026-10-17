using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class AnimalScenarios
{
    public static async Task Run(string url)
    {
        using var admin=new HttpClient{BaseAddress=new Uri(url)};
        using var owner=new HttpClient{BaseAddress=new Uri(url)};
        using var finder=new HttpClient{BaseAddress=new Uri(url)};
        using var stranger=new HttpClient{BaseAddress=new Uri(url)};
        async Task<T> Post<T>(HttpClient c,string path,object data)
        {
            using var r=await c.PostAsJsonAsync(path,data);
            if(!r.IsSuccessStatusCode)throw new Exception($"{path}: {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
            return (await r.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage r,HttpStatusCode code)
        {using(r)if(r.StatusCode!=code)throw new Exception($"Esperado {code}, recibido {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");}
        static void Check(bool value,string text){if(!value)throw new Exception(text);}
        foreach(var c in new[]{admin,owner,finder,stranger})
        {
            var session=await Post<SessionDto>(c,"api/auth/register",new RegisterRequest("Vecino",$"{Guid.NewGuid():N}@example.test","Animals-test-password-123"));
            c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
        }
        var community=await Post<CommunityDto>(admin,"api/communities",new CreateCommunityRequest("Animales test"));
        foreach(var c in new[]{owner,finder})
        {
            var invitation=await Post<InvitationDto>(admin,$"api/communities/{community.Id}/invitations",new{});
            await Post<CommunityDto>(c,"api/communities/join",new JoinRequest(invitation.Code));
        }
        var root=$"api/communities/{community.Id}/animals";
        var input=new AnimalNoticeInput(Guid.NewGuid(),"Lost","Dog","Luna","Entrada norte",DateTimeOffset.UtcNow.AddHours(-1),"Collar rojo",false,null);
        await Expect(await stranger.GetAsync(root),HttpStatusCode.Forbidden);
        await Expect(await stranger.PostAsJsonAsync(root,input),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync(root,input with{Species="Bird"}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root,input with{Photo=new byte[]{1,2,3}}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root,input with{Location=""}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root,input with{OccurredAt=DateTimeOffset.UtcNow.AddDays(1)}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root,input with{IsWithReporter=true}),HttpStatusCode.BadRequest);
        var twins=await Task.WhenAll(Post<AnimalNoticeDto>(owner,root,input),Post<AnimalNoticeDto>(owner,root,input));
        var notice=twins[0];Check(notice.Id==twins[1].Id,"Aviso duplicado al reintentar");
        await Expect(await owner.PostAsJsonAsync(root,input with{Name="Otro"}),HttpStatusCode.Conflict);
        var path=root+"/"+notice.Id;
        Check((await finder.GetFromJsonAsync<AnimalNoticePage>(root))!.Items.Single().Species=="Dog","Aviso no visible");
        await Expect(await stranger.GetAsync(path),HttpStatusCode.Forbidden);
        var other=await Post<CommunityDto>(stranger,"api/communities",new CreateCommunityRequest("Otra comunidad"));
        await Expect(await stranger.GetAsync($"api/communities/{other.Id}/animals/{notice.Id}"),HttpStatusCode.NotFound);
        await Expect(await finder.PutAsJsonAsync(path+"?version=1",input),HttpStatusCode.Forbidden);
        await Expect(await admin.PutAsJsonAsync(path+"?version=1",input),HttpStatusCode.Forbidden);
        await Expect(await owner.PutAsJsonAsync(path+"?version=1",input with{Description="Collar rojo, tímida"}),HttpStatusCode.NoContent);
        await Expect(await owner.PutAsJsonAsync(path+"?version=1",input),HttpStatusCode.Conflict);
        var chats=await Task.WhenAll(Post<MarketConversationDto>(finder,path+"/conversations",new{}),Post<MarketConversationDto>(finder,path+"/conversations",new{}));
        Check(chats[0].Id==chats[1].Id,"Chat duplicado");
        var messages=path+$"/conversations/{chats[0].Id}/messages";
        var message=new MarketMessageRequest(Guid.NewGuid(),"He visto a Luna cerca de la entrada");
        await Expect(await finder.PostAsJsonAsync(messages,message),HttpStatusCode.NoContent);
        await Expect(await finder.PostAsJsonAsync(messages,message),HttpStatusCode.NoContent);
        var history=(await owner.GetFromJsonAsync<MarketMessagePage>(messages))!;
        Check(history.Items.Length==1,"Mensaje duplicado");
        await Expect(await admin.GetAsync(messages),HttpStatusCode.NotFound);
        await Expect(await admin.PostAsJsonAsync(messages,message),HttpStatusCode.NotFound);
        await Expect(await admin.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items[0].Id)),HttpStatusCode.NotFound);
        Check((await admin.GetFromJsonAsync<AnimalNoticeDetail>(path))!.Conversations.Length==0,"El administrador ve chats ajenos");
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.Items.Single().Kind=="Animal","No hay aviso en campana");
        await Expect(await owner.PostAsJsonAsync(messages,new MarketMessageRequest(Guid.NewGuid(),"Voy a buscarla, gracias")),HttpStatusCode.NoContent);
        Check((await finder.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==1,"Sin notificación de respuesta");
        await Expect(await owner.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items[0].Id)),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==0,"Lectura no contabilizada");
        await Expect(await finder.PostAsJsonAsync(path+"/close",new CloseAnimalNoticeRequest("Recovered")),HttpStatusCode.Forbidden);
        await Expect(await admin.PostAsJsonAsync(path+"/close",new CloseAnimalNoticeRequest("Recovered")),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync(path+"/close",new CloseAnimalNoticeRequest("Recovered")),HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync(path+"/close",new CloseAnimalNoticeRequest("Recovered")),HttpStatusCode.NoContent);
        Check((await finder.GetFromJsonAsync<AnimalNoticePage>(root))!.Items.Length==0,"Aviso cerrado sigue activo");
        var closed=(await finder.GetFromJsonAsync<AnimalNoticePage>(root+"?closed=true"))!.Items.Single();
        Check(closed.Status=="Recovered"&&closed.ClosedAt is not null,"No conserva recuperación");
        Check((await owner.GetFromJsonAsync<MarketMessagePage>(messages))!.Items.Length==2,"Se perdió el historial");
        await Expect(await finder.PostAsJsonAsync(path+"/conversations",new{}),HttpStatusCode.Conflict);
        await Expect(await owner.PutAsJsonAsync(path+"?version=3",input),HttpStatusCode.Conflict);
        var photo=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","object.jpg"));
        var found=await Post<AnimalNoticeDto>(finder,root,input with{RequestId=Guid.NewGuid(),Kind="Found",Species="Dog",Name="",IsWithReporter=true,Photo=photo});
        Check(found.IsWithReporter && found.Photo!.SequenceEqual(photo),"No guarda situación o foto del animal");
        await Expect(await stranger.GetAsync(root+"/"+found.Id),HttpStatusCode.Forbidden);
        await Expect(await admin.PostAsJsonAsync(root+$"/{found.Id}/close",new CloseAnimalNoticeRequest("Withdrawn")),HttpStatusCode.NoContent);
        var cat=await Post<AnimalNoticeDto>(finder,root,input with{RequestId=Guid.NewGuid(),Kind="Found",Species="Cat",Name=""});
        await Expect(await finder.PostAsJsonAsync(root+$"/{cat.Id}/close",new CloseAnimalNoticeRequest("Withdrawn")),HttpStatusCode.NoContent);
        for(var i=0;i<21;i++)await Post<AnimalNoticeDto>(owner,root,input with{RequestId=Guid.NewGuid()});
        var page1=(await owner.GetFromJsonAsync<AnimalNoticePage>(root))!;
        var page2=(await owner.GetFromJsonAsync<AnimalNoticePage>(root+"?before="+page1.NextBefore))!;
        Check(page1.Items.Length==20 && page2.Items.Length==1 && !page1.Items.Select(x=>x.Id).Intersect(page2.Items.Select(x=>x.Id)).Any(),"Paginación incorrecta");
        await Expect(await admin.PostAsJsonAsync($"api/communities/{community.Id}/delete",new DeleteCommunityRequest(community.Name)),HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync(root),HttpStatusCode.Forbidden);
        await Expect(await finder.GetAsync(messages),HttpStatusCode.Forbidden);
        Check((await finder.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==0,"Comunidad archivada sigue notificando");
        Console.WriteLine("OK: animales, reintentos concurrentes, permisos, privacidad, mensajes, campana, recuperación, retirada y paginación.");
    }
}

