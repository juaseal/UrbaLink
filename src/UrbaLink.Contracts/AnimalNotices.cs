namespace UrbaLink.Contracts;

public sealed record AnimalNoticeInput(Guid RequestId, string Kind, string Species, string Name,
    string Location, DateTimeOffset OccurredAt, string Description, bool IsWithReporter, byte[]? Photo);
public sealed record AnimalNoticeDto(long Id, Guid CommunityId, Guid AuthorId, string AuthorName,
    string Kind, string Species, string Name, string Location, DateTimeOffset OccurredAt,
    string Description, bool IsWithReporter, byte[]? Photo, string Status, DateTimeOffset? ClosedAt, int Version)
{
    public string Title => $"{(Species == "Dog" ? "Perro" : "Gato")} {(Kind == "Lost" ? "perdido" : "encontrado")}{(Name.Length > 0 ? " · " + Name : "")}";
    public string StatusLabel => Status switch { "Recovered" => "Ha vuelto a casa", "Withdrawn" => "Aviso retirado", _ => "Activo" };
}
public sealed record AnimalNoticePage(AnimalNoticeDto[] Items, long? NextBefore);
public sealed record AnimalNoticeDetail(AnimalNoticeDto Notice, MarketConversationDto[] Conversations);
public sealed record CloseAnimalNoticeRequest(string Status);
public static class AnimalNoticeRules
{
    public static AnimalNoticeInput Normalize(AnimalNoticeInput r) => r with
    { Name = r.Name?.Trim() ?? "", Location = r.Location?.Trim() ?? "", Description = r.Description?.Trim() ?? "" };
    public static string? Validate(AnimalNoticeInput r)
    {
        if (r.RequestId == Guid.Empty || r.Kind is not ("Lost" or "Found") || r.Species is not ("Dog" or "Cat")) return "Selecciona pérdida o hallazgo y perro o gato.";
        if (r.Name.Length > 80 || r.Location.Length is < 2 or > 160 || r.Description.Length is < 1 or > 2000) return "Indica una zona aproximada y una descripción (máximo 2000 caracteres).";
        if (r.OccurredAt < new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero) || r.OccurredAt > DateTimeOffset.UtcNow.AddMinutes(10)) return "La fecha no puede estar en el futuro.";
        if (r.Kind == "Lost" && r.IsWithReporter) return "Una mascota perdida no puede estar contigo.";
        if (r.Photo is not null && !MarketPhotoRules.IsValid(r.Photo)) return "La foto debe ser un JPEG válido de hasta 40 KB.";
        return null;
    }
}
