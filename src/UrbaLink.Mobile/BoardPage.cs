using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class BoardPage : ContentPage
{
    private readonly ApiClient api;
    private readonly CommunityDto community;
    private readonly Guid userId;
    private readonly Func<Task> sessionExpired;
    private readonly VerticalStackLayout posts = new() { Spacing = 16 };
    private readonly VerticalStackLayout controls = new() { Spacing = 16 };
    private readonly Label status = Text("");
    private readonly Picker filter = new() { Title = "Categoría", TextColor = Color.FromArgb("#16352C") };
    private readonly Button more = new() { Text = "Ver publicaciones anteriores", IsVisible = false };
    private readonly ActivityIndicator busy = new() { IsVisible = false, Color = Color.FromArgb("#176B57") };
    private long? nextBefore;
    private string? loadedCategory;
    private bool loaded;
    private readonly List<Button> filterButtons = [];
    private readonly bool helpGroup;
    private bool composeOnOpen;

    public BoardPage(ApiClient api, CommunityDto community, Guid userId, Func<Task> sessionExpired,
        string? initialCategory = null, bool composeOnOpen = false)
    {
        this.api = api;
        status.IsVisible = false;
        helpGroup = initialCategory == PostCategories.HelpGroup;
        this.community = community;
        this.userId = userId;
        this.sessionExpired = sessionExpired;
        this.composeOnOpen = composeOnOpen;
        Title = community.Name;
        BackgroundColor = Color.FromArgb("#F4F7F6");
        filter.Items.Add("Todas");
        foreach (var category in helpGroup ? PostCategories.Help : PostCategories.All) filter.Items.Add(PostCategories.Label(category));
        filter.SelectedIndex = initialCategory is null ? 0 : Math.Max(0, filter.Items.IndexOf(PostCategories.Label(initialCategory)));
        filter.SelectedIndexChanged += async (_, _) =>
        {
            UpdateFilterButtons();
            var previous = loadedCategory;
            await Run(async () =>
            {
                try { await Load(true); }
                catch
                {
                    filter.SelectedIndex = previous is null ? 0 : Math.Max(0, filter.Items.IndexOf(PostCategories.Label(previous)));
                    throw;
                }
            });
        };
        more.Clicked += async (_, _) => await Run(() => Load(false));
        controls.Add(Text(initialCategory is null ? "Actividad vecinal" : PostCategories.Label(initialCategory), 26));
        controls.Add(Text(helpGroup ? "Pide ayuda, ofrece un servicio o recomienda a alguien. Filtra por lo que buscas." : "Comparte novedades y ayuda con tus vecinos."));
        controls.Add(MakeButton("Nueva publicación", OpenComposer));
        if (helpGroup)
        {
            var chips = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, Direction = Microsoft.Maui.Layouts.FlexDirection.Row };
            for (var i = 0; i < filter.Items.Count; i++)
            {
                var index = i;
                var chip = new Button { Text = filter.Items[i], FontSize = 14, CornerRadius = 22,
                    Padding = new Thickness(14, 8), Margin = new Thickness(0, 0, 8, 8), MinimumHeightRequest = 44 };
                chip.Clicked += (_, _) => { if (!busy.IsRunning) filter.SelectedIndex = index; };
                filterButtons.Add(chip);
                chips.Add(chip);
            }
            UpdateFilterButtons();
            controls.Add(chips);
        }
        else controls.Add(filter);
        ToolbarItems.Add(new ToolbarItem { Text = "Actualizar", Command = new Command(async () => await Run(() => Load(true))) });
        controls.Add(posts);
        controls.Add(more);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20, Spacing = 16, Children = { busy, status, controls }
            }
        };
    }

    private void UpdateFilterButtons()
    {
        for (var i = 0; i < filterButtons.Count; i++)
        {
            var selected = i == filter.SelectedIndex;
            var chip = filterButtons[i];
            chip.BackgroundColor = Color.FromArgb(selected ? "#176B57" : "#E4F1EA");
            chip.TextColor = selected ? Colors.White : Color.FromArgb("#174B3D");
            chip.FontAttributes = selected ? FontAttributes.Bold : FontAttributes.None;
            SemanticProperties.SetDescription(chip, chip.Text + (selected ? ", filtro seleccionado" : ", filtrar publicaciones"));
        }
    }

    private Task OpenPost(PostDto post) => Navigation.PushAsync(post.Category == "Ofrezco"
        ? new ServicePage(api, community, post.Id, userId, sessionExpired)
        : new CommentsPage(api, community, post.Id, userId, sessionExpired));

    private string Path => $"api/communities/{community.Id}/posts";
    private static Label Text(string text, double size = 16) => new()
    {
        Text = text, FontSize = size, TextColor = Color.FromArgb("#16352C"), LineBreakMode = LineBreakMode.WordWrap
    };

    private Button MakeButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, BackgroundColor = Color.FromArgb("#176B57"), TextColor = Colors.White };
        button.Clicked += async (_, _) => await Run(action);
        return button;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!loaded) await Run(() => Load(true));
        if (loaded && composeOnOpen)
        {
            composeOnOpen = false;
            await Run(OpenComposer);
        }
    }

    private async Task Run(Func<Task> action)
    {
        if (busy.IsRunning) return;
        controls.IsEnabled = false;
        busy.IsRunning = busy.IsVisible = true;
        status.IsVisible = false;
        status.Text = "";
        status.TextColor = Color.FromArgb("#8B3228");
        try { await action(); }
        catch (ApiException exception)
        {
            if (exception.Status == HttpStatusCode.Unauthorized) await sessionExpired();
            else status.Text = exception.Message;
        }
        catch (TaskCanceledException) { status.Text = "La operación tardó demasiado. Puedes volver a intentarlo."; }
        catch (HttpRequestException) { status.Text = "No se pudo conectar. Comprueba la conexión y actualiza."; }
        catch (JsonException) { status.Text = "El servidor devolvió una respuesta inesperada."; }
        finally { controls.IsEnabled = true; busy.IsRunning = busy.IsVisible = false; status.IsVisible = !string.IsNullOrWhiteSpace(status.Text); }
    }

    private async Task Load(bool reset)
    {
        if (!reset && nextBefore is null) return;
        var category = reset ? (filter.SelectedIndex <= 0 ? (helpGroup ? PostCategories.HelpGroup : null) : PostCategories.Key(filter.SelectedItem!.ToString()!)) : loadedCategory;
        var query = new List<string>();
        if (category is not null) query.Add("category=" + Uri.EscapeDataString(category));
        if (!reset) query.Add("before=" + nextBefore);
        var page = await api.Send<PostPage>(HttpMethod.Get, Path + (query.Count == 0 ? "" : "?" + string.Join("&", query)));
        if (reset)
        {
            posts.Clear();
            loadedCategory = category;
        }
        foreach (var post in page.Items) AddCard(post);
        if (reset && page.Items.Length == 0)
        {
            var empty = category switch
            {
                "Ayuda" => "Todavía no hay solicitudes. Cuenta qué necesitas y deja que tus vecinos te ayuden.",
                "Ofrezco" => "Todavía no hay servicios. Comparte tus clases, tu oficio o tu negocio.",
                "Recomendación" => "Todavía no hay recomendaciones. Comparte una buena experiencia con un profesional.",
                _ => "No hay publicaciones todavía. ¡Comparte la primera!"
            };
            posts.Add(Text(empty));
        }
        nextBefore = page.NextBefore;
        more.IsVisible = nextBefore.HasValue;
        loaded = true;
    }

    private void AddCard(PostDto post)
    {
        var card = new VerticalStackLayout { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
        var category = Text(PostCategories.Label(post.Category) + (post.Category == "Ofrezco" ? " · Contacto privado" : ""), 13);
        category.VerticalOptions = LayoutOptions.Center;
        header.Add(category);
        var menu = CardActions.Menu($"Opciones de {post.Title}");
        menu.Clicked += async (_, _) => await Run(async () =>
        {
            var canRemove = post.AuthorId == userId || community.Role == "Admin";
            var action = post.Category == "Ofrezco" ? "Ver servicio / Mensajes privados" : "Ver comentarios / Responder";
            var choice = await DisplayActionSheetAsync(post.Title, "Cancelar",
                canRemove ? "Retirar publicación" : null, action);
            if (choice == action)
                await OpenPost(post);
            else if (canRemove && choice == "Retirar publicación")
            {
                if (!await DisplayAlertAsync("Retirar publicación", $"¿Quieres retirar «{post.Title}» del tablón?", "Retirar", "Cancelar")) return;
                await api.Send<object>(HttpMethod.Delete, $"{Path}/{post.Id}");
                posts.Remove(card.Parent as View ?? card);
                // A refresh failure must not leave a removed card actionable.
                await Load(true);
                status.TextColor = Color.FromArgb("#176B57");
                status.Text = "Publicación retirada.";
            }
        });
        header.Add(menu, 1, 0);
        card.Add(header);
        var summary = new VerticalStackLayout { Spacing = 10 };
        var title = new Button { Text = post.Title, FontSize = 21, FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#16352C"), BackgroundColor = Colors.Transparent, Padding = 0,
            HorizontalOptions = LayoutOptions.Start, LineBreakMode = LineBreakMode.WordWrap, MinimumHeightRequest = 44 };
        SemanticProperties.SetHint(title, post.Category == "Ofrezco" ? "Abre el servicio y su contacto privado" : "Abre la publicación y sus comentarios");
        title.Clicked += async (_, _) => await Run(() => OpenPost(post));
        card.Add(title);
        var preview = Text(post.Body);
        preview.MaxLines = 3;
        preview.LineBreakMode = LineBreakMode.TailTruncation;
        summary.Add(preview);
        summary.Add(Text($"{post.AuthorName} · {post.CreatedAt.LocalDateTime:g}", 12));
        summary.Add(Text(post.Category == "Ofrezco" ? "Ver servicio  →" : "Leer publicación  →", 13));
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) => await Run(() => OpenPost(post));
        summary.GestureRecognizers.Add(tap);
        card.Add(summary);
        var border = new Border
        {
            Padding = 18, Stroke = Color.FromArgb("#DAE5DF"), BackgroundColor = Colors.White,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }, Content = card
        };
        posts.Add(border);
    }

    private Task OpenComposer()
    {
        if(filter.SelectedItem?.ToString()=="Coche")return Navigation.PushAsync(new RidePage(api,community,userId,sessionExpired,true));
        var page = new ContentPage { Title = "Nueva publicación", BackgroundColor = BackgroundColor };
        var title = new Entry { Placeholder = "Título", MaxLength = 120, TextColor = Color.FromArgb("#16352C"), PlaceholderColor = Color.FromArgb("#52665F") };
        var message = new Editor { Placeholder = "¿Qué quieres compartir?", MaxLength = 4000, HeightRequest = 180, TextColor = Color.FromArgb("#16352C"), PlaceholderColor = Color.FromArgb("#52665F") };
        var category = new Picker { Title = "Categoría", TextColor = Color.FromArgb("#16352C") };
        foreach (var value in (helpGroup ? PostCategories.Help : PostCategories.All).Where(c => c != "Coche" && (c != "Aviso oficial" || community.Role == "Admin"))) category.Items.Add(PostCategories.Label(value));
        category.SelectedIndex = Math.Max(0, category.Items.IndexOf(filter.SelectedItem?.ToString() ?? "General"));
        var guidance = Text("");
        void Explain() => guidance.Text = PostCategories.Key(category.SelectedItem?.ToString() ?? "") switch
        {
            "Ayuda" => "Pide lo que necesitas, por ejemplo clases o un profesional. Las respuestas son visibles para la comunidad.",
            "Ofrezco" => "Ofrece clases, tu oficio o los servicios de tu negocio. Cada interesado te escribirá en privado.",
            "Recomendación" => "Recomienda a un tercero por tu experiencia, por ejemplo un buen fontanero. Los comentarios son visibles para la comunidad.",
            _ => "Los comentarios son visibles para la comunidad."
        };
        category.SelectedIndexChanged += (_, _) => Explain();
        Explain();
        var feedback = Text("");
        var publish = new Button { Text = "Publicar", BackgroundColor = Color.FromArgb("#176B57"), TextColor = Colors.White };
        var submitting = false;
        publish.Clicked += async (_, _) =>
        {
            if (submitting) return;
            if ((title.Text?.Trim().Length ?? 0) < 3 || string.IsNullOrWhiteSpace(message.Text))
            {
                feedback.Text = "Escribe un título de al menos 3 caracteres y un mensaje.";
                return;
            }
            submitting = true;
            publish.IsEnabled = false;
            feedback.Text = "Publicando…";
            try
            {
                await api.Send<PostDto>(HttpMethod.Post, Path,
                    new CreatePostRequest(title.Text!, message.Text!, PostCategories.Key(category.SelectedItem!.ToString()!)));
            }
            catch (ApiException exception)
            {
                feedback.Text = exception.Status == HttpStatusCode.Unauthorized
                    ? "Tu sesión ha caducado. Vuelve al tablón e inicia sesión de nuevo."
                    : exception.Message;
                submitting = false;
                publish.IsEnabled = true;
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            {
                feedback.Text = "No se pudo confirmar la publicación. Revisa el tablón antes de reenviarla.";
                submitting = false;
                publish.IsEnabled = true;
                return;
            }
            // Do not offer to submit again if only the subsequent refresh fails.
            feedback.Text = "Publicación creada.";
            if (Navigation.NavigationStack.LastOrDefault() == page)
                await Navigation.PopAsync();
            if (!Navigation.NavigationStack.Contains(this)) return;
            await Run(async () =>
            {
                filter.SelectedIndex = Math.Max(0, filter.Items.IndexOf(category.SelectedItem!.ToString()!));
                await Load(true);
                status.TextColor = Color.FromArgb("#176B57");
                status.Text = "¡Publicación creada!";
            });
        };
        var rideShortcut = MakeButton("Ofrecer un trayecto en coche", () => Navigation.PushAsync(new RidePage(api,community,userId,sessionExpired,true)));
        rideShortcut.IsVisible = !helpGroup;
        page.Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 18,
                Children = { Text(community.Name, 22), category, guidance, title, message, publish, feedback, rideShortcut }
            }
        };
        return Navigation.PushAsync(page);
    }
}
