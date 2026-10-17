using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;
namespace UrbaLink.Mobile;

public sealed class AnimalsPage : ContentPage
{
    private readonly ApiClient api;
    private readonly CommunityDto community;
    private readonly Guid userId;
    private readonly Func<Task> expired;
    private readonly VerticalStackLayout body=new(){Padding=20,Spacing=12};
    private readonly Label feedback=new(){TextColor=Color.FromArgb("#8B3228"),Margin=16};
    private bool running,closed,editing;
    private long? detailId;
    private string Path=>$"api/communities/{community.Id}/animals";
    public AnimalsPage(ApiClient api,CommunityDto community,Guid userId,Func<Task> expired)
    {
        this.api=api;this.community=community;this.userId=userId;this.expired=expired;
        Shell.SetBackButtonBehavior(this,new BackButtonBehavior{Command=new Command(async()=>await Back())});
        Title="Animales";BackgroundColor=Color.FromArgb("#F4F7F6");
        Content=new ScrollView{Content=new VerticalStackLayout{Children={feedback,body}}};
        ToolbarItems.Add(new ToolbarItem{Text="Actualizar",Command=new Command(async()=>await Run(async()=>{if(await Discard())await Reload();}))});
    }
    protected override async void OnAppearing(){base.OnAppearing();if(!editing)await Run(Reload);}
    private Task Reload()=>detailId is {} id?Detail(id):Load();
    private async Task<bool> Discard()=>!editing || await DisplayAlertAsync("¿Descartar los cambios?","Los datos sin guardar se perderán.","Descartar","Seguir editando");
    protected override bool OnBackButtonPressed(){_=Back();return true;}
    private Task Back()=>Run(async()=>{if(!await Discard())return;if(editing || detailId is not null)await Load();else await Navigation.PopAsync();});
    private static Label Text(string value,int size=16)=>new(){Text=value,FontSize=size,TextColor=Color.FromArgb("#16352C")};
    private Button Action(string title,Func<Task> action)
    {
        var b=new Button{Text=title,BackgroundColor=Color.FromArgb("#176B57"),TextColor=Colors.White,CornerRadius=14};
        b.Clicked+=async(_,_)=>await Run(action);return b;
    }
    private async Task Run(Func<Task> action)
    {
        if(running)return;running=true;body.IsEnabled=false;feedback.Text="";
        try{await action();}
        catch(ApiException e){if(e.Status==HttpStatusCode.Unauthorized)await expired();else feedback.Text=e.Message;}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){feedback.Text="No se pudo confirmar la operación. Puedes reintentar.";}
        catch(Exception e) when(e is InvalidOperationException or PermissionException or FeatureNotSupportedException){feedback.Text=e.Message;}
        finally{running=false;body.IsEnabled=true;}
    }
    private async Task Load(long? before=null)
    {
        var page=await api.Send<AnimalNoticePage>(HttpMethod.Get,Path+$"?closed={closed.ToString().ToLowerInvariant()}"+(before is null?"":"&before="+before));
        if(before is null)
        {
            editing=false;detailId=null;body.Clear();body.Add(Text("Perdidos y encontrados",26));
            body.Add(Action("He perdido mi mascota",()=>{Form("Lost");return Task.CompletedTask;}));
            body.Add(Action("He encontrado un animal",()=>{Form("Found");return Task.CompletedTask;}));
            body.Add(Action(closed?"Ver avisos activos":"Ver avisos cerrados",async()=>{closed=!closed;await Load();}));
            body.Add(Text(closed?"Avisos cerrados":"Avisos activos de tu comunidad",20));
            if(page.Items.Length==0)body.Add(Text("No hay avisos en esta lista."));
        }
        foreach(var n in page.Items)
        {
            var card=new VerticalStackLayout{Spacing=8};
            if(n.Photo is {} photo)card.Add(new Image{Source=ImageSource.FromStream(()=>new MemoryStream(photo)),HeightRequest=160,Aspect=Aspect.AspectFit});
            card.Add(Text(n.Title,20));card.Add(Text($"{n.Location} · {n.OccurredAt.LocalDateTime:g}"));
            card.Add(Text(n.Status=="Active" && n.Kind=="Found" ? (n.IsWithReporter?"Está con quien lo encontró":"Visto en la zona"):n.StatusLabel));
            card.Add(Action("Ver aviso y contactar",()=>Detail(n.Id)));
            body.Add(new Border{Content=card,Padding=16,BackgroundColor=Colors.White,Stroke=Colors.Transparent});
        }
        if(page.NextBefore is {} next)
        {
            Button? more=null;more=Action("Ver anteriores",async()=>{await Load(next);body.Remove(more!);});body.Add(more);
        }
    }
    private async Task Detail(long id)
    {
        var detail=await api.Send<AnimalNoticeDetail>(HttpMethod.Get,Path+"/"+id);var n=detail.Notice;
        editing=false;detailId=id;body.Clear();body.Add(Action("Volver a los avisos",()=>Load()));body.Add(Text(n.Title,26));body.Add(Text(n.StatusLabel,20));
        if(n.Photo is {} photo)body.Add(new Image{Source=ImageSource.FromStream(()=>new MemoryStream(photo)),HeightRequest=240,Aspect=Aspect.AspectFit});
        body.Add(Text($"{n.Location} · {n.OccurredAt.LocalDateTime:g}"));body.Add(Text(n.Description));
        if(n.Kind=="Found")body.Add(Text(n.IsWithReporter?"Está con quien lo encontró":"Visto en la zona"));
        body.Add(Text("Publicado por "+n.AuthorName));
        body.Add(Text("Usa el chat privado para comunicar avistamientos o acordar la entrega. No publiques teléfonos ni direcciones exactas.",14));
        if(n.Status=="Active" && n.AuthorId!=userId)body.Add(Action("Contactar en privado",async()=>
        {
            var chat=await api.Send<MarketConversationDto>(HttpMethod.Post,Path+$"/{id}/conversations");
            await Navigation.PushAsync(new ConversationPage(api,community,n,chat,userId,expired));
        }));
        foreach(var chat in detail.Conversations)body.Add(Action(n.AuthorId==userId?"Conversación con "+chat.Name:"Abrir conversación",()=>Navigation.PushAsync(new ConversationPage(api,community,n,chat,userId,expired))));
        if(n.Status=="Active" && n.AuthorId==userId)
        {
            body.Add(Action("Editar aviso",()=>{Form(n.Kind,n);return Task.CompletedTask;}));
            body.Add(Action("Ha vuelto a casa",()=>Close(n,"Recovered")));
        }
        if(n.Status=="Active" && (n.AuthorId==userId || community.Role=="Admin"))body.Add(Action("Retirar aviso",()=>Close(n,"Withdrawn")));
    }
    private async Task Close(AnimalNoticeDto n,string state)
    {
        if(!await DisplayAlertAsync(state=="Recovered"?"¿Ha vuelto a casa?":"¿Retirar este aviso?","Se conservarán el aviso y las conversaciones.","Confirmar","Cancelar"))return;
        await api.Send<object>(HttpMethod.Post,Path+$"/{n.Id}/close",new CloseAnimalNoticeRequest(state));await Detail(n.Id);
    }
    private void Form(string kind,AnimalNoticeDto? n=null)
    {
        editing=true;body.Clear();body.Add(Text(n is null?(kind=="Lost"?"He perdido mi mascota":"He encontrado un animal"):"Editar aviso",24));
        var species=new Picker{Title="Perro o gato",ItemsSource=new[]{"Perro","Gato"},SelectedIndex=n is null?-1:n.Species=="Dog"?0:1};
        var name=new Entry{Placeholder="Nombre (opcional)",MaxLength=80,Text=n?.Name};
        var location=new Entry{Placeholder="Ej.: junto a la entrada norte",MaxLength=160,Text=n?.Location};
        var date=new DatePicker{Date=n?.OccurredAt.LocalDateTime.Date??DateTime.Today,MaximumDate=DateTime.Today,MinimumDate=new DateTime(2000,1,1)};
        var time=new TimePicker{Time=n?.OccurredAt.LocalDateTime.TimeOfDay??DateTime.Now.TimeOfDay};
        var description=new Editor{Placeholder="Aspecto, collar y circunstancias…",MaxLength=2000,AutoSize=EditorAutoSizeOption.TextChanges,MinimumHeightRequest=100,Text=n?.Description};
        var withMe=new Switch{IsToggled=n?.IsWithReporter??false};
        byte[]? photo=n?.Photo;var preview=new Image{HeightRequest=180,IsVisible=photo is not null,Aspect=Aspect.AspectFit};
        void ShowPhoto(){preview.IsVisible=photo is not null;var bytes=photo;preview.Source=bytes is null?null:ImageSource.FromStream(()=>new MemoryStream(bytes));}
        ShowPhoto();
        body.Add(Text("Animal"));body.Add(species);if(kind=="Lost"){body.Add(Text("Nombre (opcional)"));body.Add(name);}
        body.Add(Text("Zona aproximada"));body.Add(location);body.Add(Text("Fecha y hora aproximadas"));body.Add(date);body.Add(time);
        body.Add(Text("Descripción"));body.Add(description);
        if(kind=="Found"){body.Add(Text("¿Está contigo?"));SemanticProperties.SetDescription(withMe,"¿El animal está contigo?");body.Add(withMe);}
        body.Add(Text("Foto opcional"));body.Add(preview);
        body.Add(Action("Elegir foto",async()=>{var picked=await MarketPhoto.Pick("Foto del animal");if(picked is not null){photo=picked;ShowPhoto();}}));
        body.Add(Action("Quitar foto",()=>{photo=null;ShowPhoto();return Task.CompletedTask;}));
        body.Add(Text("Visible solo en tu comunidad. No incluyas teléfonos ni una dirección exacta; podéis hablar por mensaje privado.",14));
        AnimalNoticeInput? last=null;Guid requestId=Guid.NewGuid();
        body.Add(Action(n is null?"Publicar aviso":"Guardar cambios",async()=>
        {
            if(species.SelectedIndex<0){feedback.Text="Selecciona perro o gato.";return;}
            var local=DateTime.SpecifyKind((date.Date??DateTime.Today).Date+(time.Time??TimeSpan.Zero),DateTimeKind.Local);
            var input=AnimalNoticeRules.Normalize(new AnimalNoticeInput(requestId,kind,species.SelectedIndex==0?"Dog":"Cat",name.Text??"",location.Text??"",new DateTimeOffset(local),description.Text??"",kind=="Found"&&withMe.IsToggled,photo));
            if(last is not null && input!=last){requestId=Guid.NewGuid();input=input with{RequestId=requestId};}
            last=input;
            if(AnimalNoticeRules.Validate(input) is {} error){feedback.Text=error;return;}
            if(n is null){var created=await api.Send<AnimalNoticeDto>(HttpMethod.Post,Path,input);await Detail(created.Id);}
            else{await api.Send<object>(HttpMethod.Put,Path+$"/{n.Id}?version={n.Version}",input);await Detail(n.Id);}
        }));
        body.Add(Action("Cancelar",async()=>
        {if(await DisplayAlertAsync("¿Descartar los cambios?","Los datos sin guardar se perderán.","Descartar","Seguir editando"))await Load();}));
    }
}

