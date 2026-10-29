namespace UrbaLink.Contracts;

public sealed record CreateRideRequest(Guid RequestId,string Origin,string Destination,int Days,
    int DepartureMinutes,int? ReturnMinutes,bool ReturnNextDay,int Seats,int PriceCents,
    bool AllowsDetour,string DetourDetails,string MeetingPoint,string Notes,byte[]? Photo=null);
public sealed record RideDto(PostDto Post,string Origin,string Destination,int Days,
    int DepartureMinutes,int? ReturnMinutes,bool ReturnNextDay,int Seats,int PriceCents,
    bool AllowsDetour,string DetourDetails,string MeetingPoint,string Notes);
public sealed record RidePageResult(RideDto[] Items,long? NextBefore);

public static class RideRules
{
    public static readonly string[] DayNames=["Lun","Mar","Mié","Jue","Vie","Sáb","Dom"];
    public static string DaysText(int mask)=>string.Join(" · ",DayNames.Where((_,i)=>(mask & (1<<i))!=0));
    public static string TimeText(int minutes)=>$"{minutes/60:00}:{minutes%60:00}";
    public static CreateRideRequest Normalize(CreateRideRequest r)=>r with
    {Origin=r.Origin?.Trim()??"",Destination=r.Destination?.Trim()??"",MeetingPoint=r.MeetingPoint?.Trim()??"",
        Notes=r.Notes?.Trim()??"",DetourDetails=r.AllowsDetour?r.DetourDetails?.Trim()??"":""};
    public static string? Validate(CreateRideRequest r)
    {
        if(r.RequestId==Guid.Empty)return "Identificador de envío no válido.";
        if(r.Origin.Length is <2 or >80 || r.Destination.Length is <2 or >80)return "Indica origen y destino de 2 a 80 caracteres.";
        if(string.Equals(r.Origin,r.Destination,StringComparison.OrdinalIgnoreCase))return "El origen y el destino deben ser distintos.";
        if(r.Days is <1 or >127)return "Selecciona al menos un día de la semana.";
        if(r.DepartureMinutes is <0 or >1439 || r.ReturnMinutes is <0 or >1439)return "Indica una hora válida.";
        if(r.ReturnMinutes is null && r.ReturnNextDay)return "Indica la hora de vuelta o desactiva la vuelta al día siguiente.";
        if(r.ReturnMinutes is {} back && !r.ReturnNextDay && back<=r.DepartureMinutes)return "La vuelta debe ser posterior a la ida; si es al día siguiente, indícalo.";
        if(r.Seats is <1 or >8)return "Indica entre 1 y 8 plazas para pasajeros.";
        if(r.PriceCents is <0 or >1000000)return "Indica un precio entre 0 y 10.000 € por asiento y sentido.";
        if(r.DetourDetails.Length>300 || r.MeetingPoint.Length>200 || r.Notes.Length>1000)return "Acorta el punto de encuentro, los detalles del desvío o las observaciones.";
        if(r.Photo is not null&&!MarketPhotoRules.IsValid(r.Photo))return "La foto debe ser un JPEG válido de hasta 40 KB.";
        return null;
    }
}
