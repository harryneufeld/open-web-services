using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Hetzner;

/// <summary>Thin HTTP wrapper around the Hetzner Cloud REST API (https://api.hetzner.cloud/v1/).</summary>
internal sealed class HetznerApiClient(HttpClient http, string token)
{
    private const int PageSize = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions JsonWithNulls = new(Json)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Reads every page of a list endpoint, e.g. <c>server_types</c>.</summary>
    public async Task<List<T>> GetAllAsync<T>(string path, string collection, CancellationToken ct)
    {
        var items = new List<T>();
        for (int? page = 1; page is not null;)
        {
            var body = await SendAsync(HttpMethod.Get, $"{path}?per_page={PageSize}&page={page}", null, ct)
                       ?? throw new ProviderException($"Hetzner returned no data for '{path}'.");
            using var document = JsonDocument.Parse(body);
            items.AddRange(document.RootElement.GetProperty(collection).Deserialize<List<T>>(Json) ?? []);
            page = ReadNextPage(document.RootElement);
        }

        return items;
    }

    /// <summary>Returns null when the resource does not exist (HTTP 404).</summary>
    public async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        var body = await SendAsync(HttpMethod.Get, path, null, ct, allowNotFound: true);
        return body is null ? null : JsonSerializer.Deserialize<T>(body, Json);
    }

    public async Task<T> PostAsync<T>(string path, object? payload, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Post, path, payload, ct)
                   ?? throw new ProviderException($"Hetzner returned no data for '{path}'.");
        return JsonSerializer.Deserialize<T>(body, Json)
               ?? throw new ProviderException($"Hetzner returned an empty response for '{path}'.");
    }

    public Task PostAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Post, path, null, ct);

    /// <param name="includeNulls">Send null properties instead of omitting them, where null has a meaning.</param>
    public Task PostAsync(string path, object payload, CancellationToken ct, bool includeNulls = false) =>
        SendAsync(HttpMethod.Post, path, payload, ct, serializer: includeNulls ? JsonWithNulls : Json);

    /// <summary>Performs a GET and discards the body; throws <see cref="ProviderException"/> on any error.</summary>
    public Task GetRawAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    public Task DeleteAsync(string path, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, path, null, ct, allowNotFound: true);

    private async Task<string?> SendAsync(
        HttpMethod method, string path, object? payload, CancellationToken ct, bool allowNotFound = false,
        JsonSerializerOptions? serializer = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, payload.GetType(), options: serializer ?? Json);

        try
        {
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
                return null;
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ProviderException("Hetzner rejected the API token (401). Check or replace it under Groups.");
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"Hetzner API error ({(int)response.StatusCode}): {ReadErrorMessage(body)}");
            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("The Hetzner API could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("The Hetzner API did not respond in time.", ex);
        }
    }

    private static int? ReadNextPage(JsonElement root) =>
        root.TryGetProperty("meta", out var meta)
        && meta.TryGetProperty("pagination", out var pagination)
        && pagination.TryGetProperty("next_page", out var next)
        && next.ValueKind == JsonValueKind.Number
            ? next.GetInt32()
            : null;

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return string.IsNullOrWhiteSpace(body) ? "no details" : body;
        }
    }
}
