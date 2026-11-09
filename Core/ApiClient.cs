using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security;
using System.Text.Json;

namespace HamulProto;

public sealed class ApiResult<T>
{
    private ApiResult(bool isSuccess, T? data, string? error, HttpStatusCode? statusCode)
    {
        IsSuccess = isSuccess;
        Data = data;
        Error = error;
        StatusCode = statusCode;
    }

    public bool IsSuccess { get; }
    public T? Data { get; }
    public string? Error { get; }
    public HttpStatusCode? StatusCode { get; }

    internal static ApiResult<T> Success(T data, HttpStatusCode statusCode) =>
        new(true, data, null, statusCode);

    internal static ApiResult<T> Failure(string error, HttpStatusCode? statusCode = null) =>
        new(false, default, error, statusCode);
}

public static class ApiClient
{
    private const string BaseUrl = "http://localhost:5118/";
    private static readonly object TokenSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri(BaseUrl),
        Timeout = TimeSpan.FromSeconds(10)
    };

    public static event EventHandler? AuthenticationRequired;

    public static void SetToken(string? token)
    {
        lock (TokenSync)
        {
            if (string.IsNullOrWhiteSpace(token))
                Client.DefaultRequestHeaders.Authorization = null;
            else
                Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public static Task<ApiResult<T>> GetAsync<T>(string path) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path));

    public static Task<ApiResult<TRes>> PostAsync<TReq, TRes>(string path, TReq body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        return SendAsync<TRes>(request);
    }

    private static async Task<ApiResult<T>> SendAsync<T>(HttpRequestMessage request)
    {
        using (request)
        {
            try
            {
                using var response = await Client.SendAsync(request).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    string? saveError = ClearSavedToken();
                    AuthenticationRequired?.Invoke(null, EventArgs.Empty);
                    return ApiResult<T>.Failure(
                        saveError ?? "La sesión expiró. Inicia sesión nuevamente.",
                        response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return ApiResult<T>.Failure(
                        $"La API respondió con el estado {(int)response.StatusCode} ({response.StatusCode}).",
                        response.StatusCode);
                }

                var data = await response.Content.ReadFromJsonAsync<T>(JsonOptions).ConfigureAwait(false);
                return data is null
                    ? ApiResult<T>.Failure("La API devolvió una respuesta vacía.", response.StatusCode)
                    : ApiResult<T>.Success(data, response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                return ApiResult<T>.Failure($"No se pudo conectar con la API: {ex.Message}");
            }
            catch (TaskCanceledException ex)
            {
                return ApiResult<T>.Failure($"La solicitud a la API agotó el tiempo de espera: {ex.Message}");
            }
            catch (JsonException ex)
            {
                return ApiResult<T>.Failure($"La respuesta de la API no tiene un formato válido: {ex.Message}");
            }
        }
    }

    private static string? ClearSavedToken()
    {
        Config.Current.AuthToken = null;
        SetToken(null);

        try
        {
            Config.Current.Save();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException)
        {
            return $"La sesión expiró, pero no se pudo guardar el cierre de sesión: {ex.Message}";
        }
    }
}
