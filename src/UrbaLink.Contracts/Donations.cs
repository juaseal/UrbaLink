namespace UrbaLink.Contracts;

public sealed record DonationInput(Guid RequestId,string Title,string Season,string Size,string Condition,
    string Audience,int Quantity,string Description,byte[]? Photo);
public sealed record DonationDto(long Id,Guid CommunityId,Guid OwnerId,string OwnerName,string Title,string Season,
    string Size,string Condition,string Audience,int Quantity,string Description,byte[]? Photo,string State,int Version);
public sealed record DonationPage(DonationDto[] Items,long? NextBefore);
public sealed record DonationDetail(DonationDto Donation,MarketConversationDto[] Conversations);
public sealed record DonationStateRequest(string State);

public static class DonationRules
{
    public static DonationInput Normalize(DonationInput r)=>r with{Title=r.Title?.Trim()??"",Size=r.Size?.Trim()??"",Description=r.Description?.Trim()??""};
    public static string? Validate(DonationInput r)
    {
        if(r.RequestId==Guid.Empty)return "El envío no es válido. Vuelve a abrir el formulario.";
        if(r.Title.Length is <1 or >120)return "Escribe un título de hasta 120 caracteres.";
        if(r.Size.Length is <1 or >40)return "Indica la talla o las tallas del lote (hasta 40 caracteres).";
        if(r.Quantity is <1 or >1000)return "Indica entre 1 y 1000 prendas.";
        if(r.Description.Length>2000)return "La descripción puede tener hasta 2000 caracteres.";
        if(r.Condition is not("New" or "LikeNew" or "Good" or "Worn"))return "Selecciona el estado de la ropa.";
        if(r.Audience is not("Adult" or "Young" or "Child" or "Baby"))return "Selecciona para quién es la ropa.";
        if(r.Season is not("AllYear" or "Winter" or "Summer" or "MidSeason"))return "Selecciona la temporada.";
        if(r.Photo is not null&&!MarketPhotoRules.IsValid(r.Photo))return "La foto debe ser un JPEG válido de hasta 40 KB. Puedes publicar sin foto.";
        return null;
    }
}
