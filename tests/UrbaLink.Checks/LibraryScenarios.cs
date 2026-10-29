using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class LibraryScenarios
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
        static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        var users=new List<SessionDto>();
        foreach(var client in new[]{admin,owner,member,other,outsider})
        {
            var session=await Post<SessionDto>(client,"api/auth/register",new RegisterRequest("Vecino de Biblioteca",$"{Guid.NewGuid():N}@example.test","x"));
            users.Add(session);client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
        }
        var community=await Post<CommunityDto>(admin,"api/communities",new CreateCommunityRequest("Biblioteca de prueba"));
        var root=$"api/communities/{community.Id}";
        foreach(var client in new[]{owner,member,other}){var invite=await Post<InvitationDto>(admin,root+"/invitations",new{});await Post<CommunityDto>(client,"api/communities/join",new JoinRequest(invite.Code));}
        var photo=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures/object.jpg"));
        var input=new BookInput(Guid.NewGuid(),"Jardín de historias","Autora de prueba","","Good","Adult","Lend","Libro de relatos",photo);
        await Expect(await outsider.PostAsJsonAsync(root+"/books",input),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync(root+"/books",input with{Photo=null}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root+"/books",input with{Photo=[1,2,3,4]}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root+"/books",input with{Condition="Invalid"}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root+"/books",input with{Audience="Invalid"}),HttpStatusCode.BadRequest);
        var twins=await Task.WhenAll(Post<BookDto>(owner,root+"/books",input),Post<BookDto>(owner,root+"/books",input));var book=twins[0];
        Check(twins[0].Id==twins[1].Id&&book.Photo.SequenceEqual(photo),"Duplicado o foto alterada al crear libro.");
        await Expect(await owner.PostAsJsonAsync(root+"/books",input with{Title="Otro título"}),HttpStatusCode.Conflict);
        var path=root+"/books/"+book.Id;
        await Expect(await outsider.GetAsync(path),HttpStatusCode.Forbidden);
        foreach(var audience in new[]{"Young","Child","Baby"})
            await Post<BookDto>(owner,root+"/books",input with{RequestId=Guid.NewGuid(),Title="Libro "+audience,Audience=audience});
        Check((await member.GetFromJsonAsync<BookPage>(root+"/books?audience=Adult&mode=Lend"))!.Items.Single().Id==book.Id,"Filtros de Biblioteca incorrectos.");
        await Expect(await member.PutAsJsonAsync(path+"?version=1",input),HttpStatusCode.Forbidden);
        await Expect(await owner.PutAsJsonAsync(path+"?version=1",input with{Isbn="978-84-376-0494-7",Condition="LikeNew"}),HttpStatusCode.NoContent);
        await Expect(await owner.PutAsJsonAsync(path+"?version=1",input),HttpStatusCode.Conflict);
        var detail=(await owner.GetFromJsonAsync<BookDetail>(path))!;
        Check(detail.Book.Version==2&&detail.Book.Isbn=="978-84-376-0494-7","No conserva edición o versión del libro.");
        var chats=await Task.WhenAll(Post<MarketConversationDto>(member,path+"/conversations",new{}),Post<MarketConversationDto>(member,path+"/conversations",new{}));
        Check(chats[0].Id==chats[1].Id,"Duplica conversación de Biblioteca.");
        var another=await Post<MarketConversationDto>(other,path+"/conversations",new{});
        var messages=path+$"/conversations/{chats[0].Id}/messages";
        var message=new MarketMessageRequest(Guid.NewGuid(),"¿Me lo prestas este mes?");
        await Expect(await member.PostAsJsonAsync(messages,message),HttpStatusCode.NoContent);
        await Expect(await member.PostAsJsonAsync(messages,message),HttpStatusCode.NoContent);
        await Expect(await admin.GetAsync(messages),HttpStatusCode.NotFound);
        await Expect(await other.GetAsync(messages),HttpStatusCode.NotFound);
        var inbox=(await owner.GetFromJsonAsync<ConversationInbox>("api/conversations"))!;
        Check(inbox.Items.Any(c=>c.Kind=="Book"&&c.SubjectId==book.Id&&c.UnreadCount==1),"Biblioteca no aparece en mensajes.");
        var notification=(await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!;
        Check(notification.UnreadCount==1&&notification.LatestMessageId>0,"Biblioteca no avisa de mensajes.");
        var history=(await owner.GetFromJsonAsync<MarketMessagePage>(messages))!;
        await Expect(await owner.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items.Single().Id)),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<NotificationCount>("api/notifications/count"))!.UnreadCount==0,"Leer Biblioteca no limpia el aviso.");
        await Post<ReportReceipt>(member,"api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Book",book.Id,"Foto o descripción no apropiada"));
        await Expect(await admin.PostAsJsonAsync("api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Message",history.Items[0].Id,"No puede leer mensajes ajenos")),HttpStatusCode.NotFound);
        await Post<ReportReceipt>(owner,"api/reports",new ReportRequest(Guid.NewGuid(),community.Id,"Message",history.Items[0].Id,"Mensaje concreto denunciado"));
        await Expect(await member.PostAsJsonAsync(path+"/state",new BookStateRequest("Lent")),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync(path+"/state",new BookStateRequest("Lent")),HttpStatusCode.NoContent);
        await Expect(await owner.PutAsJsonAsync(path+"?version=3",input with{Mode="Pass"}),HttpStatusCode.Conflict);
        await Expect(await owner.PostAsJsonAsync(path+"/state",new BookStateRequest("Available")),HttpStatusCode.NoContent);
        var pass=await Post<BookDto>(owner,root+"/books",input with{RequestId=Guid.NewGuid(),Mode="Pass",Audience="Child"});
        await Expect(await owner.PostAsJsonAsync(root+$"/books/{pass.Id}/state",new BookStateRequest("Lent")),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root+$"/books/{pass.Id}/state",new BookStateRequest("Passed")),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<BookPage>(root+"/books?scope=Mine"))!.Items.Any(b=>b.Id==pass.Id),"Se pierde el libro pasado del historial del propietario.");
        Check((await member.GetFromJsonAsync<BookPage>(root+"/books"))!.Items.All(b=>b.Id!=pass.Id),"Un libro pasado sigue disponible.");
        var results=(await member.GetFromJsonAsync<CommunitySearchPage>(root+"/search?q=jardin"))!;
        Check(results.Items.Any(r=>r.Kind=="Book"&&r.Id==book.Id),"El buscador no incluye Biblioteca.");
        await Expect(await admin.PostAsJsonAsync(path+"/state",new BookStateRequest("Withdrawn")),HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync(path+"/state",new BookStateRequest("Available")),HttpStatusCode.Conflict);
        await Expect(await member.PostAsJsonAsync(messages,new MarketMessageRequest(Guid.NewGuid(),"Se conserva la conversación")),HttpStatusCode.NoContent);
        Check((await member.GetFromJsonAsync<CommunitySearchPage>(root+"/search?q=jardin"))!.Items.All(r=>r.Id!=book.Id||r.Kind!="Book"),"Se busca un libro retirado.");
        // Every board category can carry a photo; rides use the same post projection.
        foreach(var category in PostCategories.All)
        {
            var post=await Post<PostDto>(admin,root+"/posts",new CreatePostRequest("Imagen en "+category,"Descripción de prueba",category,photo));
            Check(post.Photo?.SequenceEqual(photo)==true,"Se pierde foto al publicar "+category);
            var listed=(await member.GetFromJsonAsync<PostPage>(root+"/posts?category="+Uri.EscapeDataString(category)))!;
            Check(listed.Items.Single(p=>p.Id==post.Id).Photo?.SequenceEqual(photo)==true,"Se pierde foto en el tablón.");
            if(category=="Ofrezco")Check((await member.GetFromJsonAsync<ServiceDetail>(root+$"/services/{post.Id}"))!.Service.Photo?.SequenceEqual(photo)==true,"Servicio sin foto.");
            else Check((await member.GetFromJsonAsync<CommentPage>(root+$"/posts/{post.Id}/comments"))!.Post.Photo?.SequenceEqual(photo)==true,"Detalle de publicación sin foto.");
        }
        await Expect(await owner.PostAsJsonAsync(root+"/posts",new CreatePostRequest("Foto inválida","Texto","General",[1,2,3,4])),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root+"/posts",new CreatePostRequest("Sin permiso","Texto","Aviso oficial",photo)),HttpStatusCode.Forbidden);
        var rideInput=new CreateRideRequest(Guid.NewGuid(),"Carambolo","Centro",1,480,null,false,2,0,false,"","","",photo);
        var ride=await Post<RideDto>(owner,root+"/rides",rideInput);
        Check(ride.Post.Photo?.SequenceEqual(photo)==true,"No guarda foto del coche.");
        Check((await Post<RideDto>(owner,root+"/rides",rideInput)).Post.Id==ride.Post.Id,"Reintentar trayecto con foto lo duplica.");
        Check((await member.GetFromJsonAsync<RidePageResult>(root+"/rides"))!.Items.Single().Post.Photo?.SequenceEqual(photo)==true,"No se ve foto en Trayectos.");
        await Expect(await owner.PostAsJsonAsync(root+"/rides",rideInput with{Photo=[1,2,3,4]}),HttpStatusCode.BadRequest);
        await Expect(await admin.PostAsJsonAsync(root+"/delete",new DeleteCommunityRequest(community.Name)),HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync(root+"/books"),HttpStatusCode.Forbidden);
        Console.WriteLine("OK: Biblioteca, portada, ISBN opcional, estados, públicos, préstamo y cesión, edición, reintentos, chats privados, avisos, búsqueda y fotos en todo el tablón y Trayectos.");
    }
}
