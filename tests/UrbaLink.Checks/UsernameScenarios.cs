using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using UrbaLink.Contracts;
using Npgsql;

internal static class UsernameScenarios
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
        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",new {name=" ",password="x"}),HttpStatusCode.BadRequest);
        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",new {name="A",password=""}),HttpStatusCode.BadRequest);
        var first=await Post<SessionDto>("api/auth/register",new {name="A",password="x"});
        Check(first.Account.Username=="A"&&first.Account.Email=="","El registro sin correo falla.");
        var second=await Post<SessionDto>("api/auth/register",new {name="B",password="1"});
        await Expect(await anonymous.PostAsJsonAsync("api/auth/register",new {name=" a ",password="y"}),HttpStatusCode.Conflict);
        var race=await Task.WhenAll(anonymous.PostAsJsonAsync("api/auth/register",new {name="Vecino único",password="a"}),
            anonymous.PostAsJsonAsync("api/auth/register",new {name="VECINO ÚNICO",password="b"}));
        try{Check(race.Count(r=>r.StatusCode==HttpStatusCode.OK)==1&&race.Count(r=>r.StatusCode==HttpStatusCode.Conflict)==1,"Se repiten nombres en registros simultáneos.");}
        finally{foreach(var response in race)response.Dispose();}
        var login=await Post<SessionDto>("api/auth/login",new {username=" a ",password="x"});
        Check(login.Account.Id==first.Account.Id,"El usuario no permite normalización de mayúsculas.");
        await Expect(await anonymous.PostAsJsonAsync("api/auth/login",new {username="A",password="z"}),HttpStatusCode.Unauthorized);
        using var user=new HttpClient{BaseAddress=new Uri(url)};
        user.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",first.Token);
        using(var r=await user.PutAsJsonAsync("api/me",new UpdateProfileRequest(" Vecino@Example.test ")))
        {
            Check(r.IsSuccessStatusCode,"No permite añadir correo en el perfil.");
            var profile=(await r.Content.ReadFromJsonAsync<AccountDto>())!;
            Check(profile.Email=="vecino@example.test"&&profile.Username=="A","El perfil cambia el usuario o no normaliza el correo.");
        }
        Check((await Post<SessionDto>("api/auth/login",new LoginRequest("VECINO@example.test","x"))).Account.Id==first.Account.Id,"Se rompe el acceso por correo anterior.");
        using var other=new HttpClient{BaseAddress=new Uri(url)};
        other.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",second.Token);
        await Expect(await other.PutAsJsonAsync("api/me",new UpdateProfileRequest("vecino@example.test")),HttpStatusCode.Conflict);
        await Expect(await user.PutAsJsonAsync("api/me",new UpdateProfileRequest("invalid")),HttpStatusCode.BadRequest);
        await Expect(await user.PutAsJsonAsync("api/me",new UpdateProfileRequest(null)),HttpStatusCode.OK);
        Check((await user.GetFromJsonAsync<AccountDto>("api/me"))!.Email=="","No permite retirar el correo opcional.");
        Check((await Post<SessionDto>("api/auth/login",new {username="A",password="x"})).Account.Id==first.Account.Id,"Retirar correo rompe el acceso por usuario.");
        using var web=new HttpClient(new HttpClientHandler{CookieContainer=new CookieContainer()}){BaseAddress=new Uri(url)};
        web.DefaultRequestHeaders.Add("X-UrbaLink-Web","1");
        await Expect(await web.PostAsJsonAsync("api/web/register",new {name="Web sin correo",password="w"}),HttpStatusCode.OK);
        Check((await web.GetFromJsonAsync<AccountDto>("api/me"))!.Username=="Web sin correo","No se guarda la sesión web sin correo.");
        await Expect(await web.PostAsJsonAsync("api/auth/logout",new {}),HttpStatusCode.NoContent);
        await Expect(await web.PostAsJsonAsync("api/web/session",new {username="WEB SIN CORREO",password="w"}),HttpStatusCode.OK);
        Console.WriteLine("OK: usuarios únicos, registros concurrentes, contraseña de un carácter, correo opcional en perfil, sesiones web y acceso anterior por correo.");
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
                ('00000000-0000-0000-0000-000000000002','ALBA','otra@example.test','original-hash-2'),
                ('00000000-0000-0000-0000-000000000003','ALBA-00000000000000000000000000000002-1','tercera@example.test','original-hash-3');
            """,db))await setup.ExecuteNonQueryAsync();
        await using var stream=typeof(UsernameScenarios).Assembly.GetManifestResourceStream("UsernameMigration.sql")!;
        using var reader=new StreamReader(stream);
        await using(var migrate=new NpgsqlCommand(await reader.ReadToEndAsync(),db))await migrate.ExecuteNonQueryAsync();
        await using var verify=new NpgsqlCommand("""
            SELECT count(*)=3 AND count(DISTINCT lower(username))=3 AND bool_and(password_hash LIKE 'original-hash-%')
                AND count(email)=3 FROM users
            """,db);
        if(!(bool)(await verify.ExecuteScalarAsync())!)throw new Exception("La migración no conserva cuentas anteriores o repite usuarios.");
        Console.WriteLine("OK: migración de cuentas anteriores, nombres repetidos y contraseñas conservadas.");
        await tx.RollbackAsync();
    }
}
