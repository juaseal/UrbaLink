using System.Globalization;
using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class MarketPage : ContentPage
{
    private readonly ApiClient api;
    private readonly CommunityDto community;
    private readonly Guid userId;
    private readonly Func<Task> expired;
    private readonly VerticalStackLayout body = new() { Spacing = 16 };
    private readonly Label status = Label("");
    private readonly ActivityIndicator busy = new() { Color = Color.FromArgb("#176B57") };
    private string scope = "", mode = "";
    private long? detailId, next;
    private bool editing;
    private readonly List<ItemDto> items = [];
    private string Root => $"api/communities/{community.Id}/items";
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    public MarketPage(ApiClient api, CommunityDto community, Guid userId, Func<Task> expired)
    {
        this.api=api; this.community=community; this.userId=userId; this.expired=expired;
        Title="Objetos · "+community.Name; BackgroundColor=Color.FromArgb("#F4F7F6");
        Content=new ScrollView { Content=new VerticalStackLayout { Padding=20, Spacing=12, Children={ busy,status,body } } };
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if(!editing) await Run(()=>detailId is {} id?ShowDetail(id):LoadCatalog(true));
    }
    private static Label Label(string text,double size=15,bool bold=false) => new()
    { Text=text,FontSize=size,FontAttributes=bold?FontAttributes.Bold:FontAttributes.None,TextColor=Color.FromArgb("#16352C") };
    private static Border Card(View content,string background="#FFFFFF") => new()
    { Content=content,Padding=16,BackgroundColor=Color.FromArgb(background),Stroke=Colors.Transparent,
      StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius=20 } };
    private Button Button(string text,Func<Task> action,bool quiet=false)
    {
        var button=new Button { Text=text,CornerRadius=14,BackgroundColor=Color.FromArgb(quiet?"#E4F1EA":"#176B57"),
            TextColor=quiet?Color.FromArgb("#176B57"):Colors.White };
        button.Clicked+=async(_,_)=>await Run(action); return button;
    }
    private async Task Run(Func<Task> action)
    {
        if(busy.IsRunning) return;
        busy.IsRunning=true; body.IsEnabled=false; status.Text="";
        try { await action(); }
        catch(ApiException e) { if(e.Status==HttpStatusCode.Unauthorized) await expired(); else status.Text=e.Message; }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException) { status.Text="No se pudo confirmar la operación. Actualiza para comprobar su estado; los reintentos del mismo envío no se duplican."; }
        catch(JsonException) { status.Text="Respuesta inesperada del servidor."; }
        catch(InvalidOperationException e) { status.Text=e.Message; }
        finally { busy.IsRunning=false; body.IsEnabled=true; }
    }
    private static string Money(long cents) => (cents/100m).ToString("C",Spanish);
    private static string Period(string? value) => value switch { "Hour"=>"hora", "Week"=>"semana", _=>"día" };
    private static string Mode(string value) => value switch { "Sale"=>"VENTA", "Rent"=>"ALQUILER / PRÉSTAMO", _=>"SUBASTA" };
    private static string State(string value) => value switch
    { "Active"=>"Disponible", "Reserved"=>"Reservado", "Ended"=>"Subasta cerrada", "Withdrawn"=>"Retirado", "Completed"=>"Completado", "Pending"=>"Pendiente", "Accepted"=>"Aceptada", "Rejected"=>"No aceptada", "Cancelled"=>"Cancelada", _=>value };
    private static string Price(ItemDto item) => item.Mode switch
    { "Rent"=>item.PriceCents==0?"Préstamo gratuito":$"{Money(item.PriceCents)} / {Period(item.Period)}",
      "Auction"=>$"{Money(item.HighestBidCents??item.PriceCents)} · {(item.BidCount==0?"salida":"puja actual")}", _=>Money(item.PriceCents) };
    private static View Photo(byte[]? bytes,double height=150) => bytes is { Length:>0 }
        ? new Image { Source=ImageSource.FromStream(()=>new MemoryStream(bytes)),Aspect=Aspect.AspectFit,HeightRequest=height,BackgroundColor=Color.FromArgb("#EFF3ED") }
        : new Border { BackgroundColor=Color.FromArgb("#EFF3ED"),Stroke=Colors.Transparent,HeightRequest=height,
            Content=new Label { Text="◇",FontSize=64,TextColor=Color.FromArgb("#7B9C83"),HorizontalTextAlignment=TextAlignment.Center,VerticalTextAlignment=TextAlignment.Center } };
    private static bool Cents(string? text,out int cents)
    {
        cents=0;
        if(!decimal.TryParse(text?.Trim(),NumberStyles.AllowDecimalPoint,Spanish,out var value) &&
            !decimal.TryParse(text?.Trim(),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out value)) return false;
        if(value<0 || value>MarketRules.MaxPriceCents/100m || decimal.Round(value,2)!=value) return false;
        cents=(int)(value*100); return true;
    }
    private static Entry Input(string hint,Keyboard? keyboard=null) => new()
    { Placeholder=hint,Keyboard=keyboard??Keyboard.Default,TextColor=Color.FromArgb("#16352C"),BackgroundColor=Colors.White };

    private async Task LoadCatalog(bool reset)
    {
        var page=await api.Send<ItemPage>(HttpMethod.Get,Root+$"?scope={Uri.EscapeDataString(scope)}".Replace("scope=",scope==""?"unused=":"scope=")+
            (mode==""?"":"&mode="+mode)+(reset||next is null?"":"&before="+next));
        if(reset) items.Clear();
        items.AddRange(page.Items); next=page.NextBefore; detailId=null; editing=false;
        RenderCatalog();
    }
    private void RenderCatalog()
    {
        body.Clear();
        body.Add(Label("Dale otra vida.",30,true));
        body.Add(Label("Compra, alquila o encuentra tu próxima oportunidad entre vecinos."));
        body.Add(Button("＋  Ofrecer un objeto",()=>{ShowCreate();return Task.CompletedTask;}));
        var tabs=new Grid { ColumnDefinitions=new ColumnDefinitionCollection {new(GridLength.Star),new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=5 };
        var sections=new[] {("Catálogo",""),("Mis objetos","Mine"),("Mis gestiones","Activity")};
        for(var n=0;n<sections.Length;n++)
        {
            var section=sections[n];
            var button=Button(section.Item1,async()=>{scope=section.Item2;await LoadCatalog(true);},scope!=section.Item2);
            button.FontSize=12; button.Padding=new Thickness(4,8); tabs.Add(button,n,0);
        }
        body.Add(tabs);
        var modes=new Picker { Title="Modalidad",ItemsSource=new[]{"Todo","Venta","Alquiler / préstamo","Subasta"},SelectedIndex=mode switch {"Sale"=>1,"Rent"=>2,"Auction"=>3,_=>0},TextColor=Color.FromArgb("#16352C") };
        modes.SelectedIndexChanged+=async(_,_)=>await Run(async()=>{mode=modes.SelectedIndex switch {1=>"Sale",2=>"Rent",3=>"Auction",_=>""};await LoadCatalog(true);});
        body.Add(modes);
        if(items.Count==0) body.Add(Card(Label(scope=="Mine"?"Aquí aparecerán los objetos que ofrezcas.":scope=="Activity"?"Aquí puedes seguir tus solicitudes y pujas, también después del cierre.":"El catálogo está por estrenar. ¿Tienes algo que ya no usas?"),"#E4F1EA"));
        var grid=new Grid { ColumnDefinitions=new ColumnDefinitionCollection {new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=12,RowSpacing=12 };
        for(var n=0;n<items.Count;n++)
        {
            var item=items[n];
            if(n%2==0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var content=new VerticalStackLayout { Spacing=8 };
            content.Add(Photo(item.Photo,120)); content.Add(Label(Mode(item.Mode),10,true));
            content.Add(Label(item.Title,18,true)); content.Add(Label(Price(item),16,true));
            content.Add(Label(item.State=="Active"&&item.EndsAt is {} end?$"Cierra {end.LocalDateTime:g}":State(item.State),12));
            if(item.Leading && item.State=="Active") content.Add(Label("Tu puja va en cabeza",12,true));
            var card=Card(content); card.Padding=12;
            SemanticProperties.SetDescription(card,$"{item.Title}. {Price(item)}. Abrir objeto");
            var tap=new TapGestureRecognizer(); tap.Tapped+=async(_,_)=>await Run(()=>ShowDetail(item.Id)); card.GestureRecognizers.Add(tap);
            grid.Add(card,n%2,n/2);
        }
        body.Add(grid);
        if(next is not null) body.Add(Button("Ver más objetos",()=>LoadCatalog(false),true));
        body.Add(Button("Actualizar",()=>LoadCatalog(true),true));
    }

    private async Task ShowDetail(long id)
    {
        var detail=await api.Send<ItemDetail>(HttpMethod.Get,$"{Root}/{id}");
        var item=detail.Item; detailId=id; editing=false; body.Clear();
        body.Add(Button("← Catálogo",()=>LoadCatalog(true),true));
        body.Add(Photo(item.Photo,240));
        var header=new Grid { ColumnDefinitions=new ColumnDefinitionCollection {new(GridLength.Star),new(GridLength.Auto)} };
        header.Add(Label(Mode(item.Mode)+" · "+State(item.State),12,true));
        if((item.OwnerId==userId || community.Role=="Admin") && item.State is not ("Withdrawn" or "Completed"))
        {
            var menu=CardActions.Menu("Opciones del objeto");
            menu.Clicked+=async(_,_)=>await Run(async()=>
            {
                if(await DisplayActionSheetAsync(item.Title,"Cancelar","Retirar objeto")!="Retirar objeto") return;
                if(!await DisplayAlertAsync("Retirar objeto","Dejará de aparecer en el catálogo. No se puede retirar si tiene pujas o reservas aceptadas.","Retirar","Cancelar")) return;
                await api.Send<object>(HttpMethod.Post,$"{Root}/{id}/withdraw"); await ShowDetail(id);
            }); header.Add(menu,1,0);
        }
        body.Add(header); body.Add(Label(item.Title,28,true)); body.Add(Label(Price(item),25,true));
        body.Add(Label(item.Description)); body.Add(Label($"Lo ofrece {item.OwnerName}",13));
        body.Add(Label("Pago y entrega acordados entre vecinos. La app no realiza cobros.",12));
        if(item.Mode=="Auction")
        {
            body.Add(Card(new VerticalStackLayout { Spacing=8,Children={Label($"{item.BidCount} pujas · incremento mínimo {Money(item.BidStepCents)}",14),
                Label($"Cierre: {item.EndsAt!.Value.LocalDateTime:g}",16,true),Label("El cierre lo determina el servidor. Actualiza para ver las últimas pujas.",12)} },"#FBF1D9"));
            if(item.State is "Ended" or "Completed") body.Add(Label(item.WinnerName is null?"La subasta terminó sin pujas.":$"Ganador: {item.WinnerName} · {Money(item.HighestBidCents!.Value)}",18,true));
            else if(item.Leading) body.Add(Label("Tu puja va en cabeza.",17,true));
            if(item.OwnerId!=userId && item.State=="Active") AddBid(item);
            if(item.OwnerId==userId && item.State=="Ended" && item.BidCount>0)
                body.Add(Button("Confirmar entrega al ganador",async()=>
                {
                    if(!await DisplayAlertAsync("Confirmar entrega","¿Has entregado el objeto al ganador? Esto no registra ni realiza un cobro.","Confirmar","Cancelar")) return;
                    await api.Send<object>(HttpMethod.Post,$"{Root}/{id}/complete");await ShowDetail(id);
                }));
        }
        else
        {
            if(detail.Bookings.Length>0)
            {
                body.Add(Label("Fechas ya reservadas",18,true));
                foreach(var slot in detail.Bookings) body.Add(Label($"{slot.StartsAt.LocalDateTime:g} → {slot.EndsAt.LocalDateTime:g}",13));
            }
            if(item.OwnerId!=userId && item.State=="Active")
                body.Add(Button(item.Mode=="Rent"?"Solicitar fechas":"Solicitar compra",()=>{ShowRequest(item);return Task.CompletedTask;}));
            body.Add(Label(item.OwnerId==userId?"Solicitudes recibidas":"Tus solicitudes",20,true));
            if(detail.Requests.Length==0) body.Add(Label("Todavía no hay solicitudes."));
            foreach(var request in detail.Requests) AddRequest(item,request);
        }
        body.Add(Button("Actualizar objeto",()=>ShowDetail(id),true));
    }
    private void AddBid(ItemDto item)
    {
        var minimum=item.HighestBidCents is {} high?(long)high+item.BidStepCents:Math.Max(1,item.PriceCents);
        if(minimum>MarketRules.MaxPriceCents) {body.Add(Label("Se ha alcanzado el importe máximo admitido."));return;}
        var amount=Input("Tu puja en €",Keyboard.Numeric); amount.Text=(minimum/100m).ToString("0.00",Spanish);
        var key=Guid.NewGuid(); amount.TextChanged+=(_,_)=>key=Guid.NewGuid();
        body.Add(Label($"Puja mínima: {Money(minimum)}",14)); body.Add(amount);
        body.Add(Button("Pujar",async()=>
        {
            if(!Cents(amount.Text,out var value) || value<minimum) {status.Text="Introduce una puja válida igual o superior al mínimo.";return;}
            if(!await DisplayAlertAsync("Confirmar puja",$"Vas a pujar {Money(value)} por «{item.Title}». Las pujas no se pueden retirar.","Pujar","Cancelar")) return;
            await api.Send<object>(HttpMethod.Post,$"{Root}/{item.Id}/bids",new BidRequest(key,value));
            await ShowDetail(item.Id); status.Text="Puja registrada.";
        }));
    }
    private void AddRequest(ItemDto item,MarketRequestDto request)
    {
        var content=new VerticalStackLayout {Spacing=8};
        content.Add(Label($"{request.Name} · {State(request.State)}",16,true));
        if(request.StartsAt is {} start) content.Add(Label($"{start.LocalDateTime:g} → {request.EndsAt!.Value.LocalDateTime:g}",13));
        content.Add(Label(Money(request.TotalCents),19,true));
        if(request.Note.Length>0) content.Add(Label(request.Note));
        async Task Decide(string action)
        {
            var message=action=="Accept"?"¿Aceptar esta solicitud por "+Money(request.TotalCents)+"?":action=="Complete"?(item.Mode=="Rent"?"¿El objeto ya ha sido devuelto?":"¿Has entregado el objeto?"):"¿Confirmar esta acción?";
            if(!await DisplayAlertAsync("Confirmar",message,"Confirmar","Volver")) return;
            await api.Send<object>(HttpMethod.Post,$"{Root}/{item.Id}/requests/{request.Id}",new RequestDecision(action));
            await ShowDetail(item.Id);
        }
        if(request.State=="Pending")
        {
            if(item.OwnerId==userId)
            { content.Add(Button("Aceptar solicitud",()=>Decide("Accept")));content.Add(Button("Rechazar",()=>Decide("Reject"),true)); }
            else content.Add(Button("Cancelar solicitud",()=>Decide("Cancel"),true));
        }
        if(request.State=="Accepted" && item.OwnerId==userId) content.Add(Button(item.Mode=="Rent"?"Confirmar devolución":"Confirmar entrega",()=>Decide("Complete")));
        body.Add(Card(content));
    }
    private void ShowRequest(ItemDto item)
    {
        editing=true;body.Clear();body.Add(Label(item.Mode=="Rent"?"Elige tus fechas":"Solicitar compra",27,true));
        body.Add(Label(item.Title,20,true));body.Add(Label(Price(item),18));
        var from=new DatePicker { Date=DateTime.Today.AddDays(1),MinimumDate=DateTime.Today,MaximumDate=DateTime.Today.AddDays(365) };
        var fromTime=new TimePicker { Time=TimeSpan.FromHours(10) };
        var to=new DatePicker { Date=DateTime.Today.AddDays(2),MinimumDate=DateTime.Today,MaximumDate=DateTime.Today.AddDays(366) };
        var toTime=new TimePicker { Time=TimeSpan.FromHours(10) };
        DateTimeOffset Start()=>new(DateTime.SpecifyKind(from.Date!.Value.Date+fromTime.Time!.Value,DateTimeKind.Local));
        DateTimeOffset End()=>new(DateTime.SpecifyKind(to.Date!.Value.Date+toTime.Time!.Value,DateTimeKind.Local));
        var total=Label("",22,true);
        void Estimate()
        {
            total.Text=End()>Start()?"Total estimado: "+Money(MarketRules.RentalTotal(item.PriceCents,item.Period!,Start(),End())):"La devolución debe ser posterior a la recogida.";
        }
        if(item.Mode=="Rent")
        {
            body.Add(Label("Recogida"));body.Add(from);body.Add(fromTime);body.Add(Label("Devolución"));body.Add(to);body.Add(toTime);
            body.Add(Label($"Se cobra cada {Period(item.Period)} iniciada (24 h por día, 7 días por semana).",12));
            from.DateSelected+=(_,_)=>Estimate();to.DateSelected+=(_,_)=>Estimate();fromTime.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(TimePicker.Time))Estimate();};toTime.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(TimePicker.Time))Estimate();};
            Estimate();body.Add(total);
        }
        var note=new Editor { Placeholder="Mensaje al propietario: recogida, dudas…",MaxLength=1000,HeightRequest=110,TextColor=Color.FromArgb("#16352C") };
        body.Add(note);var key=Guid.NewGuid();
        body.Add(Label("El propietario debe aceptar la solicitud. No se cobra nada al enviarla.",13));
        body.Add(Button("Enviar solicitud",async()=>
        {
            await api.Send<object>(HttpMethod.Post,$"{Root}/{item.Id}/requests",new ObjectRequest(key,item.Mode=="Rent"?Start().ToUniversalTime():null,item.Mode=="Rent"?End().ToUniversalTime():null,note.Text??""));
            await ShowDetail(item.Id);status.Text="Solicitud enviada al propietario.";
        }));
        body.Add(Button("Volver al objeto",()=>ShowDetail(item.Id),true));
    }
    private void ShowCreate()
    {
        editing=true;body.Clear();body.Add(Label("Un objeto, otra oportunidad.",27,true));
        var title=Input("¿Qué ofreces?");title.MaxLength=120;
        var description=new Editor {Placeholder="Estado, características y condiciones de entrega",MaxLength=4000,HeightRequest=130,TextColor=Color.FromArgb("#16352C")};
        var modePicker=new Picker {ItemsSource=new[]{"Venta","Alquiler / préstamo","Subasta"},SelectedIndex=0};
        var price=Input("Precio en €",Keyboard.Numeric);
        var period=new Picker {ItemsSource=new[]{"Hora","Día","Semana"},SelectedIndex=1};
        var step=Input("Incremento mínimo en €",Keyboard.Numeric);step.Text="1";
        var closeDate=new DatePicker {Date=DateTime.Today.AddDays(3),MinimumDate=DateTime.Today,MaximumDate=DateTime.Today.AddDays(30)};
        var closeTime=new TimePicker {Time=TimeSpan.FromHours(20)};
        var photoHost=new ContentView {Content=Photo(null)};byte[]? photo=null;
        body.Add(photoHost);
        body.Add(Button("Elegir foto",async()=>{var chosen=await MarketPhoto.Pick();if(chosen is not null){photo=chosen;photoHost.Content=Photo(photo);}},true));
        body.Add(title);body.Add(description);body.Add(Label("Modalidad"));body.Add(modePicker);body.Add(price);
        var rental=new VerticalStackLayout {Spacing=8,IsVisible=false,Children={Label("Precio por"),period,Label("Para un préstamo gratuito, indica 0 €.",12)}};
        var auction=new VerticalStackLayout {Spacing=8,IsVisible=false,Children={Label("Incremento mínimo de cada puja"),step,Label("Fecha y hora de cierre"),closeDate,closeTime,Label("El precio será el de salida. Una subasta con pujas no se puede retirar.",12)}};
        modePicker.SelectedIndexChanged+=(_,_)=>{rental.IsVisible=modePicker.SelectedIndex==1;auction.IsVisible=modePicker.SelectedIndex==2;price.Placeholder=modePicker.SelectedIndex==2?"Precio de salida en €":"Precio en €";};
        body.Add(rental);body.Add(auction);body.Add(Label("El precio se publica en euros. Pago y entrega se acuerdan entre vecinos.",12));
        var key=Guid.NewGuid();
        body.Add(Button("Publicar objeto",async()=>
        {
            if(!Cents(price.Text,out var value)) {status.Text="Introduce un precio válido con hasta dos decimales.";return;}
            var bidStep=0;
            if(modePicker.SelectedIndex==2 && (!Cents(step.Text,out bidStep)||bidStep==0)) {status.Text="El incremento mínimo debe ser mayor que cero.";return;}
            var selected=modePicker.SelectedIndex switch {1=>"Rent",2=>"Auction",_=>"Sale"};
            DateTimeOffset? close=selected=="Auction"?new DateTimeOffset(DateTime.SpecifyKind(closeDate.Date!.Value.Date+closeTime.Time!.Value,DateTimeKind.Local)).ToUniversalTime():null;
            var created=await api.Send<ItemDto>(HttpMethod.Post,Root,new CreateItemRequest(key,title.Text??"",description.Text??"",selected,value,
                selected=="Rent"?period.SelectedIndex switch {0=>"Hour",2=>"Week",_=>"Day"}:null,close,bidStep,photo));
            await ShowDetail(created.Id);status.Text="Tu objeto ya está publicado.";
        }));
        body.Add(Button("Cancelar",()=>LoadCatalog(true),true));
    }
}
