using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public partial class MainPage : ContentPage
{
    private Uri? verifiedServer;
    private bool attemptedAutomaticConnection;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(8) };
#if DEBUG
    private const bool AllowHttp = true;
#else
    private const bool AllowHttp = false;
#endif

    public MainPage()
    {
        InitializeComponent();
        var defaultAddress = DeviceInfo.DeviceType == DeviceType.Virtual
            ? "http://10.0.2.2:5080" : "";
        ServerEntry.Text = Preferences.Default.Get("server_address", AllowHttp ? defaultAddress : "");
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!attemptedAutomaticConnection && Preferences.Default.ContainsKey("server_address"))
        {
            attemptedAutomaticConnection = true;
            await Connect(true);
        }
    }

    private async void OnConnectClicked(object? sender, EventArgs e) => await Connect(true);

    private async Task Connect(bool enter)
    {
        ContinueButton.IsVisible = false;
        verifiedServer = null;
        if (!ServerAddress.TryParse(ServerEntry.Text, AllowHttp, out var address))
        {
            StatusLabel.Text = AllowHttp
                ? "Introduce una dirección http:// o https:// sin rutas, usuario ni parámetros."
                : "Introduce una dirección HTTPS sin rutas, usuario ni parámetros.";
            return;
        }

        ConnectButton.IsEnabled = false;
        ServerEntry.IsEnabled = false;
        BusyIndicator.IsVisible = BusyIndicator.IsRunning = true;
        StatusLabel.Text = "Conectando…";
        try
        {
            var live = await Client.GetFromJsonAsync<ServerStatus>(new Uri(address!, "health/live"));
            if (live is not { Service: "UrbaLink", Status: "alive" })
            {
                StatusLabel.Text = "La dirección responde, pero no corresponde a la API de UrbaLink.";
                return;
            }

            Preferences.Default.Set("server_address", address!.AbsoluteUri);
            using var response = await Client.GetAsync(new Uri(address, "health/ready"));
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.ServiceUnavailable)
                response.EnsureSuccessStatusCode();

            var ready = await response.Content.ReadFromJsonAsync<ServerStatus>();
            if (response.IsSuccessStatusCode && ready is { Service: "UrbaLink", Status: "ready" })
            {
                verifiedServer = address;
                ContinueButton.IsVisible = true;
            }
            StatusLabel.Text = ready switch
            {
                { Service: "UrbaLink", Status: "ready" } when response.IsSuccessStatusCode
                    => "Conectado. La API y PostgreSQL están disponibles.",
                { Service: "UrbaLink", Status: "database_not_configured" }
                    => "La API funciona. Falta configurar la conexión a PostgreSQL en el servidor.",
                { Service: "UrbaLink", Status: "database_unavailable" }
                    => "La API funciona, pero no consigue conectar con PostgreSQL.",
                { Service: "UrbaLink", Status: "database_schema_outdated" }
                    => "El servidor necesita actualizar su base de datos antes de continuar.",
                _ => "La API responde con un estado desconocido."
            };
        }
        catch (TaskCanceledException)
        {
            StatusLabel.Text = "Se agotó el tiempo de espera. Comprueba la IP, la Wi-Fi y que el servidor esté encendido.";
        }
        catch (HttpRequestException)
        {
            StatusLabel.Text = "No se pudo completar la conexión. Revisa la dirección, el servidor y el firewall.";
        }
        catch (JsonException)
        {
            StatusLabel.Text = "El servidor devolvió una respuesta que no corresponde a UrbaLink.";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ServerEntry.IsEnabled = true;
            BusyIndicator.IsVisible = BusyIndicator.IsRunning = false;
        }
        if (enter && verifiedServer is not null && Navigation.NavigationStack.LastOrDefault() == this)
            await Navigation.PushAsync(new AccountPage(verifiedServer));
    }

    private async void OnContinueClicked(object? sender, EventArgs e)
    {
        if (verifiedServer is not null)
            await Navigation.PushAsync(new AccountPage(verifiedServer));
    }
}
