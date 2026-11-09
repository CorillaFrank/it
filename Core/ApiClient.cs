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
    // Backend desplegado en Render.
    // Para volver al backend local sin recompilar:
    //   set HAMUL_API_URL=http://localhost:5118
    private const string DefaultBaseUrl = "https://hamulapi.onrender.com/";

    private static readonly string BaseUrl = ResolveBaseUrl();
    private static readonly object TokenSync = new();
    private static readonly object LogSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri(BaseUrl),
        // El plan gratuito de Render duerme el servicio tras un rato inactivo y
        // el primer login puede tardar ~50 s en despertarlo. Con 10 s siempre
        // agotaba el tiempo de espera.
        Timeout = TimeSpan.FromSeconds(90)
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

    public static Task<ApiResult<T>> GetAsync<T>(string path, CancellationToken cancellationToken = default) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);

    public static Task<ApiResult<TRes>> PostAsync<TReq, TRes>(
        string path,
        TReq body,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        return SendAsync<TRes>(request, cancellationToken);
    }

    public static Task<ApiResult<TRes>> PostMultipartAsync<TRes>(
        string path,
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content
        };
        return SendAsync<TRes>(request, cancellationToken);
    }

    /// <summary>DELETE sin cuerpo de respuesta (204 No Content = éxito).</summary>
    public static Task<ApiResult<bool>> DeleteAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        SendNoBodyAsync(new HttpRequestMessage(HttpMethod.Delete, path), cancellationToken);

    private static async Task<ApiResult<bool>> SendNoBodyAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    string? saveError = ClearSavedToken();
                    AuthenticationRequired?.Invoke(null, EventArgs.Empty);

                    return ApiResult<bool>.Failure(
                        saveError ?? "La sesión expiró. Inicia sesión nuevamente.",
                        response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    string body = await ReadBodyAsync(response.Content, cancellationToken)
                        .ConfigureAwait(false);

                    WriteErrorLog(response.StatusCode, request.RequestUri, body);

                    return ApiResult<bool>.Failure(
                        BuildErrorMessage(response.StatusCode, body),
                        response.StatusCode);
                }

                return ApiResult<bool>.Success(true, response.StatusCode);
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<bool>.Failure("La operación se canceló.");
            }
            catch (TaskCanceledException ex)
            {
                return ApiResult<bool>.Failure(
                    $"La API en {BaseUrl.TrimEnd('/')} no respondió a tiempo. Detalle: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                return ApiResult<bool>.Failure(
                    $"No se pudo conectar con la API en {BaseUrl.TrimEnd('/')}. {Describe(ex)}");
            }
        }
    }

    private static async Task<ApiResult<T>> SendAsync<T>(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            try
            {
                using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Un 401 del propio login son credenciales incorrectas, no una
                    // sesión expirada: no dispares el prompt de autenticación ni
                    // borres un token que ya era válido.
                    bool isLoginRequest = request.RequestUri?.AbsolutePath
                        .Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) == true;

                    if (isLoginRequest)
                        return ApiResult<T>.Failure("Email o contraseña incorrectos.", response.StatusCode);

                    string? saveError = ClearSavedToken();
                    AuthenticationRequired?.Invoke(null, EventArgs.Empty);

                    return ApiResult<T>.Failure(
                        saveError ?? "La sesión expiró. Inicia sesión nuevamente.",
                        response.StatusCode);
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Leer el cuerpo REAL del error: Render/Aiven suelen devolver
                    // un JSON o un texto con el detalle (SSL, BD caída, etc.).
                    string responseBody = await ReadBodyAsync(response.Content, cancellationToken)
                        .ConfigureAwait(false);

                    WriteErrorLog(response.StatusCode, request.RequestUri, responseBody);

                    return ApiResult<T>.Failure(
                        BuildErrorMessage(response.StatusCode, responseBody),
                        response.StatusCode);
                }

                var data = await response.Content
                    .ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                return data is null
                    ? ApiResult<T>.Failure("La API devolvió una respuesta vacía.", response.StatusCode)
                    : ApiResult<T>.Success(data, response.StatusCode);
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ApiResult<T>.Failure("La operación se canceló.");
            }
            catch (TaskCanceledException ex)
            {
                return ApiResult<T>.Failure(
                    $"La API en {BaseUrl.TrimEnd('/')} no respondió a tiempo. " +
                    "Si el servicio estaba dormido, espera unos segundos y reintenta. " +
                    $"Detalle: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                return ApiResult<T>.Failure(
                    $"No se pudo conectar con la API en {BaseUrl.TrimEnd('/')}. {Describe(ex)}");
            }
            catch (JsonException ex)
            {
                return ApiResult<T>.Failure($"La respuesta de la API no tiene un formato válido: {ex.Message}");
            }
        }
    }

    private static string ResolveBaseUrl()
    {
        var candidate = Environment.GetEnvironmentVariable("HAMUL_API_URL");

        if (string.IsNullOrWhiteSpace(candidate))
            candidate = DefaultBaseUrl;

        candidate = candidate.Trim();
        if (!candidate.EndsWith('/'))
            candidate += '/';

        return candidate;
    }

    /// <summary>
    /// Incluye la excepción interna: "An error occurred while sending the request"
    /// por sí solo no dice nada; el inner trae el motivo real (connection refused,
    /// certificado, DNS, TLS...).
    /// </summary>
    private static string Describe(Exception exception)
    {
        var message = exception.Message;
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            message += $" → {inner.Message}";

        return message;
    }

    /// <summary>Ruta del log compartido de Hamul.</summary>
    private static string LogPath => Path.Combine(Config.Dir, "hamul.log");

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Si el cuerpo no se puede leer, seguimos mostrando al menos el status.
            return "";
        }
    }

    /// <summary>
    /// Arma el mensaje que verá el usuario: incluye el status y el texto que
    /// envió el servidor. Si es JSON, adelanta el campo de mensaje conocido.
    /// </summary>
    private static string BuildErrorMessage(HttpStatusCode statusCode, string body)
    {
        string statusLine = $"La API respondió con el estado {(int)statusCode} ({statusCode}).";
        string detail = (body ?? "").Trim();

        if (detail.Length == 0)
            return $"{statusLine} El servidor no envió ningún cuerpo de respuesta.";

        string? extracted = TryExtractMessage(detail);

        // Un HTML de error de Render puede ser enorme y romper la ventana de
        // login. En la UI mostramos un recorte; el log guarda el texto completo.
        const int maxUiLength = 1200;
        string shown = detail.Length > maxUiLength
            ? detail[..maxUiLength] + $"… (recortado; completo en {LogPath})"
            : detail;

        return extracted is null
            ? $"{statusLine} Respuesta del servidor: {shown}"
            : $"{statusLine} Mensaje del servidor: {extracted}{Environment.NewLine}" +
              $"Respuesta completa: {shown}";
    }

    /// <summary>Intenta sacar "message"/"detail"/"error"/"title" de un JSON.</summary>
    private static string? TryExtractMessage(string body)
    {
        if (!body.TrimStart().StartsWith('{'))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            foreach (var propertyName in new[] { "message", "detail", "error", "title" })
            {
                if (!root.TryGetProperty(propertyName, out var property))
                    continue;

                return property.ValueKind switch
                {
                    JsonValueKind.String => property.GetString(),
                    JsonValueKind.Object or JsonValueKind.Array => property.GetRawText(),
                    _ => property.ToString()
                };
            }
        }
        catch (JsonException)
        {
            // No era JSON válido: se mostrará el texto tal cual.
        }

        return null;
    }

    /// <summary>Guarda el cuerpo exacto del error en %APPDATA%\HamulProto\hamul.log.</summary>
    private static void WriteErrorLog(HttpStatusCode statusCode, Uri? uri, string body)
    {
        try
        {
            lock (LogSync)
            {
                Directory.CreateDirectory(Config.Dir);
                File.AppendAllText(
                    LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] API {(int)statusCode} ({statusCode}) {uri}{Environment.NewLine}" +
                    $"Cuerpo de la respuesta:{(string.IsNullOrWhiteSpace(body) ? " (vacío)" : "")}{Environment.NewLine}" +
                    $"{body}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // El log nunca debe tumbar la petición.
        }
    }

    private static string? ClearSavedToken()
    {
        Config.Current.AuthToken = null;
        SetToken(null);
        Config.NotifySessionChanged();

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
