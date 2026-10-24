using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using UrbaLink.Contracts;

internal static class SearchScenarios
{
    public static async Task Run(string url)
    {
        using var owner=new HttpClient{BaseAddress=new Uri(url)};
        using var member=new HttpClient{BaseAddress=new Uri(url)};
        using var stranger=new HttpClient{BaseAddress=new Uri(url)};
        using var anonymous=new HttpClient{BaseAddress=new Uri(url)};
        async Task<T> Post<T>(HttpClient c,string path,object body)
        {
            using var response=await c.PostAsJsonAsync(path,body);
            if(!response.IsSuccessStatusCode)throw new Exception($"{path}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage response,HttpStatusCode status)
        {using(response)if(response.StatusCode!=status)throw new Exception($"Esperado {status}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");}
        static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        foreach(var c in new[]{owner,member,stranger})
        {
            var session=await Post<SessionDto>(c,"api/auth/register",new RegisterRequest("Búsqueda",$"{Guid.NewGuid():N}@example.test","x"));
            c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
        }
        var community=await Post<CommunityDto>(owner,"api/communities",new CreateCommunityRequest("Comunidad de búsqueda"));
        var root=$"api/communities/{community.Id}";
        var invitation=await Post<InvitationDto>(owner,root+"/invitations",new{});
        await Post<CommunityDto>(member,"api/communities/join",new JoinRequest(invitation.Code));
        var title=await Post<PostDto>(owner,root+"/posts",new CreatePostRequest("Herramientas de jardín","Palabra solo en el título","General"));
        var body=await Post<PostDto>(owner,root+"/posts",new CreatePostRequest("Se ofrece ayuda","Experiencia con JARDÍN comunitario","Ayuda"));
        var service=await Post<PostDto>(owner,root+"/posts",new CreatePostRequest("Servicio de jardín","Poda y mantenimiento","Ofrezco"));
        var item=await Post<ItemDto>(owner,root+"/items",new CreateItemRequest(Guid.NewGuid(),"Mesa de jardín","Buen estado","Sale",2000,null,null,0,null));
        var animal=await Post<AnimalNoticeDto>(owner,root+"/animals",new AnimalNoticeInput(Guid.NewGuid(),"Lost","Cat","Luna jardín","Entrada",DateTimeOffset.UtcNow.AddHours(-1),"Collar rojo",false,null));
        var ride=await Post<RideDto>(owner,root+"/rides",new CreateRideRequest(Guid.NewGuid(),"Jardín","Centro",1,480,null,false,2,0,false,"","",""));
        await Post<CommentDto>(member,root+$"/posts/{title.Id}/comments",new CreateCommentRequest("comentarioexclusivo",Guid.NewGuid()));
        var chat=await Post<MarketConversationDto>(member,root+$"/services/{service.Id}/conversations",new{});
        await Expect(await member.PostAsJsonAsync(root+$"/services/{service.Id}/conversations/{chat.Id}/messages",new MarketMessageRequest(Guid.NewGuid(),"privadoexclusivo")),HttpStatusCode.NoContent);
        var search=root+"/search?q=";
        await Expect(await anonymous.GetAsync(search+"jardin"),HttpStatusCode.Unauthorized);
        await Expect(await stranger.GetAsync(search+"jardin"),HttpStatusCode.Forbidden);
        await Expect(await owner.GetAsync(search),HttpStatusCode.BadRequest);
        await Expect(await owner.GetAsync(search+new string('x',121)),HttpStatusCode.BadRequest);
        await Expect(await owner.GetAsync(search+"jardin&before=invalid"),HttpStatusCode.BadRequest);
        var page=(await member.GetFromJsonAsync<CommunitySearchPage>(search+"JARDIN"))!;
        Check(page.Items.Length==6,"No encuentra todos los módulos por título y descripción sin tildes ni mayúsculas.");
        Check(page.Items.Any(r=>r.Kind=="Post"&&r.Id==body.Id)&&page.Items.Any(r=>r.Kind=="Post"&&r.Id==ride.Post.Id)&&
            page.Items.Any(r=>r.Kind=="Item"&&r.Id==item.Id)&&page.Items.Any(r=>r.Kind=="Animal"&&r.Id==animal.Id),"Faltan módulos en la búsqueda.");
        foreach(var term in new[]{"comentarioexclusivo","privadoexclusivo","' OR 1=1 --"})
            Check((await owner.GetFromJsonAsync<CommunitySearchPage>(search+Uri.EscapeDataString(term)))!.Items.Length==0,"La búsqueda incluye contenido privado, comentarios o interpreta SQL.");
        var otherCommunity=await Post<CommunityDto>(owner,"api/communities",new CreateCommunityRequest("Otra comunidad"));
        await Post<PostDto>(owner,$"api/communities/{otherCommunity.Id}/posts",new CreatePostRequest("Jardín ajeno","No debe mezclarse","General"));
        var literal=await Post<PostDto>(owner,root+"/posts",new CreatePostRequest("Símbolo literal","La palabra %_ es literal","General"));
        Check((await owner.GetFromJsonAsync<CommunitySearchPage>(search+Uri.EscapeDataString("%_")))!.Items.Single().Id==literal.Id,"Interpreta comodines en vez de texto literal.");
        await Expect(await owner.DeleteAsync(root+$"/posts/{title.Id}"),HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync(root+$"/items/{item.Id}/withdraw",new{}),HttpStatusCode.NoContent);
        await Expect(await owner.PostAsJsonAsync(root+$"/animals/{animal.Id}/close",new CloseAnimalNoticeRequest("Withdrawn")),HttpStatusCode.NoContent);
        Check((await owner.GetFromJsonAsync<CommunitySearchPage>(search+"jardin"))!.Items.Length==3,"Incluye publicaciones retiradas o de otras comunidades.");
        for(var i=0;i<21;i++)await Post<PostDto>(owner,root+"/posts",new CreatePostRequest("Jardín paginado "+i,"Datos ficticios de paginación","General"));
        var connection=Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION");
        if(new NpgsqlConnectionStringBuilder(connection).Database?.StartsWith("urbalink_posts_test_")!=true)throw new Exception("Solo se permiten bases temporales.");
        await using(var db=new NpgsqlConnection(connection))
        {
            await db.OpenAsync();
            foreach(var table in new[]{"posts","market_items","animal_notices"})
            {
                await using var command=new NpgsqlCommand($"UPDATE {table} SET created_at=$2 WHERE community_id=$1",db);
                command.Parameters.AddWithValue(community.Id);command.Parameters.AddWithValue(new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc));await command.ExecuteNonQueryAsync();
            }
        }
        page=(await owner.GetFromJsonAsync<CommunitySearchPage>(search+"jardin"))!;
        Check(page.Items.Length==20&&page.NextBefore is not null,"No pagina los resultados.");
        var second=(await owner.GetFromJsonAsync<CommunitySearchPage>(search+"jardin&before="+Uri.EscapeDataString(page.NextBefore!)))!;
        Check(second.Items.Length==4&&second.NextBefore is null&&page.Items.Concat(second.Items).Select(r=>(r.Kind,r.Id)).Distinct().Count()==24,"Pierde o duplica resultados con fechas iguales.");
        await Expect(await owner.PostAsJsonAsync(root+"/delete",new DeleteCommunityRequest(community.Name)),HttpStatusCode.NoContent);
        await Expect(await owner.GetAsync(search+"jardin"),HttpStatusCode.Forbidden);
        Console.WriteLine("OK: búsqueda global en títulos y descripciones, tildes y mayúsculas, privacidad, aislamiento, retiradas, texto literal y paginación estable.");
    }
}
