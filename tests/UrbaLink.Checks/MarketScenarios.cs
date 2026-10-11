using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Npgsql;
using UrbaLink.Contracts;

internal static class MarketScenarios
{
    public static async Task Run(string url)
    {
        var connection=Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION")??throw new Exception("Ejecuta scripts/Test-Posts.ps1 -Suite market.");
        var settings=new NpgsqlConnectionStringBuilder(connection);
        if(!Regex.IsMatch(settings.Database??"","^urbalink_posts_test_[a-f0-9]{32}$")) throw new Exception("Solo se permiten bases temporales de pruebas.");
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync();
        async Task Sql(string text,params object[] values)
        {
            await using var command=new NpgsqlCommand(text,db);
            foreach(var value in values) command.Parameters.Add(new NpgsqlParameter {Value=value});
            await command.ExecuteNonQueryAsync();
        }
        using var anonymous=new HttpClient {BaseAddress=new Uri(url)};
        async Task<T> Post<T>(HttpClient c,string path,object data)
        {
            using var r=await c.PostAsJsonAsync(path,data);
            if(!r.IsSuccessStatusCode)throw new Exception($"{path}: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
            return (await r.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage r,HttpStatusCode status)
        {
            using(r)if(r.StatusCode!=status)throw new Exception($"Expected {status}: {r.StatusCode} {await r.Content.ReadAsStringAsync()}");
        }
        static void Check(bool result,string message){if(!result)throw new Exception(message);}
        async Task<(HttpClient Client,Guid Id)> User(string name)
        {
            var session=await Post<SessionDto>(anonymous,"api/auth/register",new RegisterRequest(name,$"{Guid.NewGuid():N}@example.test","Market-test-password-123"));
            var c=new HttpClient {BaseAddress=new Uri(url)};c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
            return(c,session.Account.Id);
        }
        var adminUser=await User("Administración");using var admin=adminUser.Client;
        var ownerUser=await User("Propietario");using var owner=ownerUser.Client;
        var aliceUser=await User("Alicia");using var alice=aliceUser.Client;
        var bobUser=await User("Bruno");using var bob=bobUser.Client;
        var outsiderUser=await User("Exterior");using var outsider=outsiderUser.Client;
        var community=await Post<CommunityDto>(admin,"api/communities",new CreateCommunityRequest("Objetos de prueba"));
        foreach(var member in new[]{owner,alice,bob})
        {
            var invite=await Post<InvitationDto>(admin,$"api/communities/{community.Id}/invitations",new{});
            await Post<CommunityDto>(member,"api/communities/join",new JoinRequest(invite.Code));
        }
        var root=$"api/communities/{community.Id}/items";
        Task<ItemDetail?> Detail(HttpClient c,long id)=>c.GetFromJsonAsync<ItemDetail>($"{root}/{id}");
        var photo=await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory,"Fixtures","object.jpg"));
        Check(MarketPhotoRules.IsValid(photo),"Fixture JPEG inválida");
        Check(!MarketPhotoRules.IsValid([255,216,255,217]),"Se aceptó una foto sin imagen");
        var saleRequest=new CreateItemRequest(Guid.NewGuid(),"Taladro azul","Funciona correctamente.","Sale",2500,null,null,0,photo);
        await Expect(await anonymous.GetAsync(root),HttpStatusCode.Unauthorized);
        await Expect(await outsider.GetAsync(root),HttpStatusCode.Forbidden);
        await Expect(await outsider.PostAsJsonAsync(root,saleRequest),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync(root,saleRequest with {PriceCents=-1}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root,saleRequest with {Photo=[255,216,255,217]}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root,saleRequest with {Photo=new byte[40001]}),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(root,saleRequest with {Mode="Rent",Period=null}),HttpStatusCode.BadRequest);
        var creates=await Task.WhenAll(Post<ItemDto>(owner,root,saleRequest),Post<ItemDto>(owner,root,saleRequest));
        var sale=creates[0];Check(creates[1].Id==sale.Id,"Publicación duplicada por reintento concurrente");
        Check(sale.Photo!.SequenceEqual(photo)&&sale.PriceCents==2500,"Foto o precio alterados");
        await Expect(await owner.PostAsJsonAsync(root,saleRequest with {Title="Otro título"}),HttpStatusCode.Conflict);
        var rent=await Post<ItemDto>(owner,root,saleRequest with {RequestId=Guid.NewGuid(),Title="Escalera",Mode="Rent",PriceCents=500,Period="Day",Photo=null});
        await Expect(await alice.PostAsJsonAsync($"{root}/{rent.Id}/availability",new AvailabilityRequest(true)),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync($"{root}/{rent.Id}/availability",new AvailabilityRequest(true)),HttpStatusCode.NoContent);
        Check((await Detail(alice,rent.Id))!.Item.IsLent,"No se mostró el estado manual de alquiler");
        await Expect(await alice.PostAsJsonAsync($"{root}/{rent.Id}/requests",new{}),HttpStatusCode.NotFound);
        var chat=await Post<MarketConversationDto>(alice,$"{root}/{rent.Id}/conversations",new StartMarketConversation());
        var secondChat=await Post<MarketConversationDto>(bob,$"{root}/{rent.Id}/conversations",new StartMarketConversation());
        Check(chat.Id!=secondChat.Id,"Se mezclaron conversaciones privadas");
        Check((await Post<MarketConversationDto>(alice,$"{root}/{rent.Id}/conversations",new StartMarketConversation())).Id==chat.Id,"Conversación duplicada");
        await Expect(await bob.PostAsJsonAsync($"{root}/{rent.Id}/conversations",new StartMarketConversation(aliceUser.Id)),HttpStatusCode.Forbidden);
        var messages=$"{root}/{rent.Id}/conversations/{chat.Id}/messages";
        var message=new MarketMessageRequest(Guid.NewGuid(),"¿Podría recogerla cuando quede libre?");
        var sends=await Task.WhenAll(alice.PostAsJsonAsync(messages,message),alice.PostAsJsonAsync(messages,message));
        foreach(var r in sends)await Expect(r,HttpStatusCode.NoContent);
        var history=(await alice.GetFromJsonAsync<MarketMessagePage>(messages))!;
        Check(history.Items.Length==1&&history.Items[0].AuthorId==aliceUser.Id,"Mensaje duplicado o autor incorrecto");
        await Expect(await alice.PostAsJsonAsync(messages,message with {Body="Otra cosa"}),HttpStatusCode.Conflict);
        await Expect(await owner.PostAsJsonAsync(messages,new MarketMessageRequest(Guid.NewGuid(),"Sí, hablamos de la entrega.")),HttpStatusCode.NoContent);
        var ownerInbox=(await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!;
        Check(ownerInbox.UnreadCount==1 && ownerInbox.Items.Single().ConversationId==chat.Id,"Contador duplicado o destinatario incorrecto");
        Check((await alice.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount==1,"Falta aviso de respuesta al interesado");
        Check((await admin.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount==0,"Administrador recibió mensajes ajenos");
        await Expect(await bob.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items[0].Id)),HttpStatusCode.NotFound);
        await Expect(await owner.PostAsJsonAsync(messages+"/read",new MessageReadRequest(long.MaxValue)),HttpStatusCode.BadRequest);
        await Expect(await owner.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items[0].Id)),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount==0,"La lectura no limpió el contador");
        await Expect(await alice.PostAsJsonAsync(messages,new MarketMessageRequest(Guid.NewGuid(),"Nuevo mensaje después de leer")),HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync(messages+"/read",new MessageReadRequest(history.Items[0].Id)),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount==1,"Una lectura antigua consumió un mensaje nuevo");
        await Expect(await bob.GetAsync(messages),HttpStatusCode.NotFound);
        await Expect(await admin.GetAsync(messages),HttpStatusCode.NotFound);
        await Expect(await bob.PostAsJsonAsync(messages,message),HttpStatusCode.NotFound);
        await Expect(await outsider.GetAsync(messages),HttpStatusCode.Forbidden);
        await Expect(await alice.GetAsync($"{root}/{sale.Id}/conversations/{chat.Id}/messages"),HttpStatusCode.NotFound);
        Check((await Detail(admin,rent.Id))!.Conversations.Length==0,"El administrador vio conversaciones ajenas");
        Check((await Detail(owner,rent.Id))!.Conversations.Length==2,"El propietario no ve sus conversaciones");
        await Expect(await owner.PostAsJsonAsync($"{root}/{rent.Id}/availability",new AvailabilityRequest(false)),HttpStatusCode.NoContent);
        Check(!(await Detail(alice,rent.Id))!.Item.IsLent,"No se pudo marcar disponible");

