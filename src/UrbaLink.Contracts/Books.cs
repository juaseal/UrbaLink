namespace UrbaLink.Contracts;

public sealed record BookInput(Guid RequestId,string Title,string Author,string Isbn,string Condition,
    string Audience,string Mode,string Description,byte[]? Photo);
public sealed record BookDto(long Id,Guid CommunityId,Guid OwnerId,string OwnerName,string Title,string Author,
    string Isbn,string Condition,string Audience,string Mode,string Description,byte[] Photo,string State,int Version);
public sealed record BookPage(BookDto[] Items,long? NextBefore);
public sealed record BookDetail(BookDto Book,MarketConversationDto[] Conversations);
public sealed record BookStateRequest(string State);

public static class BookRules
{
    public static BookInput Normalize(BookInput r)=>r with{Title=r.Title?.Trim()??"",Author=r.Author?.Trim()??"",
        Isbn=r.Isbn?.Trim()??"",Description=r.Description?.Trim()??""};
    public static string? Validate(BookInput r)
    {
        if(r.RequestId==Guid.Empty||r.Title.Length is <1 or >120||r.Author.Length>120||r.Isbn.Length>32||r.Description.Length>2000)
            return "Revisa el título, autor, ISBN (hasta 32 caracteres) y descripción (hasta 2000).";
        if(r.Condition is not ("New" or "LikeNew" or "Good" or "Worn")||r.Audience is not ("Adult" or "Young" or "Child" or "Baby")||r.Mode is not ("Lend" or "Pass"))
            return "Selecciona estado del libro, público y si lo prestas o lo pasas.";
        if(r.Photo is null||!MarketPhotoRules.IsValid(r.Photo))return "Añade una foto JPEG válida del libro (hasta 40 KB).";
        return null;
    }
}
