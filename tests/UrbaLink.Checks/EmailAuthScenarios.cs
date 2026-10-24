using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;
using UrbaLink.Contracts;

internal static class EmailAuthScenarios
{
    public static async Task Run(string url)
    {
        await CheckLegacyMigration();
        using var anonymous=new HttpClient{BaseAddress=new Uri(url)};
        async Task<T> Post<T>(string path,object body)
        {
            using var response=await anonymous.PostAsJsonAsync(path,body);
            if(!response.IsSuccessStatusCode)throw new Exception($"{path}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
        async Task Expect(HttpResponseMessage response,HttpStatusCode status)
        {using(response)if(response.StatusCode!=status)throw new Exception($"Esperado {status}: {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");}
        static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",new {name="A",password="x"}),HttpStatusCode.BadRequest);
        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",new {name="A",email="invalid",password="x"}),HttpStatusCode.BadRequest);
        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",new {name="A",email="a@example.test",password=""}),HttpStatusCode.BadRequest);
        var first=await Post<SessionDto>("api/auth/register",new {name="A",email=" A@Example.test ",password="x"});
        Check(first.Account.Email=="a@example.test","No normaliza el correo de registro.");
        await Post<SessionDto>("api/auth/register",new {name="A",email="b@example.test",password="1"});
        var race=await Task.WhenAll(anonymous.PostAsJsonAsync("api/auth/register",new {name="Vecino",email="race@example.test",password="a"}),
            anonymous.PostAsJsonAsync("api/auth/register",new {name="Otro",email=" RACE@EXAMPLE.TEST ",password="b"}));
        try{Check(race.Count(r=>r.StatusCode==HttpStatusCode.OK)==1&&race.Count(r=>r.StatusCode==HttpStatusCode.Conflict)==1,"Se repiten correos en registros simultáneos.");}
        finally{foreach(var response in race)response.Dispose();}
        var login=await Post<SessionDto>("api/auth/login",new {email=" A@EXAMPLE.TEST ",password="x"});
        Check(login.Account.Id==first.Account.Id,"El acceso por correo falla con mayúsculas o espacios.");
        await Expect(await anonymous.PostAsJsonAsync("api/auth/login",new {username="A",password="x"}),HttpStatusCode.Unauthorized);
        await Expect(await anonymous.PostAsJsonAsync("api/auth/login",new {email="a@example.test",password="z"}),HttpStatusCode.Unauthorized);
        using var user=new HttpClient{BaseAddress=new Uri(url)};
        user.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",first.Token);
        await Expect(await user.PutAsJsonAsync("api/me",new UpdateProfileRequest(null)),HttpStatusCode.BadRequest);
        await Expect(await user.PutAsJsonAsync("api/me",new UpdateProfileRequest("invalid")),HttpStatusCode.BadRequest);
        await Expect(await user.PutAsJsonAsync("api/me",new UpdateProfileRequest("b@example.test")),HttpStatusCode.Conflict);
        await Expect(await user.PutAsJsonAsync("api/me",new UpdateProfileRequest(" Nuevo@Example.test ")),HttpStatusCode.OK);
        Check((await user.GetFromJsonAsync<AccountDto>("api/me"))!.Email=="nuevo@example.test","No actualiza el correo del perfil.");
        Check((await Post<SessionDto>("api/auth/login",new {email="nuevo@example.test",password="x"})).Account.Id==first.Account.Id,"Cambiar el correo altera la contraseña.");
        using var web=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new Uri(url)};
        web.DefaultRequestHeaders.Add("X-UrbaLink-Web","1");
        await Expect(await web.PostAsJsonAsync("api/web/register",new {name="Web",password="w"}),HttpStatusCode.BadRequest);
        await Expect(await web.PostAsJsonAsync("api/web/register",new {name="Web",email="web@example.test",password="w"}),HttpStatusCode.OK);
        Check((await web.GetFromJsonAsync<AccountDto>("api/me"))!.Email=="web@example.test","No se guarda la sesión web.");
        await Expect(await web.PostAsJsonAsync("api/auth/logout",new {}),HttpStatusCode.NoContent);
        await Expect(await web.PostAsJsonAsync("api/web/session",new {username="Web",password="w"}),HttpStatusCode.Unauthorized);
        await Expect(await web.PostAsJsonAsync("api/web/session",new {email="WEB@EXAMPLE.TEST",password="w"}),HttpStatusCode.OK);
        // A legacy account keeps its open session and password while supplying its missing email.
        var connection=Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION");
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync();
        await using(var command=new NpgsqlCommand("UPDATE users SET email=NULL,username='Legacy' WHERE id=$1",db))
        {command.Parameters.AddWithValue(first.Account.Id);await command.ExecuteNonQueryAsync();}
        Check((await user.GetFromJsonAsync<AccountDto>("api/me"))!.Email=="","La cuenta anterior sin correo pierde su sesión.");
        await Expect(await user.PutAsJsonAsync("api/me",new UpdateProfileRequest("legacy@example.test")),HttpStatusCode.OK);
        Check((await Post<SessionDto>("api/auth/login",new {email="legacy@example.test",password="x"})).Account.Id==first.Account.Id,"La cuenta anterior no conserva su contraseña.");
        Console.WriteLine("OK: email obligatorio y único, contraseña de un carácter, rechazo de acceso por usuario, sesiones web y transición de cuentas anteriores.");
    }

    private static async Task CheckLegacyMigration()
    {
        var connection=Environment.GetEnvironmentVariable("URBALINK_TEST_CONNECTION");
        if(new NpgsqlConnectionStringBuilder(connection).Database?.StartsWith("urbalink_posts_test_")!=true)
            throw new Exception("Solo se permiten bases temporales.");
        await using var db=new NpgsqlConnection(connection);await db.OpenAsync();
        await using var tx=await db.BeginTransactionAsync();
        await using(var setup=new NpgsqlCommand("""
            CREATE TEMP TABLE users(id uuid PRIMARY KEY,name varchar(80) NOT NULL,email varchar(254) NOT NULL UNIQUE,
                password_hash text NOT NULL,created_at timestamptz NOT NULL DEFAULT now());
            CREATE TEMP TABLE schema_versions(version integer PRIMARY KEY);
            INSERT INTO users(id,name,email,password_hash) VALUES
                ('00000000-0000-0000-0000-000000000001','Alba','alba@example.test','original-hash-1'),
                ('00000000-0000-0000-0000-000000000002','ALBA','otra@example.test','original-hash-2');
            """,db))await setup.ExecuteNonQueryAsync();
        foreach(var resource in new[]{"UsernameMigration.sql","EmailMigration.sql"})
        {
            await using var stream=typeof(EmailAuthScenarios).Assembly.GetManifestResourceStream(resource)!;
            using var reader=new StreamReader(stream);
            await using var migrate=new NpgsqlCommand(await reader.ReadToEndAsync(),db);await migrate.ExecuteNonQueryAsync();
        }
        await using var verify=new NpgsqlCommand("SELECT count(*)=2 AND bool_and(password_hash LIKE 'original-hash-%') AND count(email)=2 FROM users",db);
        if(!(bool)(await verify.ExecuteScalarAsync())!)throw new Exception("La migración no conserva las cuentas anteriores.");
        Console.WriteLine("OK: migración a email sin borrar cuentas ni contraseñas.");
        await tx.RollbackAsync();
    }
}
