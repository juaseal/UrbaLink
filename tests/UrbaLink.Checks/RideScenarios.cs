using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;

internal static class RideScenarios
{
    public static async Task Run(string url)
    {
        using var admin=new HttpClient{BaseAddress=new Uri(url)};
        using var member=new HttpClient{BaseAddress=new Uri(url)};
        using var stranger=new HttpClient{BaseAddress=new Uri(url)};
        async Task<T> Post<T>(HttpClient c,string path,object data)
        {
            using var r=await c.PostAsJsonAsync(path,data);
            if(!r.IsSuccessStatusCode)throw new Exception($"{path}: {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
            return (await r.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage r,HttpStatusCode code)
        {using(r)if(r.StatusCode!=code)throw new Exception($"Esperado {code}, recibido {r.StatusCode}: {await r.Content.ReadAsStringAsync()}");}
        static void Check(bool value,string message){if(!value)throw new Exception(message);}
        foreach(var c in new[]{admin,member,stranger})
        {
            var session=await Post<SessionDto>(c,"api/auth/register",new RegisterRequest("Conductor",$"{Guid.NewGuid():N}@example.test","Ride-test-password-123"));
            c.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.Token);
        }
        var community=await Post<CommunityDto>(admin,"api/communities",new CreateCommunityRequest("Trayectos test"));
        var invite=await Post<InvitationDto>(admin,$"api/communities/{community.Id}/invitations",new{});
        await Post<CommunityDto>(member,"api/communities/join",new JoinRequest(invite.Code));
        var root=$"api/communities/{community.Id}/rides";
        var request=new CreateRideRequest(Guid.NewGuid()," Carambolo ","Sevilla",31,8*60+30,18*60,false,3,500,true,"Hasta 5 minutos","Puerta principal","Una mochila por persona");
        await Expect(await stranger.GetAsync(root),HttpStatusCode.Forbidden);
        await Expect(await stranger.PostAsJsonAsync(root,request),HttpStatusCode.Forbidden);
        foreach(var invalid in new[]{request with{Days=0},request with{Days=128},request with{DepartureMinutes=1440},request with{ReturnMinutes=480},request with{ReturnMinutes=null,ReturnNextDay=true},request with{PriceCents=-1},request with{Seats=0},request with{Seats=9}})
            await Expect(await member.PostAsJsonAsync(root,invalid),HttpStatusCode.BadRequest);
        var created=await Task.WhenAll(Post<RideDto>(member,root,request),Post<RideDto>(member,root,request));
        var ride=created[0];Check(ride.Post.Id==created[1].Post.Id,"Reintentos duplicaron el trayecto");
        Check(ride.Origin=="Carambolo"&&ride.Days==31&&ride.ReturnMinutes==1080&&ride.PriceCents==500&&ride.Seats==3&&ride.AllowsDetour,"Se perdieron datos estructurados");
        await Expect(await member.PostAsJsonAsync(root,request with{Seats=2}),HttpStatusCode.Conflict);
        Check(ride.Post.Category=="Coche"&&ride.Post.Body.Contains("08:30")&&ride.Post.Body.Contains("18:00")&&ride.Post.Body.Contains("por asiento y sentido"),"El tablón no refleja las condiciones");
        var monday=(await admin.GetFromJsonAsync<RidePageResult>(root+"?day=0"))!;
        var sunday=(await admin.GetFromJsonAsync<RidePageResult>(root+"?day=6"))!;
        Check(monday.Items.Length==1&&sunday.Items.Length==0,"Filtro de días incorrecto");
        var night=await Post<RideDto>(member,root,request with{RequestId=Guid.NewGuid(),DepartureMinutes=22*60,ReturnMinutes=6*60,ReturnNextDay=true,Days=64,PriceCents=0,AllowsDetour=false});
        Check(night.ReturnNextDay&&night.DetourDetails==""&&night.PriceCents==0,"Fallo en turno nocturno, gratuidad o desvío");
        await Post<RideDto>(member,root,request with{RequestId=Guid.NewGuid(),ReturnMinutes=null});
        var comments=$"api/communities/{community.Id}/posts/{ride.Post.Id}/comments";
        await Post<CommentDto>(admin,comments,new CreateCommentRequest("¿Dónde paramos?",Guid.NewGuid()));
        await Expect(await admin.GetAsync(comments),HttpStatusCode.OK);
        for(var i=0;i<20;i++)await Post<RideDto>(member,root,request with{RequestId=Guid.NewGuid(),Origin="Origen "+i});
        var first=(await admin.GetFromJsonAsync<RidePageResult>(root))!;
        var last=(await admin.GetFromJsonAsync<RidePageResult>(root+"?before="+first.NextBefore))!;
        Check(first.Items.Length==20&&last.Items.Length==3&&!first.Items.Select(x=>x.Post.Id).Intersect(last.Items.Select(x=>x.Post.Id)).Any(),"Paginación incorrecta");
        await Expect(await member.DeleteAsync($"api/communities/{community.Id}/posts/{ride.Post.Id}"),HttpStatusCode.NoContent);
        Check(!(await admin.GetFromJsonAsync<RidePageResult>(root+"?before="+(ride.Post.Id+1)))!.Items.Any(x=>x.Post.Id==ride.Post.Id),"Trayecto retirado visible");
        await Expect(await admin.PostAsJsonAsync($"api/communities/{community.Id}/delete",new DeleteCommunityRequest(community.Name)),HttpStatusCode.NoContent);
        await Expect(await member.GetAsync(root),HttpStatusCode.Forbidden);
        await Expect(await member.PostAsJsonAsync(root,request with{RequestId=Guid.NewGuid()}),HttpStatusCode.Forbidden);
        Console.WriteLine("OK: trayectos, días, ida/vuelta, noche, desvío, precio, plazas, permisos, reintentos concurrentes, preguntas, paginación y retirada.");
    }
}
