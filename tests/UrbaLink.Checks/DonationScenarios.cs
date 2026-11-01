using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class DonationScenarios
{
    public static async Task Run(string url)
    {
        using var admin=new HttpClient{BaseAddress=new Uri(url)};
        using var owner=new HttpClient{BaseAddress=new Uri(url)};
        using var member=new HttpClient{BaseAddress=new Uri(url)};
        using var other=new HttpClient{BaseAddress=new Uri(url)};
        using var outsider=new HttpClient{BaseAddress=new Uri(url)};
        async Task<T> Post<T>(HttpClient c,string path,object data)
        {
            using var response=await c.PostAsJsonAsync(path,data);
            if(!response.IsSuccessStatusCode)throw new Exception($"{path}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage response,HttpStatusCode status)
        {using(response)if(response.StatusCode!=status)throw new Exception($"Esperado {status}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");}
        static void Check(bool ok,string text){if(!ok)throw new Exception(text);}
        foreach(var client in new[]{admin,owner,member,other,outsider})
        {
            var session=await Post<SessionDto>(client,"api/auth/register",new RegisterRequest("Vecino de ropa",$"{Guid.NewGuid():N}@example.test","x"));
            client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
        }
        var community=await Post<CommunityDto>(admin,"api/communities",new CreateCommunityRequest("Ropa de prueba"));
        var root=$"api/communities/{community.Id}";
        foreach(var client in new[]{owner,member,other}){var invite=await Post<InvitationDto>(admin,root+"/invitations",new{});await Post<CommunityDto>(client,"api/communities/join",new JoinRequest(invite.Code));}
        var input=new DonationInput(Guid.NewGuid(),"Abrigos de invierno","Winter","M","Good","Adult",5,"Lote para compartir",null);
        await Expect(await outsider.GetAsync(root+"/donations"),HttpStatusCode.Forbidden);
        await Expect(await outsider.PostAsJsonAsync(root+"/donations",input),HttpStatusCode.Forbidden);
        foreach(var bad in new[]{input with{Title=" "},input with{Size=""},input with{Quantity=0},input with{Quantity=1001},input with{Season="Invalid"},input with{Audience="Invalid"},input with{Condition="Invalid"},input with{Photo=[1,2,3,4]}})
            await Expect(await owner.PostAsJsonAsync(root+"/donations",bad),HttpStatusCode.BadRequest);
        var twins=await Task.WhenAll(Post<DonationDto>(owner,root+"/donations",input),Post<DonationDto>(owner,root+"/donations",input));
        var donation=twins[0];Check(donation.Id==twins[1].Id&&donation.Photo is null&&donation.Quantity==5,"Publicación sin foto o reintento incorrectos.");
        await Expect(await owner.PostAsJsonAsync(root+"/donations",input with{Quantity=6}),HttpStatusCode.Conflict);
        var path=root+"/donations/"+donation.Id;
        await Expect(await outsider.GetAsync(path),HttpStatusCode.Forbidden);
        var foreign=await Post<CommunityDto>(outsider,"api/communities",new CreateCommunityRequest("Otra comunidad de ropa"));
        var foreignInvite=await Post<InvitationDto>(outsider,$"api/communities/{foreign.Id}/invitations",new{});
        await Post<CommunityDto>(owner,"api/communities/join",new JoinRequest(foreignInvite.Code));
        await Expect(await owner.GetAsync($"api/communities/{foreign.Id}/donations/{donation.Id}"),HttpStatusCode.NotFound);
        Check((await member.GetFromJsonAsync<ItemPage>(root+"/items"))!.Items.Length==0,"Las donaciones aparecen en Compraventa.");
        foreach(var audience in new[]{"Baby","Child","Young"})await Post<DonationDto>(owner,root+"/donations",input with{RequestId=Guid.NewGuid(),Audience=audience,Size="4 años",Season="Summer"});
        Check((await member.GetFromJsonAsync<DonationPage>(root+"/donations?audience=Adult&season=Winter&size=m"))!.Items.Single().Id==donation.Id,"No filtra edad, talla y temporada.");
        Check((await member.GetFromJsonAsync<DonationPage>(root+"/donations?size=4"))!.Items.Length==3,"No busca tallas parciales.");
        await Expect(await member.GetAsync(root+"/donations?season=Invalid"),HttpStatusCode.BadRequest);
        await Expect(await member.GetAsync(root+"/donations?before=0"),HttpStatusCode.BadRequest);
        var photo=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures/object.jpg"));
        await Expect(await member.PutAsJsonAsync(path+"?version=1",input),HttpStatusCode.Forbidden);
        await Expect(await owner.PutAsJsonAsync(path+"?version=1",input with{Photo=photo,Quantity=6}),HttpStatusCode.NoContent);
        await Expect(await owner.PutAsJsonAsync(path+"?version=1",input),HttpStatusCode.Conflict);
        Check((await member.GetFromJsonAsync<DonationDetail>(path))!.Donation.Photo?.SequenceEqual(photo)==true,"No conserva la foto.");
        await Expect(await owner.PutAsJsonAsync(path+"?version=2",input),HttpStatusCode.NoContent);
        Check((await member.GetFromJsonAsync<DonationDetail>(path))!.Donation.Photo is null,"No permite quitar la foto.");
        var chats=await Task.WhenAll(Post<MarketConversationDto>(member,path+"/conversations",new{}),Post<MarketConversationDto>(member,path+"/conversations",new{}));
        Check(chats[0].Id==chats[1].Id,"Conversación duplicada.");
        var another=await Post<MarketConversationDto>(other,path+"/conversations",new{});
        Check((await admin.GetFromJsonAsync<DonationDetail>(path))!.Conversations.Length==0,"El administrador ve conversaciones ajenas.");
        var messages=path+$"/conversations/{chats[0].Id}/messages";
        var send=new MarketMessageRequest(Guid.NewGuid(),"Me interesa el lote, ¿cuándo podemos quedar?");
        await Expect(await member.PostAsJsonAsync(messages,send),HttpStatusCode.NoContent);
        await Expect(await member.PostAsJsonAsync(messages,send),HttpStatusCode.NoContent);
        await Expect(await admin.GetAsync(messages),HttpStatusCode.NotFound);
        await Expect(await other.GetAsync(messages),HttpStatusCode.NotFound);
        Check((await owner.GetFromJsonAsync<ConversationInbox>("api/conversations"))!.Items.Any(c=>c.Kind=="Donation"&&c.SubjectId==donation.Id&&c.UnreadCount==1),"La ropa no aparece en la bandeja.");
        Check((await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==1,"No avisa del mensaje.");
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.Items.Single().Kind=="Donation","El aviso no enlaza a la ropa.");
        var history=(await owner.GetFromJsonAsync<MarketMessagePage>(messages))!;
        await Expect(await owner.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items.Single().Id)),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==0,"No marca la lectura.");
        await Post<ReportReceipt>(member,"api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Donation",donation.Id,"Anuncio de ropa para revisar"));
        await Expect(await admin.PostAsJsonAsync("api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Message",history.Items[0].Id,"Mensaje ajeno no accesible")),HttpStatusCode.NotFound);
        await Post<ReportReceipt>(owner,"api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Message",history.Items[0].Id,"Revisión de mensaje concreto"));
        await Expect(await member.PostAsJsonAsync(path+"/state",new DonationStateRequest("Reserved")),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync(path+"/state",new DonationStateRequest("Reserved")),HttpStatusCode.NoContent);
        Check((await member.GetFromJsonAsync<DonationPage>(root+"/donations"))!.Items.Any(d=>d.Id==donation.Id&&d.State=="Reserved"),"No muestra la reserva.");
        await Expect(await owner.PostAsJsonAsync(path+"/state",new DonationStateRequest("Available")),HttpStatusCode.NoContent);
        Check((await member.GetFromJsonAsync<CommunitySearchPage>(root+"/search?q=abrigos"))!.Items.Any(r=>r.Kind=="Donation"&&r.Id==donation.Id),"No se busca la ropa.");
        await Expect(await owner.PostAsJsonAsync(path+"/state",new DonationStateRequest("Given")),HttpStatusCode.NoContent);
        Check((await member.GetFromJsonAsync<DonationPage>(root+"/donations"))!.Items.All(d=>d.Id!=donation.Id),"La ropa entregada sigue disponible.");
        Check((await owner.GetFromJsonAsync<DonationPage>(root+"/donations?scope=Mine"))!.Items.Any(d=>d.Id==donation.Id),"Se pierde el historial de ropa entregada.");
        await Expect(await owner.PostAsJsonAsync(path+"/state",new DonationStateRequest("Available")),HttpStatusCode.Conflict);
        await Expect(await owner.PutAsJsonAsync(path+"?version=6",input),HttpStatusCode.Conflict);
        await Expect(await admin.PostAsJsonAsync(path+"/conversations",new{}),HttpStatusCode.Conflict);
        Check((await Post<MarketConversationDto>(member,path+"/conversations",new{})).Id==chats[0].Id,"Se pierde el chat tras la entrega.");
        await Expect(await member.PostAsJsonAsync(messages,new MarketMessageRequest(Guid.NewGuid(),"Recogido, muchas gracias")),HttpStatusCode.NoContent);
        var withdrawn=await Post<DonationDto>(owner,root+"/donations",input with{RequestId=Guid.NewGuid(),Photo=photo});
        await Expect(await admin.PostAsJsonAsync(root+$"/donations/{withdrawn.Id}/state",new DonationStateRequest("Withdrawn")),HttpStatusCode.NoContent);
        Check((await member.GetFromJsonAsync<CommunitySearchPage>(root+"/search?q=abrigos"))!.Items.All(r=>r.Kind!="Donation"||r.Id!=withdrawn.Id),"Se busca ropa retirada.");
        for(var i=0;i<22;i++)await Post<DonationDto>(owner,root+"/donations",input with{RequestId=Guid.NewGuid(),Title="Prenda "+i});
        var first=(await member.GetFromJsonAsync<DonationPage>(root+"/donations"))!;
        var second=(await member.GetFromJsonAsync<DonationPage>(root+"/donations?before="+first.NextBefore))!;
        Check(first.Items.Length==20&&first.NextBefore is not null&&second.Items.Length>0&&!first.Items.Select(d=>d.Id).Intersect(second.Items.Select(d=>d.Id)).Any(),"La paginación repite o pierde ropa.");
        await Expect(await admin.PostAsJsonAsync(root+"/delete",new DeleteCommunityRequest(community.Name)),HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync(path),HttpStatusCode.Forbidden);
        Check((await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==0,"La comunidad archivada sigue notificando ropa.");
        Console.WriteLine("OK: ropa de todas las edades, tallas, temporadas, lotes, foto opcional, reintentos, edición, reservas, entrega, historial, paginación, búsqueda, denuncias, privacidad, mensajes y avisos.");
    }
}
