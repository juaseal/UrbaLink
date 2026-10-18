using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class CommentsPage : ContentPage
{
    private readonly ApiClient api;
    private readonly CommunityDto community;
    private readonly long postId;
    private readonly Guid userId;
    private readonly Func<Task> sessionExpired;
    private readonly VerticalStackLayout controls = new() { Spacing = 16 };
    private readonly VerticalStackLayout comments = new() { Spacing = 12 };
    private readonly Label title = Text("", 24);
    private readonly Label postBody = Text("");
    private readonly Label author = Text("", 13);
    private readonly Label status = Text("");
    private readonly Editor draft = new()
    {
        Placeholder = "Escribe una respuesta…", MaxLength = 2000, HeightRequest = 110,
        TextColor = Color.FromArgb("#16352C"), BackgroundColor = Colors.White
    };
    private readonly Button send = new() { Text = "Enviar comentario", IsEnabled = false };
    private readonly Button more = new() { Text = "Ver comentarios anteriores", IsVisible = false };
    private readonly ActivityIndicator busy = new() { Color = Color.FromArgb("#176B57") };
    private Guid requestId = Guid.NewGuid();
    private long? nextBefore;
    private bool available;

    public CommentsPage(ApiClient api, CommunityDto community, long postId, Guid userId, Func<Task> sessionExpired)
    {
        this.api = api;
        this.community = community;
        this.postId = postId;
        this.userId = userId;
        this.sessionExpired = sessionExpired;
        Title = "Comentarios";
        ToolbarItems.Add(new ToolbarItem{Text="Denunciar publicación",Command=new Command(async()=>await Navigation.PushAsync(new ReportPage(api,community.Id,"Post",postId,sessionExpired)))});
        BackgroundColor = Color.FromArgb("#F4F7F6");
        draft.TextChanged += (_, _) => requestId = Guid.NewGuid();
        send.Clicked += async (_, _) => await Run(SendComment);
        more.Clicked += async (_, _) => await Run(() => Load(false));
        var refresh = new Button { Text = "Actualizar conversación" };
        refresh.Clicked += async (_, _) => await Run(() => Load(true));
        controls.Add(title);
        controls.Add(postBody);
        controls.Add(author);
        controls.Add(draft);
        controls.Add(send);
        controls.Add(refresh);
        controls.Add(Text("Respuestas · más recientes primero", 18));
        controls.Add(comments);
        controls.Add(more);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout { Padding = 20, Spacing = 16, Children = { controls, busy, status } }
        };
    }

    private string Path => $"api/communities/{community.Id}/posts/{postId}/comments";
    private static Label Text(string value, double size = 16) => new()
    {
        Text = value, FontSize = size, TextColor = Color.FromArgb("#16352C"), LineBreakMode = LineBreakMode.WordWrap
    };

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Run(() => Load(true));
    }

    private async Task Run(Func<Task> action)
    {
        if (busy.IsRunning) return;
        busy.IsRunning = true;
        controls.IsEnabled = false;
        status.Text = "";
        status.TextColor = Color.FromArgb("#8B3228");
        try { await action(); }
        catch (ApiException exception)
        {
            if (exception.Status == HttpStatusCode.Unauthorized) await sessionExpired();
            else
            {
                status.Text = exception.Message;
                if (exception.Status == HttpStatusCode.NotFound)
                {
                    // A missing comment can also return 404. Revalidate the parent before hiding it.
                    try { await Load(true); }
                    catch (ApiException parent) when (parent.Status == HttpStatusCode.NotFound)
                    {
                        available = false;
                        comments.Clear();
                        title.Text = "Publicación no disponible";
                        postBody.Text = "";
                        author.Text = "";
                        more.IsVisible = false;
                    }
                    catch (Exception retry) when (retry is ApiException or HttpRequestException or TaskCanceledException or JsonException)
                    {
                        available = false;
                    }
                }
            }
        }
        catch (TaskCanceledException) { status.Text = "Se agotó el tiempo de espera. Puedes reintentar el mismo comentario sin duplicarlo."; }
        catch (HttpRequestException) { status.Text = "No hay conexión con el servidor. Conservamos tu texto para reintentarlo."; }
        catch (JsonException) { status.Text = "Respuesta inesperada del servidor. Actualiza o reintenta."; }
        finally
        {
            busy.IsRunning = false;
            controls.IsEnabled = true;
            send.IsEnabled = available;
        }
    }

    private async Task Load(bool reset)
    {
        if (!reset && nextBefore is null) return;
        var page = await api.Send<CommentPage>(HttpMethod.Get, Path + (reset ? "" : $"?before={nextBefore}"));
        title.Text = page.Post.Title;
        postBody.Text = page.Post.Body;
        author.Text = $"{page.Post.AuthorName} · {page.Post.CreatedAt.LocalDateTime:g}";
        if (reset) comments.Clear();
        foreach (var item in page.Items) AddComment(item);
        if (reset && page.Items.Length == 0) comments.Add(Text("Aún no hay comentarios. Sé el primero en responder."));
        nextBefore = page.NextBefore;
        more.IsVisible = nextBefore.HasValue;
        available = true;
    }

    private async Task SendComment()
    {
        var body = draft.Text?.Trim() ?? "";
        if (body.Length == 0)
        {
            status.Text = "Escribe un comentario antes de enviarlo.";
            return;
        }
        await api.Send<CommentDto>(HttpMethod.Post, Path, new CreateCommentRequest(body, requestId));
        draft.Text = "";
        requestId = Guid.NewGuid();
        try
        {
            await Load(true);
            status.TextColor = Color.FromArgb("#176B57");
            status.Text = "¡Comentario publicado!";
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException or JsonException)
        {
            status.Text = "Comentario publicado. No se pudo actualizar la conversación; pulsa Actualizar.";
        }
    }

    private void AddComment(CommentDto item)
    {
        var content = new VerticalStackLayout { Spacing = 8 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitionCollection { new(GridLength.Star), new(GridLength.Auto) } };
        var name = Text(item.AuthorName, 17);
        name.VerticalOptions = LayoutOptions.Center;
        header.Add(name);
        content.Add(header);
        content.Add(Text(item.Body));
        var report=new Button{Text="Denunciar comentario"}; report.Clicked+=async(_,_)=>await Navigation.PushAsync(new ReportPage(api,community.Id,"Comment",item.Id,sessionExpired));content.Add(report);
        content.Add(Text(item.CreatedAt.LocalDateTime.ToString("g"), 12));
        var card = new Border
        {
            Padding = 16, Stroke = Color.FromArgb("#DAE5DF"), BackgroundColor = Colors.White,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 }, Content = content
        };
        if (item.AuthorId == userId || community.Role == "Admin")
        {
            var remove = CardActions.Menu($"Opciones del comentario de {item.AuthorName}");
            remove.Clicked += async (_, _) => await Run(async () =>
            {
                var choice = await DisplayActionSheetAsync("Comentario", "Cancelar", "Retirar comentario");
                if (choice != "Retirar comentario") return;
                if (!await DisplayAlertAsync("Retirar comentario", "El comentario dejará de verse en esta publicación.", "Retirar", "Cancelar")) return;
                await api.Send<object>(HttpMethod.Delete, $"{Path}/{item.Id}");
                comments.Remove(card);
                await Load(true);
                status.TextColor = Color.FromArgb("#176B57");
                status.Text = "Comentario retirado.";
            });
            header.Add(remove, 1, 0);
        }
        comments.Add(card);
    }
}
