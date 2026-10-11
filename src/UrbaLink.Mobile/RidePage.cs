using System.Globalization;
using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class RidePage : ContentPage
{
    private readonly ApiClient api;private readonly CommunityDto community;private readonly Guid userId;private readonly Func<Task> expired;
    private readonly VerticalStackLayout content=new(){Spacing=14,Padding=20};
    private readonly Label status=Text("");private readonly VerticalStackLayout cards=new(){Spacing=12};
    private readonly Picker days=new(){Title="Día de la semana",TextColor=Color.FromArgb("#16352C")};
    private readonly Button more=new(){Text="Ver anteriores",IsVisible=false};
    private bool running,compose;private long? next;private int selectedDay=-1;
    private string Root=>$"api/communities/{community.Id}/rides";
    public RidePage(ApiClient api,CommunityDto community,Guid userId,Func<Task> expired,bool composeOnOpen=false)
    {
        this.api=api;this.community=community;this.userId=userId;this.expired=expired;compose=composeOnOpen;
        Title="Ir juntos";BackgroundColor=Color.FromArgb("#F4F7F6");
        content.Add(Text("Comparte tu ruta",28,true));content.Add(Text(community.Name));
        content.Add(Action("＋ Ofrecer un trayecto",()=>Navigation.PushAsync(new RideEditorPage(api,community))));
        days.Items.Add("Todos los días");foreach(var day in RideRules.DayNames)days.Items.Add(day);days.SelectedIndex=0;
        days.SelectedIndexChanged+=async(_,_)=>await Run(async()=>
        {
            var requested=days.SelectedIndex-1;
            try{await Load(true,requested);}catch{days.SelectedIndex=selectedDay+1;throw;}
        });
        content.Add(days);content.Add(cards);content.Add(more);
        more.Clicked+=async(_,_)=>await Run(()=>Load(false));
        var old=Action("Ver todas las publicaciones de coche",()=>Navigation.PushAsync(new BoardPage(api,community,userId,expired,"Coche")));
        old.BackgroundColor=Colors.Transparent;old.TextColor=Color.FromArgb("#176B57");content.Add(old);
        var layout=new Grid{RowDefinitions=new RowDefinitionCollection{new(GridLength.Auto),new(GridLength.Star)}};
        status.Margin=new Thickness(20,4);layout.Add(status);layout.Add(new ScrollView{Content=content},0,1);Content=layout;
        ToolbarItems.Add(new ToolbarItem{Text="Actualizar",Command=new Command(async()=>await Run(()=>Load(true)))});
    }
    internal static Label Text(string value,double size=15,bool bold=false)=>new(){Text=value,FontSize=size,FontAttributes=bold?FontAttributes.Bold:FontAttributes.None,TextColor=Color.FromArgb("#16352C")};
    private Button Action(string text,Func<Task> action)
    {
        var b=new Button{Text=text,BackgroundColor=Color.FromArgb("#176B57"),TextColor=Colors.White,CornerRadius=14};
        b.Clicked+=async(_,_)=>await Run(action);return b;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();await Run(async()=>{await Load(true);if(compose){compose=false;await Navigation.PushAsync(new RideEditorPage(api,community));}});
    }
    private async Task Run(Func<Task> action)
    {
        if(running)return;running=true;content.IsEnabled=false;status.Text="";
        try{await action();}
        catch(ApiException e){if(e.Status==HttpStatusCode.Unauthorized)await expired();else status.Text=e.Message;}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){status.Text="No se pudo conectar. Pulsa Actualizar para reintentar.";}
        finally{running=false;content.IsEnabled=true;}
    }
    private async Task Load(bool reset,int? requested=null)
    {
        var day=requested??selectedDay;
        var query=new List<string>();if(day>=0)query.Add("day="+day);if(!reset&&next is {} cursor)query.Add("before="+cursor);
        var page=await api.Send<RidePageResult>(HttpMethod.Get,Root+(query.Count>0?"?"+string.Join("&",query):""));
        selectedDay=day;if(reset)cards.Clear();
        foreach(var ride in page.Items)
        {
            var body=new VerticalStackLayout{Spacing=8};
            var header=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Auto)}};
            header.Add(Text(ride.Origin+" → "+ride.Destination,21,true));
            var menu=CardActions.Menu("Opciones del trayecto");menu.Clicked+=async(_,_)=>await Run(async()=>
            {
                var remove=ride.Post.AuthorId==userId||community.Role=="Admin";
                var chosen=await DisplayActionSheetAsync("Trayecto","Cancelar",remove?"Retirar trayecto":null,"Ver detalles y preguntas");
                if(chosen=="Ver detalles y preguntas")await Navigation.PushAsync(new CommentsPage(api,community,ride.Post.Id,userId,expired));
                if(chosen=="Retirar trayecto"&&remove&&await DisplayAlertAsync("Retirar trayecto","Dejará de mostrarse en la comunidad.","Retirar","Cancelar"))
                {await api.Send<object>(HttpMethod.Delete,$"api/communities/{community.Id}/posts/{ride.Post.Id}");await Load(true);}
            });header.Add(menu,1,0);body.Add(header);
            body.Add(Text(RideRules.DaysText(ride.Days),14,true));
            body.Add(Text("Ida "+RideRules.TimeText(ride.DepartureMinutes)+(ride.ReturnMinutes is {} back?" · Vuelta "+RideRules.TimeText(back)+(ride.ReturnNextDay?" (+1 día)":""):" · Solo ida"),18,true));
            var price=ride.PriceCents==0?"Gratis":(ride.PriceCents/100m).ToString("C",CultureInfo.GetCultureInfo("es-ES"))+" / asiento y sentido";
            body.Add(Text($"{ride.Seats} plazas · {price}"));
            body.Add(Text(ride.AllowsDetour?"Acepta desvíos"+(ride.DetourDetails.Length>0?": "+ride.DetourDetails:""):"Sin desvíos",13));
            if(ride.MeetingPoint.Length>0)body.Add(Text("Encuentro: "+ride.MeetingPoint,13));
            if(ride.Notes.Length>0)body.Add(Text(ride.Notes,13));
            body.Add(Text("Conduce "+ride.Post.AuthorName,12));
            cards.Add(new Border{Content=body,Padding=16,Stroke=Colors.Transparent,BackgroundColor=Colors.White,StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle{CornerRadius=18}});
        }
        if(reset&&page.Items.Length==0)cards.Add(Text("Todavía no hay trayectos para estos días."));
        next=page.NextBefore;more.IsVisible=next is not null;
    }
}