        await Sql("INSERT INTO market_messages(conversation_id,author_id,request_id,body) SELECT $1,$2,gen_random_uuid(),'Mensaje '||n FROM generate_series(1,21) n",chat.Id,ownerUser.Id);
        var page1=(await alice.GetFromJsonAsync<MarketMessagePage>(messages))!;
        var page2=(await alice.GetFromJsonAsync<MarketMessagePage>(messages+"?before="+page1.NextBefore))!;
        Check(page1.Items.Length==20 && page2.Items.Length==4 && page2.NextBefore is null && !page1.Items.Select(m=>m.Id).Intersect(page2.Items.Select(m=>m.Id)).Any(),"Paginación privada incorrecta");
        var saleChat=await Post<MarketConversationDto>(alice,$"{root}/{sale.Id}/conversations",new StartMarketConversation());
        await Expect(await alice.PostAsJsonAsync($"{root}/{sale.Id}/complete",new{}),HttpStatusCode.Forbidden);
        await Expect(await owner.PostAsJsonAsync($"{root}/{sale.Id}/complete",new{}),HttpStatusCode.NoContent);
        await Expect(await bob.PostAsJsonAsync($"{root}/{sale.Id}/conversations",new StartMarketConversation()),HttpStatusCode.Conflict);
        await Expect(await alice.PostAsJsonAsync($"{root}/{sale.Id}/conversations/{saleChat.Id}/messages",new MarketMessageRequest(Guid.NewGuid(),"Gracias por la entrega")),HttpStatusCode.NoContent);
        Console.WriteLine("OK: venta, alquiler manual, fotos, privacidad, notificaciones, reintentos y lecturas concurrentes.");

