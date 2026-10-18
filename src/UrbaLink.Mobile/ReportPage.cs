using System.Net;
using System.Text.Json;
using UrbaLink.Contracts;
namespace UrbaLink.Mobile;

public sealed class ReportPage : ContentPage
{
    public ReportPage(ApiClient api,Guid communityId,string kind,long targetId,Func<Task> expired)
    {
        Title="Denunciar";BackgroundColor=Color.FromArgb("#F4F7F6");
        var requestId=Guid.NewGuid();
        var reason=new Editor{Placeholder="Explica qué ocurre y por qué lo denuncias",MaxLength=2000,MinimumHeightRequest=150,AutoSize=EditorAutoSizeOption.TextChanges};
        var feedback=new Label{TextColor=Color.FromArgb("#8B3228")};var send=new Button{Text="Enviar denuncia",BackgroundColor=Color.FromArgb("#176B57"),TextColor=Colors.White};
        reason.TextChanged+=(_,_)=>requestId=Guid.NewGuid();
        var explanation=kind=="Message"?"Enviarás este mensaje concreto y tu explicación al equipo de UrbaLink para su revisión. No se enviará el resto de la conversación.":"Enviarás el contenido denunciado y tu explicación al equipo de UrbaLink, no al administrador de tu urbanización.";
        Content=new ScrollView{Content=new VerticalStackLayout{Padding=24,Spacing=16,Children={new Label{Text="Ayúdanos a cuidar UrbaLink",FontSize=24},new Label{Text=explanation},new Label{Text="Motivo"},reason,feedback,send}}};
        send.Clicked+=async(_,_)=>
        {
            var text=reason.Text?.Trim()??"";if(text.Length<5){feedback.Text="Explica el motivo con al menos 5 caracteres.";return;}
            send.IsEnabled=false;reason.IsReadOnly=true;
            try
            {
                var receipt=await api.Send<ReportReceipt>(HttpMethod.Post,"api/reports",new ReportRequest(requestId,communityId,kind,targetId,text));
                await DisplayAlertAsync("Denuncia enviada",$"El equipo de UrbaLink puede revisar tu denuncia #{receipt.Id}.","Aceptar");await Navigation.PopAsync();
            }
            catch(ApiException e){if(e.Status==HttpStatusCode.Unauthorized)await expired();else feedback.Text=e.Message;}
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or JsonException){feedback.Text="No se pudo confirmar el envío. Puedes reintentar sin duplicarlo.";}
            finally{send.IsEnabled=true;reason.IsReadOnly=false;}
        };
    }
}
