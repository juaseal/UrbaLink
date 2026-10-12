using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class ConversationPage : ContentPage
{
    private readonly ApiClient api;
    private readonly Guid userId;
    private readonly Func<Task> expired;
    private readonly string path;
    private readonly VerticalStackLayout messages = new() { Spacing=10,Padding=16 };
    private readonly Label feedback = new() { TextColor=Color.FromArgb("#8B3228"),FontSize=13 };
    private readonly Editor draft = new() { Placeholder="Escribe para acordar los detalles…",MaxLength=2000,
        AutoSize=EditorAutoSizeOption.TextChanges,MinimumHeightRequest=48,MaximumHeightRequest=140,
        TextColor=Color.FromArgb("#16352C"),BackgroundColor=Colors.White };
    private readonly Button send = new() { Text="Enviar",BackgroundColor=Color.FromArgb("#176B57"),TextColor=Colors.White,CornerRadius=14 };
    private readonly Button previous = new() { Text="Mensajes anteriores",BackgroundColor=Colors.Transparent,TextColor=Color.FromArgb("#176B57") };
    private readonly Button refresh = new() { Text="Actualizar",BackgroundColor=Colors.Transparent,TextColor=Color.FromArgb("#176B57") };
    private readonly ScrollView scroll;
    private readonly List<MarketMessageDto> history=[];
    private long? next;
    private Guid requestId=Guid.NewGuid();
    private bool running;

    public ConversationPage(ApiClient api,CommunityDto community,ItemDto item,MarketConversationDto conversation,Guid userId,Func<Task> expired)
        : this(api,$"api/communities/{community.Id}/items/{item.Id}/conversations/{conversation.Id}/messages",
            item.OwnerId==userId?conversation.Name:item.OwnerName,item.Title,userId,expired) { }
    public ConversationPage(ApiClient api,CommunityDto community,PostDto service,MarketConversationDto conversation,Guid userId,Func<Task> expired)
        : this(api,$"api/communities/{community.Id}/services/{service.Id}/conversations/{conversation.Id}/messages",
            service.AuthorId==userId?conversation.Name:service.AuthorName,service.Title,userId,expired) { }
    private ConversationPage(ApiClient api,string path,string title,string subject,Guid userId,Func<Task> expired)
    {
        this.api=api;this.userId=userId;this.expired=expired;this.path=path;
        Title=title;
        BackgroundColor=Color.FromArgb("#F4F7F6");
        var layout=new Grid { RowDefinitions=new RowDefinitionCollection {new(GridLength.Auto),new(GridLength.Star),new(GridLength.Auto)} };
        var header=new VerticalStackLayout {Padding=new Thickness(16,8),Spacing=4};
        header.Add(new Label {Text=subject,FontSize=19,FontAttributes=FontAttributes.Bold,TextColor=Color.FromArgb("#16352C")});
        header.Add(new Label {Text="Solo vosotros dos podéis leer esta conversación.",FontSize=12,TextColor=Color.FromArgb("#52665F")});
        header.Add(refresh);layout.Add(header);
        scroll=new ScrollView {Content=messages};layout.Add(scroll,0,1);
        var composer=new VerticalStackLayout {Padding=12,Spacing=6,Children={feedback,draft,send}};layout.Add(composer,0,2);
        Content=layout;
        Shell.SetBackButtonBehavior(this,new BackButtonBehavior {Command=new Command(async()=>await GoBack())});
        draft.TextChanged+=(_,_)=>requestId=Guid.NewGuid();
        send.Clicked+=async(_,_)=>await Run(async()=>
        {
            if(string.IsNullOrWhiteSpace(draft.Text)){feedback.Text="Escribe un mensaje.";return;}
            await api.Send<object>(HttpMethod.Post,path,new MarketMessageRequest(requestId,draft.Text));
            draft.Text="";
            try {await Load(true);}
            catch(Exception e) when(e is ApiException or HttpRequestException or TaskCanceledException or JsonException)
            {feedback.Text="Mensaje enviado. Pulsa Actualizar para verlo.";}
        });
        refresh.Clicked+=async(_,_)=>await Run(()=>Load(true));
        previous.Clicked+=async(_,_)=>await Run(()=>Load(false));
    }
    protected override bool OnBackButtonPressed(){_=GoBack();return true;}
    private Task GoBack()=>Run(async()=>
    {
        if(!string.IsNullOrWhiteSpace(draft.Text) && !await DisplayAlertAsync("Mensaje sin enviar","¿Quieres salir y descartar el mensaje?","Descartar","Seguir escribiendo"))return;
        await Navigation.PopAsync();
    });
    protected override async void OnAppearing(){base.OnAppearing();await Run(()=>Load(true));}
    private async Task Run(Func<Task> action)
    {
        if(running)return;
        running=true;send.IsEnabled=false;refresh.IsEnabled=false;previous.IsEnabled=false;draft.IsReadOnly=true;feedback.Text="";
        try{await action();}
        catch(ApiException e){if(e.Status==HttpStatusCode.Unauthorized)await expired();else feedback.Text=e.Message;}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException){feedback.Text="No se pudo confirmar el envío. Conservamos el texto; reintentar no lo duplica.";}
        catch(JsonException){feedback.Text="Respuesta inesperada del servidor.";}
        finally{running=false;send.IsEnabled=true;refresh.IsEnabled=true;previous.IsEnabled=true;draft.IsReadOnly=false;}
    }
    private async Task Load(bool reset)
    {
        if(!reset&&next is null)return;
        var page=await api.Send<MarketMessagePage>(HttpMethod.Get,path+(reset?"":"?before="+next));
        if(reset)history.Clear();
        history.AddRange(page.Items);next=page.NextBefore;messages.Clear();
        if(next is not null)messages.Add(previous);
        foreach(var message in history.OrderBy(m=>m.Id))
        {
            var mine=message.AuthorId==userId;
            var content=new VerticalStackLayout {Spacing=5};
            content.Add(new Label {Text=message.Body,FontSize=16,TextColor=Color.FromArgb("#16352C")});
            content.Add(new Label {Text=$"{message.Name} · {message.CreatedAt.LocalDateTime:g}",FontSize=11,TextColor=Color.FromArgb("#52665F")});
            messages.Add(new Border {Content=content,Padding=12,Margin=mine?new Thickness(28,0,0,0):new Thickness(0,0,28,0),
                BackgroundColor=Color.FromArgb(mine?"#E4F1EA":"#FFFFFF"),Stroke=Colors.Transparent,
                StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle {CornerRadius=16}});
        }
        if(history.Count==0)messages.Add(new Label {Text="Escribe en privado y acordad aquí los detalles.",TextColor=Color.FromArgb("#52665F")});
        if(reset && page.Items.Length>0)
        {
            try {await api.Send<object>(HttpMethod.Post,path+"/read",new MessageReadRequest(page.Items.Max(m=>m.Id)));}
            catch(Exception e) when(e is ApiException or HttpRequestException or TaskCanceledException or JsonException)
            {feedback.Text="Mensajes cargados. No se pudo actualizar el contador de notificaciones; pulsa Actualizar.";}
        }
        // Scroll to an actual bubble after layout, rather than an unbounded coordinate.
        if(reset && messages.Children.LastOrDefault() is View last)
            Dispatcher.Dispatch(async()=>
            {
                if(last.Parent==messages && Navigation.NavigationStack.LastOrDefault()==this)
                    await scroll.ScrollToAsync(last,ScrollToPosition.End,false);
            });
    }
}
