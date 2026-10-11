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
    private readonly ScrollView scroll;
    private readonly List<ItemDto> items = [];
    private string Root => $"api/communities/{community.Id}/items";
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    public MarketPage(ApiClient api, CommunityDto community, Guid userId, Func<Task> expired)
    {
        this.api=api; this.community=community; this.userId=userId; this.expired=expired;
        Title="Objetos · "+community.Name; BackgroundColor=Color.FromArgb("#F4F7F6");
        status.IsVisible=false;
        status.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(status.Text))status.IsVisible=!string.IsNullOrEmpty(status.Text);};
        SemanticProperties.SetHint(status,"Estado de la operación");
        busy.SetBinding(IsVisibleProperty,new Binding(nameof(ActivityIndicator.IsRunning),source:busy));
        var layout=new Grid {RowDefinitions=new RowDefinitionCollection {new(GridLength.Auto),new(GridLength.Star)}};
        layout.Add(new VerticalStackLayout {Padding=new Thickness(20,4),Spacing=4,Children={busy,status}});
        scroll=new ScrollView {Content=new VerticalStackLayout {Padding=20,Children={body}}};
        layout.Add(scroll,0,1);Content=layout;
        Shell.SetBackButtonBehavior(this,new BackButtonBehavior {Command=new Command(async()=>await GoBack())});
        ToolbarItems.Add(new ToolbarItem {Text="Actualizar",Command=new Command(async()=>await Run(async()=>
        {
            if(editing) {status.Text="Termina o cancela el borrador antes de actualizar.";return;}
            if(detailId is {} id) await ShowDetail(id);else await LoadCatalog(true);
        }))});
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if(!editing) await Run(()=>detailId is {} id?ShowDetail(id):LoadCatalog(true));
    }
    protected override bool OnBackButtonPressed()
    {
        _=GoBack();return true;
    }
    private Task GoBack()=>Run(async()=>
    {
        if(editing && !await DisplayAlertAsync("Descartar borrador", "¿Quieres salir sin publicar este objeto?", "Descartar", "Seguir editando")) return;
        if(editing || detailId is not null) await LoadCatalog(true);
        else await Navigation.PopAsync();
    });
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
        busy.IsRunning=true; body.IsEnabled=false; status.Text="";status.TextColor=Color.FromArgb("#8B3228");
        try { await action(); }
        catch(ApiException e) { if(e.Status==HttpStatusCode.Unauthorized) await expired(); else status.Text=e.Message; }
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException) { status.Text="No se pudo confirmar la operación. Actualiza para comprobar su estado; los reintentos del mismo envío no se duplican."; }
        catch(JsonException) { status.Text="Respuesta inesperada del servidor."; }
        catch(InvalidOperationException e) { status.Text=e.Message; }
        catch(Exception e) when(e is IOException or PermissionException or FeatureNotSupportedException)
        {status.Text="No se pudo abrir la foto. Comprueba el acceso al archivo y prueba con otra imagen.";}
        finally { busy.IsRunning=false; body.IsEnabled=true; }
    }
    private static string Money(long cents) => (cents/100m).ToString("C",Spanish);
    private static string Period(string? value) => value switch { "Hour"=>"hora", "Week"=>"semana", _=>"día" };
    private static string Mode(string value) => value switch { "Sale"=>"VENTA", "Rent"=>"ALQUILER / PRÉSTAMO", _=>"SUBASTA" };
    private static string State(string value) => value switch
    { "Active"=>"Disponible", "Ended"=>"Subasta cerrada", "Withdrawn"=>"Retirado", "Completed"=>"Completado", _=>value };
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

    private async Task LoadCatalog(bool reset,string? requestedScope=null,string? requestedMode=null)
    {
        var selectedScope=requestedScope??scope;
        var selectedMode=requestedMode??mode;
        var query=new List<string>();
        if(selectedScope!="") query.Add("scope="+selectedScope);
        if(selectedMode!="") query.Add("mode="+selectedMode);
        if(!reset && next is not null) query.Add("before="+next);
        var page=await api.Send<ItemPage>(HttpMethod.Get,Root+(query.Count==0?"":"?"+string.Join("&",query)));
        scope=selectedScope;mode=selectedMode;
        if(reset) items.Clear();
        items.AddRange(page.Items); next=page.NextBefore; detailId=null; editing=false;
        RenderCatalog();
        if(reset)await scroll.ScrollToAsync(0,0,false);
    }
    private void RenderCatalog()
    {
        body.Clear();
        body.Add(Label("Dale otra vida.",30,true));
        body.Add(Label("Compra, alquila o encuentra tu próxima oportunidad entre vecinos."));
        body.Add(Button("＋  Ofrecer un objeto",ShowCreate));
        var tabs=new Grid { ColumnDefinitions=new ColumnDefinitionCollection {new(GridLength.Star),new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=5 };
        var sections=new[] {("Catálogo",""),("Mis objetos","Mine"),("Mis gestiones","Activity")};
        for(var n=0;n<sections.Length;n++)
        {
            var section=sections[n];
            var button=Button(section.Item1,()=>LoadCatalog(true,requestedScope:section.Item2),scope!=section.Item2);
            button.FontSize=12; button.Padding=new Thickness(4,8); tabs.Add(button,n,0);
        }
        body.Add(tabs);
        var modes=new Picker { Title="Modalidad",ItemsSource=new[]{"Todo","Venta","Alquiler / préstamo","Subasta"},SelectedIndex=mode switch {"Sale"=>1,"Rent"=>2,"Auction"=>3,_=>0},TextColor=Color.FromArgb("#16352C") };
        modes.SelectedIndexChanged+=async(_,_)=>await Run(async()=>
        {
            var selected=modes.SelectedIndex switch {1=>"Sale",2=>"Rent",3=>"Auction",_=>""};
            try {await LoadCatalog(true,requestedMode:selected);}
            catch {modes.SelectedIndex=mode switch {"Sale"=>1,"Rent"=>2,"Auction"=>3,_=>0};throw;}
        });
        body.Add(modes);
        if(items.Count==0) body.Add(Card(Label(scope=="Mine"?"Aquí aparecerán los objetos que ofrezcas.":scope=="Activity"?"Aquí puedes seguir tus conversaciones y pujas, también después del cierre.":"El catálogo está por estrenar. ¿Tienes algo que ya no usas?"),"#E4F1EA"));
        var grid=new Grid { ColumnDefinitions=new ColumnDefinitionCollection {new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=12,RowSpacing=12 };
        for(var n=0;n<items.Count;n++)
        {
            var item=items[n];
            if(n%2==0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var content=new VerticalStackLayout { Spacing=8 };
            content.Add(Photo(item.Photo,120)); content.Add(Label(Mode(item.Mode),10,true));
            content.Add(Label(item.Title,18,true)); content.Add(Label(Price(item),16,true));
            content.Add(Label(item.State=="Active"&&item.EndsAt is {} end?$"Cierra {end.LocalDateTime:g}":Availability(item),12));
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
        var changedPage=detailId!=id || editing;
        var item=detail.Item; detailId=id; editing=false; body.Clear();
        body.Add(Button("← Catálogo",()=>LoadCatalog(true),true));
        body.Add(Photo(item.Photo,240));
        var header=new Grid { ColumnDefinitions=new ColumnDefinitionCollection {new(GridLength.Star),new(GridLength.Auto)} };
        header.Add(Label(Mode(item.Mode)+" · "+Availability(item),12,true));
        if((item.OwnerId==userId || community.Role=="Admin") && item.State is not ("Withdrawn" or "Completed"))
        {
            var menu=CardActions.Menu("Opciones del objeto");
            menu.Clicked+=async(_,_)=>await Run(async()=>
            {
                var options=new List<string>();
                if(item.OwnerId==userId && item.State=="Active")
                {
                    if(item.Mode=="Rent") options.Add(item.IsLent?"Marcar disponible":"Marcar alquilado");
                    if(item.Mode=="Sale") options.Add("Marcar vendido");
                }
                var choice=await DisplayActionSheetAsync(item.Title,"Cancelar","Retirar objeto",options.ToArray());
                if(choice=="Retirar objeto")
                {
                    if(!await DisplayAlertAsync("Retirar objeto","Dejará de aparecer en el catálogo. Una subasta con pujas no se puede retirar.","Retirar","Cancelar")) return;
                    await api.Send<object>(HttpMethod.Post,$"{Root}/{id}/withdraw");
                }
                else if(choice is "Marcar disponible" or "Marcar alquilado")
                    await api.Send<object>(HttpMethod.Post,$"{Root}/{id}/availability",new AvailabilityRequest(!item.IsLent));
                else if(choice=="Marcar vendido")
                {
                    if(!await DisplayAlertAsync("Marcar vendido","El objeto saldrá del catálogo. Podrás seguir hablando con los interesados.","Marcar vendido","Cancelar")) return;
                    await api.Send<object>(HttpMethod.Post,$"{Root}/{id}/complete");
                }
                else return;
                await ShowDetail(id);
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
        if(item.Mode=="Rent") body.Add(Label(item.IsLent
            ?"Ahora está alquilado. Puedes escribir al propietario para acordar cuándo estará disponible."
            :"Acuerda la duración, disponibilidad y entrega directamente con el propietario.",14));
        if(item.OwnerId!=userId && (item.State=="Active" || item.WinnerId==userId))
            body.Add(Button("Hablar con el propietario",()=>OpenConversation(item,null)));
        if(item.OwnerId==userId && item.WinnerId is {} winner)
            body.Add(Button("Hablar con el ganador",()=>OpenConversation(item,winner)));
        if(detail.Conversations.Length>0)
        {
            body.Add(Label("Conversaciones privadas",20,true));
            foreach(var conversation in detail.Conversations)
            {
                var content=new VerticalStackLayout {Spacing=5};
                var name=item.OwnerId==userId?conversation.Name:item.OwnerName;
                var open=Button(name+"  →",()=>Navigation.PushAsync(new MarketChatPage(api,community,item,conversation,userId,expired)),true);
                content.Add(open);
                var preview=Label(conversation.LastMessage??"Conversación iniciada",13);
                preview.MaxLines=2;preview.LineBreakMode=LineBreakMode.TailTruncation;content.Add(preview);body.Add(Card(content));
            }
        }
        body.Add(Button("Actualizar objeto",()=>ShowDetail(id),true));
        if(changedPage)await scroll.ScrollToAsync(0,0,false);
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
            await ShowDetail(item.Id); status.TextColor=Color.FromArgb("#176B57");status.Text="Puja registrada.";
        }));
    }
    private static string Availability(ItemDto item) => item.State=="Active" && item.Mode=="Rent" && item.IsLent
        ? "Alquilado · puedes consultar" : item.State=="Completed" && item.Mode=="Sale" ? "Vendido" : State(item.State);

    private async Task OpenConversation(ItemDto item,Guid? interested)
    {
        var conversation=await api.Send<MarketConversationDto>(HttpMethod.Post,$"{Root}/{item.Id}/conversations",new StartMarketConversation(interested));
        await Navigation.PushAsync(new MarketChatPage(api,community,item,conversation,userId,expired));
    }
    private async Task ShowCreate()
    {
        editing=true;body.Clear();body.Add(Label("Un objeto, otra oportunidad.",27,true));
        var title=Input("¿Qué ofreces?");title.MaxLength=120;
        var description=new Editor {Placeholder="Estado, características y condiciones de entrega",MaxLength=4000,HeightRequest=130,TextColor=Color.FromArgb("#16352C")};
        var modePicker=new Picker {TextColor=Color.FromArgb("#16352C"),ItemsSource=new[]{"Venta","Alquiler / préstamo","Subasta"},SelectedIndex=0};
        var price=Input("Precio en €",Keyboard.Numeric);
        var period=new Picker {TextColor=Color.FromArgb("#16352C"),ItemsSource=new[]{"Hora","Día","Semana"},SelectedIndex=1};
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
        long? publishedId=null;
        body.Add(Button("Publicar objeto",async()=>
        {
            if(publishedId is {} saved) {await ShowDetail(saved);return;}
            if(!Cents(price.Text,out var value)) {status.Text="Introduce un precio válido con hasta dos decimales.";return;}
            var bidStep=0;
            if(modePicker.SelectedIndex==2 && (!Cents(step.Text,out bidStep)||bidStep==0)) {status.Text="El incremento mínimo debe ser mayor que cero.";return;}
            var selected=modePicker.SelectedIndex switch {1=>"Rent",2=>"Auction",_=>"Sale"};
            DateTimeOffset? close=selected=="Auction"?new DateTimeOffset(DateTime.SpecifyKind(closeDate.Date!.Value.Date+closeTime.Time!.Value,DateTimeKind.Local)).ToUniversalTime():null;
            var created=await api.Send<ItemDto>(HttpMethod.Post,Root,new CreateItemRequest(key,title.Text??"",description.Text??"",selected,value,
                selected=="Rent"?period.SelectedIndex switch {0=>"Hour",2=>"Week",_=>"Day"}:null,close,bidStep,photo));
            publishedId=created.Id;
            await ShowDetail(created.Id);status.TextColor=Color.FromArgb("#176B57");status.Text="Tu objeto ya está publicado.";
        }));
        var cancel=new Button {Text="Cancelar",BackgroundColor=Colors.Transparent,TextColor=Color.FromArgb("#176B57")};
        cancel.Clicked+=async(_,_)=>await GoBack();body.Add(cancel);
        await scroll.ScrollToAsync(0,0,false);
    }
}