internal sealed class RideEditorPage : ContentPage
{
    private bool submitting,submitted;
    public RideEditorPage(ApiClient api,CommunityDto community)
    {
        Title="Ofrecer un trayecto";BackgroundColor=Color.FromArgb("#F4F7F6");
        var form=new VerticalStackLayout{Padding=20,Spacing=12};
        var feedback=RidePage.Text("");feedback.Margin=12;feedback.TextColor=Color.FromArgb("#8B3228");
        Entry Field(string placeholder,int length)=>new(){Placeholder=placeholder,MaxLength=length,TextColor=Color.FromArgb("#16352C")};
        var origin=Field("Origen: zona o población",80);var destination=Field("Destino: zona o población",80);
        var meeting=Field("Punto de encuentro (opcional)",200);
        form.Add(RidePage.Text("¿Qué ruta haces?",25,true));form.Add(origin);form.Add(destination);form.Add(meeting);
        form.Add(RidePage.Text("Días de salida",18,true));
        var week=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Star)},RowDefinitions=new RowDefinitionCollection{new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto),new(GridLength.Auto)}};
        var checks=new CheckBox[7];
        for(var i=0;i<7;i++)
        {
            checks[i]=new CheckBox{Color=Color.FromArgb("#176B57")};SemanticProperties.SetDescription(checks[i],RideRules.DayNames[i]);
            week.Add(new HorizontalStackLayout{Children={checks[i],new Label{Text=RideRules.DayNames[i],VerticalOptions=LayoutOptions.Center,TextColor=Color.FromArgb("#16352C")}}},i%2,i/2);
        }
        form.Add(week);form.Add(RidePage.Text("Horas locales del recorrido. Se repite cada semana en los días elegidos.",12));
        var departure=new TimePicker{Time=TimeSpan.FromHours(8),Format="HH:mm"};form.Add(RidePage.Text("Salida de ida"));form.Add(departure);
        var hasReturn=new Switch();form.Add(new HorizontalStackLayout{Children={hasReturn,RidePage.Text("También hago la vuelta")}});
        var back=new TimePicker{Time=TimeSpan.FromHours(18),Format="HH:mm"};var nextDay=new Switch();
        var returnForm=new VerticalStackLayout{IsVisible=false,Children={RidePage.Text("Salida de vuelta"),back,new HorizontalStackLayout{Children={nextDay,RidePage.Text("Vuelvo al día siguiente")}}}};
        hasReturn.Toggled+=(_,e)=>returnForm.IsVisible=e.Value;form.Add(returnForm);
        var seats=new Picker{Title="Plazas para pasajeros",ItemsSource=Enumerable.Range(1,8).Select(n=>n.ToString()).ToArray(),SelectedIndex=0};
        form.Add(RidePage.Text("Plazas ofrecidas (sin contar al conductor)"));form.Add(seats);
        var price=Field("Precio por asiento y sentido (€)",12);price.Keyboard=Keyboard.Numeric;price.Text="0";form.Add(price);form.Add(RidePage.Text("0 € = gratuito. Si hay vuelta, el precio se aplica también a ese sentido. Pago acordado entre vecinos.",12));
        var detour=new Switch();var details=Field("Zonas o minutos que puedes desviarte",300);details.IsVisible=false;
        detour.Toggled+=(_,e)=>details.IsVisible=e.Value;form.Add(new HorizontalStackLayout{Children={detour,RidePage.Text("Acepto hacer desvíos")}});form.Add(details);
        var notes=new Editor{Placeholder="Equipaje, mascotas, margen de espera u otras condiciones (opcional)",MaxLength=1000,HeightRequest=110,TextColor=Color.FromArgb("#16352C")};form.Add(notes);
        var publish=new Button{Text="Publicar trayecto",BackgroundColor=Color.FromArgb("#176B57"),TextColor=Colors.White,CornerRadius=14};form.Add(publish);
        var key=Guid.NewGuid();
        publish.Clicked+=async(_,_)=>
        {
            if(submitting)return;if(submitted){await Navigation.PopAsync();return;}
            var culture=CultureInfo.GetCultureInfo("es-ES");
            if(!decimal.TryParse(price.Text?.Trim(),NumberStyles.AllowDecimalPoint,culture,out var amount)&&!decimal.TryParse(price.Text?.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out amount))
            {feedback.Text="Introduce un precio válido en euros.";return;}
            if(amount<0||amount>10000||decimal.Round(amount,2)!=amount){feedback.Text="El precio debe estar entre 0 y 10.000 €, con un máximo de dos decimales.";return;}
            if(departure.Time is null || (hasReturn.IsToggled&&back.Time is null)){feedback.Text="Completa las horas de salida.";return;}
            var mask=0;for(var i=0;i<7;i++)if(checks[i].IsChecked)mask|=1<<i;
            var request=RideRules.Normalize(new CreateRideRequest(key,origin.Text??"",destination.Text??"",mask,(int)departure.Time.Value.TotalMinutes,
                hasReturn.IsToggled?(int)back.Time!.Value.TotalMinutes:null,hasReturn.IsToggled&&nextDay.IsToggled,seats.SelectedIndex+1,(int)(amount*100),detour.IsToggled,details.Text??"",meeting.Text??"",notes.Text??""));
            if(RideRules.Validate(request) is {} error){feedback.Text=error;return;}
            submitting=true;form.IsEnabled=false;feedback.Text="Publicando…";
            try{await api.Send<RideDto>(HttpMethod.Post,$"api/communities/{community.Id}/rides",request);submitted=true;await Navigation.PopAsync();}
            catch(ApiException e){feedback.Text=e.Message;}
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){feedback.Text="No se pudo confirmar el envío. Reintenta sin cambiar los datos para evitar duplicados.";}
            finally{submitting=false;form.IsEnabled=true;}
        };
        var layout=new Grid{RowDefinitions=new RowDefinitionCollection{new(GridLength.Auto),new(GridLength.Star)}};
        layout.Add(feedback);layout.Add(new ScrollView{Content=form},0,1);Content=layout;
        Shell.SetBackButtonBehavior(this,new BackButtonBehavior{Command=new Command(async()=>await Back())});
    }
    protected override bool OnBackButtonPressed(){_=Back();return true;}
    private async Task Back(){if(!submitting&&(submitted||await DisplayAlertAsync("Descartar trayecto","¿Salir sin publicar este trayecto?","Descartar","Seguir editando")))await Navigation.PopAsync();}
}