        var auctionRequest=saleRequest with {RequestId=Guid.NewGuid(),Title="Bicicleta",Mode="Auction",PriceCents=1000,EndsAt=DateTimeOffset.UtcNow.AddHours(1),BidStepCents=100,Photo=null};
        await Expect(await owner.PostAsJsonAsync(root,auctionRequest with {EndsAt=DateTimeOffset.UtcNow.AddSeconds(1)}),HttpStatusCode.BadRequest);
        var auction=await Post<ItemDto>(owner,root,auctionRequest);
        Check((await Post<ItemDto>(owner,root,auctionRequest)).Id==auction.Id,"Reintento con precisión temporal falló");
        var bids=$"{root}/{auction.Id}/bids";
        await Expect(await owner.PostAsJsonAsync(bids,new BidRequest(Guid.NewGuid(),1000)),HttpStatusCode.Forbidden);
        await Expect(await alice.PostAsJsonAsync(bids,new BidRequest(Guid.NewGuid(),999)),HttpStatusCode.Conflict);
        var aliceBid=new BidRequest(Guid.NewGuid(),1000);var bobBid=new BidRequest(Guid.NewGuid(),1000);
        var races=await Task.WhenAll(alice.PostAsJsonAsync(bids,aliceBid),bob.PostAsJsonAsync(bids,bobBid));
        Check(races.Count(r=>r.StatusCode==HttpStatusCode.NoContent)==1 && races.Count(r=>r.StatusCode==HttpStatusCode.Conflict)==1,"Se admitieron dos pujas iguales");
        var firstWinner=races[0].IsSuccessStatusCode?alice:bob;
        var firstBid=races[0].IsSuccessStatusCode?aliceBid:bobBid;
        foreach(var r in races)r.Dispose();
        await Expect(await firstWinner.PostAsJsonAsync(bids,firstBid),HttpStatusCode.NoContent);
        Check((await Detail(owner,auction.Id))!.Item.BidCount==1,"Reintento duplicó la puja");
        await Expect(await bob.PostAsJsonAsync(bids,new BidRequest(Guid.NewGuid(),1099)),HttpStatusCode.Conflict);
        await Expect(await alice.PostAsJsonAsync(bids,new BidRequest(Guid.NewGuid(),1200)),HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync($"{root}/{auction.Id}/withdraw",new{}),HttpStatusCode.Conflict);
        await Expect(await owner.PostAsJsonAsync($"{root}/{auction.Id}/complete",new{}),HttpStatusCode.Conflict);
        await Sql("UPDATE market_items SET ends_at=clock_timestamp()-interval '1 second' WHERE id=$1",auction.Id);
        await Expect(await bob.PostAsJsonAsync(bids,new BidRequest(Guid.NewGuid(),1400)),HttpStatusCode.Conflict);
        var closed=(await Detail(alice,auction.Id))!.Item;
        Check(closed.State=="Ended" && closed.WinnerId==aliceUser.Id && closed.HighestBidCents==1200,"Ganador o cierre incorrectos");
        var winningChat=await Post<MarketConversationDto>(owner,$"{root}/{auction.Id}/conversations",new StartMarketConversation(aliceUser.Id));
        Check((await Post<MarketConversationDto>(alice,$"{root}/{auction.Id}/conversations",new StartMarketConversation())).Id==winningChat.Id,"Ganador y propietario no comparten conversación");
        await Expect(await bob.PostAsJsonAsync($"{root}/{auction.Id}/conversations",new StartMarketConversation()),HttpStatusCode.Conflict);
        await Expect(await owner.PostAsJsonAsync($"{root}/{auction.Id}/complete",new{}),HttpStatusCode.NoContent);
        Check((await alice.GetFromJsonAsync<ItemPage>(root+"?scope=Activity"))!.Items.Any(i=>i.Id==auction.Id),"Desapareció la subasta del historial");
        Check(!(await alice.GetFromJsonAsync<ItemPage>(root))!.Items.Any(i=>i.Id==auction.Id || i.Id==sale.Id),"El catálogo incluye ventas completadas");

