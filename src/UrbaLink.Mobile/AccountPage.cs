using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class AccountPage : ContentPage
{
    private readonly ApiClient api;
    private readonly string storageKey;
    private readonly NotificationBell bell;
    private string? token { get => api.Token; set => api.Token = value; }
    private AccountDto? account;
    private readonly VerticalStackLayout body = new() { Spacing = 14 };
    private readonly Grid tabs = new() { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Star) }, Padding = new Thickness(12,8), BackgroundColor = Colors.White };
    private string section = "Inicio";
    private Guid? selectedCommunity;
    private CommunityDto[] communities = [];
    private readonly Label status = new() { TextColor = Color.FromArgb("#8B3228") };
    private readonly ActivityIndicator busy = new() { Color = Color.FromArgb("#176B57") };

    public AccountPage(Uri server)
    {
        Title = "Mi comunidad";
        BackgroundColor = Color.FromArgb("#F4F7F6");
        api = new ApiClient(server);
        bell=new NotificationBell(api,()=>Run(async()=>
        {
            if(account is not null)await Navigation.PushAsync(new NotificationsPage(api,account.Id,Expired));
        })) {IsVisible=false};
        storageKey = "session_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(server.AbsoluteUri)));
        Shell.SetNavBarIsVisible(this, false);
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto),new(GridLength.Star), new(GridLength.Auto) }
        };
        var scroll = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(22,24,22,20), Spacing = 18,
                Children = { body, busy, status }
            }
        };
        var header=new Grid {Padding=new Thickness(22,8),ColumnDefinitions=new ColumnDefinitionCollection {new(GridLength.Star),new(GridLength.Auto)}};
        header.Add(new Label {Text="URBALINK",FontSize=13,CharacterSpacing=3,TextColor=Color.FromArgb("#176B57"),FontAttributes=FontAttributes.Bold,VerticalOptions=LayoutOptions.Center});
        header.Add(bell,1,0);layout.Add(header);
        layout.Add(scroll,0,1);
        layout.Add(tabs, 0, 2);
        Content = layout;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (account is not null) {bell.Start();return;}
        await Run(async () =>
        {
            try { token = await SecureStorage.Default.GetAsync(storageKey); }
            catch
            {
                ClearSession();
                ShowLogin();
                status.Text = "No se pudo leer el acceso guardado en este teléfono. Inicia sesión de nuevo.";
                return;
            }
            if (token is not null)
            {
                body.Clear();
                tabs.IsVisible = false;
                body.Add(Heading("Tu acceso está guardado"));
                body.Add(Text("Conectando con tu comunidad…"));
                body.Add(Action("Reintentar conexión", RestoreSession));
                await RestoreSession();
            }
            else ShowLogin();
        });
    }

    protected override void OnDisappearing(){bell.Stop();base.OnDisappearing();}

    private async Task RestoreSession()
    {
        account = await Send<AccountDto>(HttpMethod.Get, "api/me");
        await ShowCommunities();
    }

    private static Entry Input(string placeholder, bool password = false) => new()
    {
        Placeholder = placeholder, IsPassword = password,
        Keyboard = password ? Keyboard.Plain : InputBehavior.SentenceKeyboard,
        TextColor = Color.FromArgb("#16352C"),
        PlaceholderColor = Color.FromArgb("#52665F"), BackgroundColor = Colors.White
    };

    private Button Action(string text, Func<Task> action)
    {
        var button = new Button { Text = text, BackgroundColor = Color.FromArgb("#176B57"), TextColor = Colors.White };
        button.Clicked += async (_, _) => await Run(action);
        return button;
    }

    private static Label Text(string text) => new() { Text = text, FontSize = 17, TextColor = Color.FromArgb("#16352C") };

    private async Task Run(Func<Task> action)
    {
        if (busy.IsRunning) return;
        status.Text = "";
        status.TextColor = Color.FromArgb("#8B3228");
        body.IsEnabled = false;
        busy.IsRunning = true;
        try { await action(); }
        catch (ApiException exception)
        {
            if (exception.Status == HttpStatusCode.Unauthorized && token is not null)
            {
                ClearSession();
                ShowLogin();
                status.Text = "Tu sesión ha caducado. Vuelve a iniciar sesión.";
            }
            else status.Text = exception.Message;
        }
        catch (TaskCanceledException) { status.Text = "La operación tardó demasiado. Comprueba la conexión e inténtalo de nuevo."; }
        catch (HttpRequestException) { status.Text = "No se pudo conectar con el servidor."; }
        catch (JsonException) { status.Text = "El servidor devolvió una respuesta inesperada."; }
        catch (Exception)
        {
            status.Text = "No se pudo completar la operación. Inténtalo de nuevo.";
            if (body.Children.Count == 0) ShowLogin();
        }
        finally { body.IsEnabled = true; busy.IsRunning = false; }
    }

    private Task<T> Send<T>(HttpMethod method, string path, object? data = null) =>
        api.Send<T>(method, path, data);

    private void ShowLogin(bool register = false)
    {
        InputBehavior.CancelLogin();
        tabs.IsVisible = false;
        bell.Stop();bell.IsVisible=false;
        body.Clear();
        body.Add(Text(register ? "Crea tu cuenta" : "Bienvenido a tu comunidad"));
        var name = Input("Tu nombre");
        var email = Input("Correo electrónico");
        email.Keyboard = Keyboard.Email;
        email.AutomationId = "account-email";
        var password = Input("Contraseña (mínimo 12 caracteres)", true);
        password.AutomationId = register ? "account-new-password" : "account-password";
        if (register) body.Add(name);
        body.Add(email);
        body.Add(password);
        body.Add(new Label { Text = "Mantendremos tu sesión en este teléfono para que no tengas que escribir la contraseña cada vez.",
            FontSize = 13, TextColor = Color.FromArgb("#52665F") });
        body.Add(Action(register ? "Crear cuenta" : "Iniciar sesión", async () =>
        {
            if (string.IsNullOrWhiteSpace(email.Text) || string.IsNullOrEmpty(password.Text) ||
                (register && (string.IsNullOrWhiteSpace(name.Text) || password.Text.Length < 12)))
            {
                status.Text = "Completa los datos. Para registrarte, usa al menos 12 caracteres en la contraseña.";
                return;
            }
            var session = register
                ? await Send<SessionDto>(HttpMethod.Post, "api/auth/register",
                    new RegisterRequest(name.Text, email.Text, password.Text))
                : await Send<SessionDto>(HttpMethod.Post, "api/auth/login", new LoginRequest(email.Text, password.Text));
            await SecureStorage.Default.SetAsync(storageKey, session.Token);
            InputBehavior.CompleteLogin();
            password.Text = "";
            token = session.Token;
            account = session.Account;
            await ShowCommunities();
        }));
        body.Add(Action(register ? "Ya tengo cuenta" : "Quiero registrarme", () =>
        {
            ShowLogin(!register);
            return Task.CompletedTask;
        }));
    }

    private static Label Heading(string value, double size = 25) => new()
    {
        Text = value, FontSize = size, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#16352C")
    };

    private static Border Card(View content, string color = "#FFFFFF") => new()
    {
        Content = content, Padding = 18, BackgroundColor = Color.FromArgb(color),
        Stroke = Colors.Transparent, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 }
    };

    private void SetTabs()
    {
        tabs.Clear();
        tabs.IsVisible = true;
        bell.IsVisible=true;bell.Start();
        var labels = new[] { "Inicio", "Perfil" };
        for (var i = 0; i < labels.Length; i++)
        {
            var label = labels[i];
            var button = Action(label, async () => { section = label; await ShowCommunities(); });
            button.BackgroundColor = label == section ? Color.FromArgb("#E4F1EA") : Colors.Transparent;
            button.TextColor = label == section ? Color.FromArgb("#176B57") : Color.FromArgb("#6B7B74");
            button.FontAttributes = label == section ? FontAttributes.Bold : FontAttributes.None;
            button.CornerRadius = 16;
            tabs.Add(button, i, 0);
        }
    }

    private async Task Expired()
    {
        ClearSession();
        ShowLogin();
        while (Navigation.NavigationStack.Contains(this) && Navigation.NavigationStack.LastOrDefault() is { } top && top != this)
            await Navigation.PopAsync();
        status.Text = "Tu sesión ha caducado. Vuelve a iniciar sesión.";
    }

    private Task OpenBoard(CommunityDto community, string? category = null, bool compose = false) =>
        Navigation.PushAsync(new BoardPage(api, community, account!.Id, Expired, category, compose));

    private async Task ShowCommunities()
    {
        communities = await Send<CommunityDto[]>(HttpMethod.Get, "api/communities");
        if (selectedCommunity is null && Guid.TryParse(Preferences.Default.Get(storageKey + "_community", ""), out var saved))
            selectedCommunity = saved;
        var current = communities.FirstOrDefault(c => c.Id == selectedCommunity) ?? communities.FirstOrDefault();
        selectedCommunity = current?.Id;
        if (current is not null) Preferences.Default.Set(storageKey + "_community", current.Id.ToString());
        CommunityOverview? overview = current is not null && section != "Perfil"
            ? await Send<CommunityOverview>(HttpMethod.Get, $"api/communities/{current.Id}/overview") : null;
        body.Clear();
        SetTabs();
        if (section == "Perfil") { ShowProfile(); return; }
        body.Add(Text($"Hola, {account!.Name}"));
        if (current is null)
        {
            body.Add(Heading("Tu comunidad empieza aquí."));
            body.Add(Card(Text("Únete con una invitación o crea un espacio para tus vecinos."), "#E4F1EA"));
            body.Add(Action("Unirme a una comunidad", () => { ShowJoin(); return Task.CompletedTask; }));
            body.Add(Action("Crear una comunidad", () => { ShowCreate(); return Task.CompletedTask; }));
            return;
        }
        var selector = new Picker { Title = "Tu comunidad", ItemsSource = communities, ItemDisplayBinding = new Binding("Name"), SelectedItem = current,
            FontSize = 22, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#16352C") };
        selector.SelectedIndexChanged += async (_, _) =>
        {
            if (selector.SelectedItem is CommunityDto chosen && chosen.Id != selectedCommunity)
                await Run(async () => { selectedCommunity = chosen.Id; await ShowCommunities(); });
        };
        body.Add(selector);
        var hero = new VerticalStackLayout { Spacing = 12 };
        hero.Add(new Label { Text = "CERCA DE TI", FontSize = 11, CharacterSpacing = 2, TextColor = Color.FromArgb("#C6E8D6") });
        hero.Add(new Label { Text = "La vida en tu urba,\nun poco más fácil.", FontSize = 29, FontAttributes = FontAttributes.Bold, TextColor = Colors.White });
        hero.Add(new Label { Text = $"{overview!.MemberCount} vecinos · {overview.ActivePosts} publicaciones", FontSize = 14, TextColor = Color.FromArgb("#C6E8D6") });
        var publish = Action("＋  Compartir con mis vecinos", () => OpenBoard(current, null, true));
        publish.BackgroundColor = Color.FromArgb("#D6F394");
        publish.TextColor = Color.FromArgb("#16352C");
        publish.CornerRadius = 14;
        hero.Add(publish);
        body.Add(Card(hero, "#174B3D"));
        body.Add(Heading("Tu día a día", 21));
        AddNeeds(current, overview);
        if (overview.Notice is { } notice)
        {
            var content = new VerticalStackLayout { Spacing = 8 };
            content.Add(new Label { Text = "AVISO DE TU COMUNIDAD", FontSize = 11, CharacterSpacing = 1, TextColor = Color.FromArgb("#80621F") });
            content.Add(Heading(notice.Title, 19));
            content.Add(new Label { Text = notice.Body, MaxLines = 3, LineBreakMode = LineBreakMode.TailTruncation, TextColor = Color.FromArgb("#514B36") });
            content.Add(Action("Leer aviso", () => Navigation.PushAsync(new CommentsPage(api,current,notice.Id,account.Id,Expired))));
            body.Add(Card(content,"#FBF1D9"));
        }
        body.Add(Heading("Lo último cerca de ti", 21));
        if (overview.Recent.Length == 0) body.Add(Text("Aún no hay novedades. Comparte la primera con tus vecinos."));
        foreach (var post in overview.Recent)
        {
            var content = new VerticalStackLayout { Spacing = 8 };
            content.Add(new Label { Text = $"{PostCategories.Label(post.Category).ToUpperInvariant()}  ·  {post.AuthorName}", FontSize = 11, TextColor = Color.FromArgb("#688075") });
            content.Add(Heading(post.Title, 19));
            content.Add(new Label { Text = post.Body, MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation, TextColor = Color.FromArgb("#52665F") });
            content.Add(Action(post.Category == "Ofrezco" ? "Ver servicio · Contacto privado  →" : "Ver conversación  →", () => Navigation.PushAsync(post.Category == "Ofrezco" ? new ServicePage(api,current,post.Id,account.Id,Expired) : new CommentsPage(api,current,post.Id,account.Id,Expired))));
            body.Add(Card(content));
        }
        body.Add(Action("Ver toda la actividad", () => OpenBoard(current)));
        body.Add(Action("Actualizar inicio", ShowCommunities));
    }

    private void AddNeeds(CommunityDto current, CommunityOverview overview)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star),new(GridLength.Star) },
            RowDefinitions = new RowDefinitionCollection { new(GridLength.Auto),new(GridLength.Auto) }, ColumnSpacing = 12, RowSpacing = 12 };
        var choices = new[] {
            ("Ayuda y servicios", "Solicitudes, servicios y recomendaciones", PostCategories.HelpGroup, "#E4F1EA"),
            ("Objetos", $"{overview.SharedItems} objetos · venta, alquiler y pujas", "Material", "#F8EDDC"),
            ("Ir juntos", $"{overview.Routes} trayectos publicados", "Coche", "#E4EDF7"),
            ("Animales", "Perdidos y encontrados", "Animales", "#E4F1EA")
        };
        for (var i=0; i<choices.Length; i++)
        {
            var choice=choices[i];
            var content=new VerticalStackLayout { Spacing=7 };
            content.Add(Heading(choice.Item1,18));
            content.Add(new Label { Text=choice.Item2, FontSize=12, TextColor=Color.FromArgb("#52665F") });
            var open=Action("Explorar  →", () => choice.Item3 == "Animales" ? Navigation.PushAsync(new AnimalsPage(api,current,account!.Id,Expired)) : choice.Item3 == "Material" ? Navigation.PushAsync(new MarketPage(api, current, account!.Id, Expired)) : choice.Item3 == "Coche" ? Navigation.PushAsync(new RidePage(api,current,account!.Id,Expired)) : OpenBoard(current,choice.Item3));
            open.BackgroundColor=Colors.Transparent;
            open.TextColor=Color.FromArgb("#174B3D");
            open.Padding=0;
            content.Add(open);
            grid.Add(Card(content,choice.Item4),i%2,i/2);
        }
        body.Add(grid);
    }

    private void ShowProfile()
    {
        body.Add(Heading("Tu perfil"));
        body.Add(Card(new VerticalStackLayout { Spacing=8, Children={ Heading(account!.Name,22),Text(account.Email) } }));
        body.Add(Heading("Tus comunidades",21));
        foreach (var community in communities)
        {
            var content = new VerticalStackLayout { Spacing=10 };
            content.Add(Heading(community.Name,20));
            content.Add(Text(community.Role == "Admin" ? "Administrador" : "Vecino"));
            content.Add(Action(community.Role == "Admin" ? "Administrar comunidad" : "Ir al inicio", async () =>
            {
                if (community.Role == "Admin") ShowManage(community);
                else { selectedCommunity=community.Id; section="Inicio"; await ShowCommunities(); }
            }));
            body.Add(Card(content));
        }
        body.Add(Action("Unirme con una invitación", () => { ShowJoin(); return Task.CompletedTask; }));
        body.Add(Action("Crear comunidad", () => { ShowCreate(); return Task.CompletedTask; }));
        body.Add(Action("Papelera de comunidades", ShowTrash));
        body.Add(Action("Cerrar sesión", async () => {
            if (!await DisplayAlertAsync("Cerrar sesión", "Para volver a entrar necesitarás tu contraseña. ¿Quieres cerrar sesión en este teléfono?", "Cerrar sesión", "Cancelar")) return;
            await Send<object>(HttpMethod.Post,"api/auth/logout");
            ClearSession(); ShowLogin();
        }));
    }

    private void ShowJoin()
    {
        body.Clear(); body.Add(Heading("Únete a tus vecinos"));
        body.Add(Text("Introduce el código que te ha enviado un administrador."));
        var code=Input("Código de invitación");
        code.Keyboard = Keyboard.Plain;
        body.Add(code);
        body.Add(Action("Unirme",async () => {
            var joined=await Send<CommunityDto>(HttpMethod.Post,"api/communities/join",new JoinRequest(code.Text ?? ""));
            selectedCommunity=joined.Id; section="Inicio"; await ShowCommunities();
            status.TextColor=Color.FromArgb("#176B57"); status.Text=$"¡Bienvenido a {joined.Name}!";
        }));
        body.Add(Action("Volver",ShowCommunities));
    }

    private void ShowCreate()
    {
        body.Clear(); body.Add(Heading("Un lugar para tu urba"));
        body.Add(Text("Crearás una nueva comunidad y serás su administrador."));
        var name=Input("Nombre de la comunidad");
        body.Add(name);
        body.Add(Action("Crear comunidad",async () => {
            var created=await Send<CommunityDto>(HttpMethod.Post,"api/communities",new CreateCommunityRequest(name.Text ?? ""));
            selectedCommunity=created.Id; section="Inicio"; await ShowCommunities();
        }));
        body.Add(Action("Volver",ShowCommunities));
    }

    private void ShowManage(CommunityDto community)
    {
        body.Clear(); body.Add(Heading(community.Name)); body.Add(Text("Administración de la comunidad"));
        body.Add(Action("Invitar vecinos", async () => {
            var invitation=await Send<InvitationDto>(HttpMethod.Post,$"api/communities/{community.Id}/invitations");
            body.Add(Card(new VerticalStackLayout { Spacing=10, Children={
                Text($"Un solo uso · hasta {invitation.ExpiresAt.LocalDateTime:g}"),
                new Editor { Text=invitation.Code, IsReadOnly=true, AutoSize=EditorAutoSizeOption.TextChanges,TextColor=Color.FromArgb("#16352C") },
                Action("Copiar invitación",async () => { await Clipboard.Default.SetTextAsync(invitation.Code); status.TextColor=Color.FromArgb("#176B57"); status.Text="Código copiado."; })
            }}));
        }));
        body.Add(Card(Text("Eliminar la comunidad la envía a la papelera. Sus vecinos perderán el acceso y los códigos pendientes quedarán invalidados. Puedes restaurarla después."),"#FBE9E5"));
        var remove=Action("Eliminar comunidad",async () => {
            var typed=await DisplayPromptAsync("Eliminar comunidad",$"Escribe exactamente «{community.Name}» para enviarla a la papelera.","Eliminar","Cancelar");
            if (typed is null) return;
            if (typed != community.Name) { status.Text="El nombre no coincide. No se ha eliminado nada."; return; }
            await Send<object>(HttpMethod.Post,$"api/communities/{community.Id}/delete",new DeleteCommunityRequest(typed));
            await ShowCommunities(); status.TextColor=Color.FromArgb("#176B57"); status.Text="Comunidad enviada a la papelera.";
        });
        remove.BackgroundColor=Color.FromArgb("#AA4436");
        body.Add(remove);
        body.Add(Action("Volver al perfil",ShowCommunities));
    }

    private async Task ShowTrash()
    {
        var deleted=await Send<CommunityDto[]>(HttpMethod.Get,"api/communities/trash");
        body.Clear(); body.Add(Heading("Papelera"));
        body.Add(Text("Solo ves las comunidades eliminadas que administras."));
        if (deleted.Length==0) body.Add(Text("No hay comunidades en la papelera."));
        foreach (var community in deleted)
        {
            body.Add(Card(new VerticalStackLayout { Spacing=10,Children={
                Heading(community.Name,20),
                Action("Restaurar",async () => {
                    if (!await DisplayAlertAsync("Restaurar comunidad",$"Los miembros volverán a acceder a {community.Name}. Los códigos antiguos seguirán invalidados.","Restaurar","Cancelar")) return;
                    await Send<object>(HttpMethod.Post,$"api/communities/{community.Id}/restore");
                    await ShowTrash(); status.TextColor=Color.FromArgb("#176B57"); status.Text="Comunidad restaurada.";
                })
            }}));
        }
        body.Add(Action("Volver al perfil",ShowCommunities));
    }
    private void ClearSession()
    {
        SecureStorage.Default.Remove(storageKey);
        token = null;
        account = null;
        selectedCommunity = null;
        section = "Inicio";
        tabs.IsVisible = false;
    }

}
