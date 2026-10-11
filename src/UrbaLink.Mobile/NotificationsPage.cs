using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class NotificationsPage : ContentPage
{
    private readonly ApiClient api;
    private readonly Guid userId;
    private readonly Func<Task> expired;
    private readonly VerticalStackLayout list=new(){Spacing=12,Padding=20};
    private readonly Label status=new(){TextColor=Color.FromArgb("#8B3228"),Margin=12};
    private readonly IDispatcherTimer timer;
    private bool running,active;
    public NotificationsPage(ApiClient api,Guid userId,Func<Task> expired)
    {
        this.api=api;this.userId=userId;this.expired=expired;Title="Notificaciones";
        BackgroundColor=Color.FromArgb("#F4F7F6");
        var layout=new Grid {RowDefinitions=new RowDefinitionCollection {new(GridLength.Auto),new(GridLength.Star)}};
        layout.Add(status);layout.Add(new ScrollView {Content=list},0,1);Content=layout;
        ToolbarItems.Add(new ToolbarItem {Text="Actualizar",Command=new Command(async()=>await Run(Load))});
        timer=Dispatcher.CreateTimer();timer.Interval=TimeSpan.FromSeconds(10);timer.Tick+=async(_,_)=>await Run(Load);
    }
    protected override async void OnAppearing(){base.OnAppearing();active=true;timer.Start();await Run(Load);}
    protected override void OnDisappearing(){active=false;timer.Stop();base.OnDisappearing();}
    private async Task Run(Func<Task> action)
    {
        if(running||!active)return;running=true;list.IsEnabled=false;status.Text="";
        try{await action();}
        catch(ApiException e){if(e.Status==HttpStatusCode.Unauthorized)await expired();else status.Text=e.Message;}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){status.Text="No se pudieron cargar las notificaciones. Pulsa Actualizar.";}
        finally{running=false;list.IsEnabled=true;}
    }
    private async Task Load()
    {
        var inbox=await api.Send<NotificationInbox>(HttpMethod.Get,"api/notifications");
        if(!active)return;
        list.Clear();
        list.Add(new Label {Text=inbox.UnreadCount==0?"Estás al día":"Tienes "+inbox.UnreadCount+" mensajes sin leer",FontSize=24,FontAttributes=FontAttributes.Bold,TextColor=Color.FromArgb("#16352C")});
        if(inbox.UnreadCount==0)list.Add(new Label {Text="Aquí aparecerán los mensajes sobre tus objetos y las respuestas a tus consultas.",TextColor=Color.FromArgb("#52665F")});
        foreach(var n in inbox.Items)
        {
            var content=new VerticalStackLayout {Spacing=7};
            content.Add(new Label {Text=$"{n.SenderName} · {n.UnreadCount} sin leer",FontAttributes=FontAttributes.Bold,TextColor=Color.FromArgb("#176B57")});
            content.Add(new Label {Text=n.ItemTitle,FontSize=20,FontAttributes=FontAttributes.Bold,TextColor=Color.FromArgb("#16352C")});
            content.Add(new Label {Text=n.Preview,MaxLines=2,LineBreakMode=LineBreakMode.TailTruncation,TextColor=Color.FromArgb("#52665F")});
            content.Add(new Label {Text=n.CommunityName,FontSize=12,TextColor=Color.FromArgb("#52665F")});
            var card=new Border {Content=content,Padding=18,Stroke=Colors.Transparent,BackgroundColor=Colors.White,
                StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle {CornerRadius=18}};
            var tap=new TapGestureRecognizer();tap.Tapped+=async(_,_)=>await Run(async()=>
            {
                var detail=await api.Send<ItemDetail>(HttpMethod.Get,$"api/communities/{n.CommunityId}/items/{n.ItemId}");
                var conversation=detail.Conversations.FirstOrDefault(c=>c.Id==n.ConversationId);
                if(conversation is null){status.Text="Esta conversación ya no está disponible.";return;}
                await Navigation.PushAsync(new MarketChatPage(api,new CommunityDto(n.CommunityId,n.CommunityName,n.Role),detail.Item,conversation,userId,expired));
            });card.GestureRecognizers.Add(tap);list.Add(card);
        }
        if(inbox.Items.Length==50)list.Add(new Label {Text="Se muestran las 50 conversaciones más recientes. Al leerlas aparecerán las siguientes.",TextColor=Color.FromArgb("#52665F")});
    }
}
