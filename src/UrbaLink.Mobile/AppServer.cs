using System.Reflection;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

internal static class AppServer
{
    public static Uri Address { get; } = ReadAddress();

    private static Uri ReadAddress()
    {
        var configured = typeof(AppServer).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "UrbaLinkServerUrl").Value;
#if DEBUG
        const bool allowHttp = true;
#else
        const bool allowHttp = false;
#endif
        if (!ServerAddress.TryParse(configured, allowHttp, out var address))
            throw new InvalidOperationException("La dirección de UrbaLink configurada al compilar no es válida.");
        return address!;
    }
}