        var nearClose=await Post<ItemDto>(owner,root,auctionRequest with {RequestId=Guid.NewGuid()});
        await using(var tx=await db.BeginTransactionAsync())
        {
            await Sql("UPDATE market_items SET ends_at=clock_timestamp()+interval '1 second' WHERE id=$1",nearClose.Id);
            var waiting=alice.PostAsJsonAsync($"{root}/{nearClose.Id}/bids",new BidRequest(Guid.NewGuid(),1000));
            await Task.Delay(1300);
            await tx.CommitAsync();
            await Expect(await waiting,HttpStatusCode.Conflict);
        }
        Console.WriteLine("OK: subastas, pujas simultáneas, incremento, ganador y cierre durante espera por bloqueo.");

        await Sql("INSERT INTO market_items(community_id,owner_id,request_id,title,description,mode,price_cents) SELECT $1,$2,gen_random_uuid(),'Objeto '||n,'Prueba','Sale',100 FROM generate_series(1,21) n",community.Id,ownerUser.Id);
        var catalog1=(await alice.GetFromJsonAsync<ItemPage>(root))!;
        var catalog2=(await alice.GetFromJsonAsync<ItemPage>(root+"?before="+catalog1.NextBefore))!;
        Check(catalog1.Items.Length==20 && catalog2.Items.Length>=2 && !catalog1.Items.Select(i=>i.Id).Intersect(catalog2.Items.Select(i=>i.Id)).Any(),"Paginación del catálogo incorrecta");
        Check((await owner.GetFromJsonAsync<ItemPage>(root+"?scope=Mine"))!.Items.All(i=>i.OwnerId==ownerUser.Id),"Filtro de propietario incorrecto");
        Check((await alice.GetFromJsonAsync<ItemPage>(root+"?mode=Rent"))!.Items.All(i=>i.Mode=="Rent"),"Filtro de modalidad incorrecto");
        var overview=(await admin.GetFromJsonAsync<CommunityOverview>($"api/communities/{community.Id}/overview"))!;
        Check(overview.SharedItems==22,"Inicio no cuenta los objetos del catálogo");
        await Expect(await admin.PostAsJsonAsync($"api/communities/{community.Id}/delete",new DeleteCommunityRequest(community.Name)),HttpStatusCode.NoContent);
        await Expect(await alice.GetAsync(messages),HttpStatusCode.Forbidden);
        await Expect(await alice.PostAsJsonAsync(messages,new MarketMessageRequest(Guid.NewGuid(),"No debe enviarse")),HttpStatusCode.Forbidden);
        await Expect(await owner.GetAsync(root),HttpStatusCode.Forbidden);
        Check((await owner.GetFromJsonAsync<NotificationInbox>("api/notifications"))!.UnreadCount==0,"Avisos de comunidad archivada accesibles");
        await Expect(await admin.PostAsJsonAsync($"api/communities/{community.Id}/restore",new{}),HttpStatusCode.NoContent);
        await Expect(await alice.GetAsync(messages),HttpStatusCode.OK);
        Console.WriteLine("OK: catálogo paginado, filtros, resumen y aislamiento de comunidades archivadas.");
    }
}
