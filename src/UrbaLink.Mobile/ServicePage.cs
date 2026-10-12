using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class ServicePage : ContentPage
{
    private readonly ApiClient api;
    private readonly CommunityDto community;
    private readonly long postId;
    private readonly Guid userId;
    private readonly Func<Task> expired;
    private readonly VerticalStackLayout body = new() { Padding = 20, Spacing = 16 };
    private readonly Label status = new() { TextColor = Color.FromArgb("#8B3228") };
    private bool running;
    private string Path => $"api/communities/{community.Id}/services/{postId}";

    public ServicePage(ApiClient api, CommunityDto community, long postId, Guid userId, Func<Task> expired)
    {
        this.api = api; this.community = community; this.postId = postId; this.userId = userId; this.expired = expired;
        Title = "Servicio";
        BackgroundColor = Color.FromArgb("#F4F7F6");
        Content = new ScrollView { Content = new VerticalStackLayout { Children = { body, status } } };
        ToolbarItems.Add(new ToolbarItem { Text = "Actualizar", Command = new Command(async () => await Run(Load)) });
    }
    protected override async void OnAppearing() { base.OnAppearing(); await Run(Load); }
    private static Label Text(string value, double size = 16) => new() { Text = value, FontSize = size, TextColor = Color.FromArgb("#16352C") };
    private Button Action(string label, Func<Task> action)
    {
        var button = new Button { Text = label, BackgroundColor = Color.FromArgb("#176B57"), TextColor = Colors.White, CornerRadius = 14 };
        button.Clicked += async (_, _) => await Run(action);
        return button;
    }
    private async Task Run(Func<Task> action)
    {
        if (running) return;
        running = true; body.IsEnabled = false; status.Text = "";
        try { await action(); }
        catch (ApiException e) { if (e.Status == HttpStatusCode.Unauthorized) await expired(); else status.Text = e.Message; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        { status.Text = "No se pudo cargar el servicio. Pulsa Actualizar para reintentar."; }
        finally { running = false; body.IsEnabled = true; }
    }
    private async Task Load()
    {
        var detail = await api.Send<ServiceDetail>(HttpMethod.Get, Path);
        var service = detail.Service;
        body.Clear();
        body.Add(Text(service.Title, 26));
        body.Add(Text($"Ofrecido por {service.AuthorName}", 14));
        body.Add(Text(service.Body));
        body.Add(Text("Contacto privado", 21));
        body.Add(Text("Cada interesado tiene su propia conversación con quien ofrece el servicio. Solo vosotros dos podéis leerla.", 14));
        if (service.AuthorId != userId)
            body.Add(Action(detail.Conversations.Length == 0 ? "Escribir en privado" : "Abrir mi conversación", async () =>
            {
                var chat = await api.Send<MarketConversationDto>(HttpMethod.Post, Path + "/conversations");
                await Navigation.PushAsync(new ConversationPage(api, community, service, chat, userId, expired));
            }));
        else
        {
            if (detail.Conversations.Length == 0) body.Add(Text("Aquí aparecerán las consultas de tus vecinos."));
            foreach (var chat in detail.Conversations)
            {
                var card = new VerticalStackLayout { Spacing = 8 };
                card.Add(Text(chat.Name, 19));
                card.Add(new Label { Text = chat.LastMessage ?? "Conversación sin mensajes", MaxLines = 2, LineBreakMode = LineBreakMode.TailTruncation, TextColor = Color.FromArgb("#52665F") });
                card.Add(Action("Abrir conversación", () => Navigation.PushAsync(new ConversationPage(api, community, service, chat, userId, expired))));
                body.Add(new Border { Content = card, Padding = 16, BackgroundColor = Colors.White, Stroke = Colors.Transparent,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 } });
            }
        }
    }
}
