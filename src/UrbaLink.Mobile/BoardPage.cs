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
    private readonly ActivityIndicator busy = new() { Color = Color.FromArgb("#176B57") };
    private long? nextBefore;
    private string? loadedCategory;
    private bool loaded;
    private bool composeOnOpen;

    public BoardPage(ApiClient api, CommunityDto community, Guid userId, Func<Task> sessionExpired,
        string? initialCategory = null, bool composeOnOpen = false)
    {
        this.api = api;
        this.community = community;
        this.userId = userId;
        this.sessionExpired = sessionExpired;
        this.composeOnOpen = composeOnOpen;
        Title = community.Name;
        BackgroundColor = Color.FromArgb("#F4F7F6");
        filter.Items.Add("Todas");
        foreach (var category in PostCategories.All) filter.Items.Add(category);
        filter.SelectedIndex = initialCategory is null ? 0 : Math.Max(0, filter.Items.IndexOf(initialCategory));
        filter.SelectedIndexChanged += async (_, _) =>
        {
            var previous = loadedCategory;
            await Run(async () =>
            {
                try { await Load(true); }
                catch
                {
                    filter.SelectedIndex = previous is null ? 0 : filter.Items.IndexOf(previous);
                    throw;
                }
            });
        };
        more.Clicked += async (_, _) => await Run(() => Load(false));
        controls.Add(Text(initialCategory ?? "Actividad vecinal", 26));
        controls.Add(Text("Comparte novedades y ayuda con tus vecinos."));
        controls.Add(MakeButton("Nueva publicación", OpenComposer));
        controls.Add(filter);
        controls.Add(MakeButton("Actualizar", () => Load(true)));
        controls.Add(posts);
        controls.Add(more);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20, Spacing = 16, Children = { controls, busy, status }
            }
        };
    }

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
        busy.IsRunning = true;
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
        finally { controls.IsEnabled = true; busy.IsRunning = false; }
    }

    private async Task Load(bool reset)
    {
        if (!reset && nextBefore is null) return;
        var category = reset ? (filter.SelectedIndex <= 0 ? null : filter.SelectedItem?.ToString()) : loadedCategory;
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
        if (reset && page.Items.Length == 0) posts.Add(Text("No hay publicaciones todavía. ¡Comparte la primera!"));
        nextBefore = page.NextBefore;
        more.IsVisible = nextBefore.HasValue;
        loaded = true;
    }

    private void AddCard(PostDto post)
    {
        var card = new VerticalStackLayout { Spacing = 10 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
        var category = Text(post.Category, 13);
        category.VerticalOptions = LayoutOptions.Center;
        header.Add(category);
        var menu = CardActions.Menu($"Opciones de {post.Title}");
        menu.Clicked += async (_, _) => await Run(async () =>
        {
            var canRemove = post.AuthorId == userId || community.Role == "Admin";
            var choice = await DisplayActionSheetAsync(post.Title, "Cancelar",
                canRemove ? "Retirar publicación" : null, "Ver comentarios / Responder");
            if (choice == "Ver comentarios / Responder")
                await Navigation.PushAsync(new CommentsPage(api, community, post.Id, userId, sessionExpired));
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
        card.Add(new Label { Text = post.Title, FontSize = 21, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#16352C") });
        card.Add(Text(post.Body));
        card.Add(Text($"{post.AuthorName} · {post.CreatedAt.LocalDateTime:g}", 12));
        var border = new Border
        {
            Padding = 18, Stroke = Color.FromArgb("#DAE5DF"), BackgroundColor = Colors.White,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 }, Content = card
        };
        posts.Add(border);
    }

    private Task OpenComposer()
    {
        var page = new ContentPage { Title = "Nueva publicación", BackgroundColor = BackgroundColor };
        var title = new Entry { Placeholder = "Título", MaxLength = 120, TextColor = Color.FromArgb("#16352C") };
        var message = new Editor { Placeholder = "¿Qué quieres compartir?", MaxLength = 4000, HeightRequest = 180, TextColor = Color.FromArgb("#16352C") };
        var category = new Picker { Title = "Categoría", TextColor = Color.FromArgb("#16352C") };
        foreach (var value in PostCategories.All.Where(c => c != "Aviso oficial" || community.Role == "Admin")) category.Items.Add(value);
        category.SelectedIndex = Math.Max(0, category.Items.IndexOf(filter.SelectedItem?.ToString() ?? "General"));
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
                    new CreatePostRequest(title.Text!, message.Text!, category.SelectedItem!.ToString()!));
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
                filter.SelectedIndex = 0;
                await Load(true);
                status.TextColor = Color.FromArgb("#176B57");
                status.Text = "¡Publicación creada!";
            });
        };
        page.Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24, Spacing = 18,
                Children = { Text(community.Name, 22), category, title, message, publish, feedback }
            }
        };
        return Navigation.PushAsync(page);
    }
}
