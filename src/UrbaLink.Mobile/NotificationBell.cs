using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

internal sealed class NotificationBell : Button
{
    private readonly ApiClient api;
    private readonly IDispatcherTimer timer;
    private bool loading,active;
    public NotificationBell(ApiClient api,Func<Task> open)
    {
        this.api=api;Text="🔔";FontSize=17;MinimumHeightRequest=48;MinimumWidthRequest=64;
        BackgroundColor=Color.FromArgb("#E4F1EA");TextColor=Color.FromArgb("#176B57");CornerRadius=18;
        SemanticProperties.SetDescription(this,"Notificaciones");
        Clicked+=async(_,_)=>await open();
        timer=Dispatcher.CreateTimer();timer.Interval=TimeSpan.FromSeconds(10);
        timer.Tick+=async(_,_)=>await Refresh();
    }
    public void Start(){active=true;timer.Start();_=Refresh();}
    public void Stop(){active=false;timer.Stop();}
    private async Task Refresh()
    {
        if(loading||!active||api.Token is null)return;
        loading=true;var token=api.Token;
        try
        {
            var inbox=await api.Send<NotificationCount>(HttpMethod.Get,"api/notifications/count");
            if(!active||api.Token!=token)return;
            Text=inbox.UnreadCount==0?"🔔":"🔔 "+(inbox.UnreadCount>99?"99+":inbox.UnreadCount.ToString());
            SemanticProperties.SetDescription(this,$"Notificaciones: {inbox.UnreadCount} mensajes sin leer");
        }
        catch(Exception e) when(e is ApiException or HttpRequestException or TaskCanceledException or JsonException)
        {
            if(active){Text="🔔 ·";SemanticProperties.SetDescription(this,"Notificaciones. No se pudo actualizar el contador");}
        }
        finally{loading=false;}
    }
}
