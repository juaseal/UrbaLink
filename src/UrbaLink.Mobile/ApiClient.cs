using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using UrbaLink.Contracts;

namespace UrbaLink.Mobile;

public sealed class ApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class ApiClient(Uri server)
{
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
    public string? Token { get; set; }

    public async Task<T> Send<T>(HttpMethod method, string path, object? data = null, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(method, new Uri(server, path));
        if (Token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (data is not null) request.Content = JsonContent.Create(data);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Demasiados intentos. Espera un minuto.",
                HttpStatusCode.Unauthorized => "Inicia sesión de nuevo o comprueba tus credenciales.",
                HttpStatusCode.Forbidden => "No tienes permiso para realizar esta acción.",
                _ => "No se pudo completar la operación."
            };
            try { message = (await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken: cancellationToken))?.Message ?? message; }
            catch (JsonException) { }
            throw new ApiException(response.StatusCode, message);
        }
        if (response.StatusCode == HttpStatusCode.NoContent) return default!;
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)) ?? throw new JsonException();
    }
}
