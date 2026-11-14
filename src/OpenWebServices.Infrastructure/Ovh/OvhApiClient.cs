using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud API v1 client. Each request is signed with the account's application and consumer keys.</summary>
internal sealed class OvhApiClient(HttpClient http, string applicationKey, string applicationSecret, string consumerKey)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _timeGate = new(1, 1);
    private long? _serverTimeOffsetSeconds;
    private DateTimeOffset _timeOffsetExpiresAt;

    public async Task<List<T>> GetAllAsync<T>(string path, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Get, path, null, ct)
                   ?? throw new ProviderException($"OVHcloud returned no data for '{path}'.");
        try
        {
            return JsonSerializer.Deserialize<List<T>>(body, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new ProviderException($"OVHcloud returned an unexpected response for '{path}': {ex.Message}", ex);
        }
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken ct, bool allowNotFound = false) where T : class
    {
        var body = await SendAsync(HttpMethod.Get, path, null, ct, allowNotFound);
        if (body is null)
            return null!;
        return Deserialize<T>(body, path);
    }

    public async Task<T> GetPublicAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"OVHcloud public catalog error ({(int)response.StatusCode}): {ReadErrorMessage(body)}");
            return Deserialize<T>(body, path);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("The OVHcloud public catalog could not be reached.", ex);
        }
    }

    public async Task<T> PostAsync<T>(string path, object payload, CancellationToken ct)
    {
        var body = await SendAsync(HttpMethod.Post, path, payload, ct)
                   ?? throw new ProviderException($"OVHcloud returned no data for '{path}'.");
        return Deserialize<T>(body, path);
    }

    public Task PostAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Post, path, null, ct);

    public Task PostAsync(string path, object payload, CancellationToken ct) => SendAsync(HttpMethod.Post, path, payload, ct);

    public Task PutAsync(string path, object payload, CancellationToken ct) => SendAsync(HttpMethod.Put, path, payload, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct, allowNotFound: true);

    public async Task GetRawAsync(string path, CancellationToken ct) => _ = await SendAsync(HttpMethod.Get, path, null, ct);

    private static T Deserialize<T>(string body, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, Json)
                   ?? throw new ProviderException($"OVHcloud returned an empty response for '{path}'.");
        }
        catch (JsonException ex)
        {
            throw new ProviderException($"OVHcloud returned an unexpected response for '{path}': {ex.Message}", ex);
        }
    }

    private async Task<string?> SendAsync(
        HttpMethod method, string path, object? payload, CancellationToken ct, bool allowNotFound = false)
    {
        var body = payload is null ? "" : JsonSerializer.Serialize(payload, Json);
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            var timestamp = await GetTimestampAsync(ct);
            var url = request.RequestUri is { IsAbsoluteUri: true } absolute
                ? absolute.AbsoluteUri
                : new Uri(http.BaseAddress!, request.RequestUri!).AbsoluteUri;
            var preimage = string.Join("+", applicationSecret, consumerKey, method.Method, url, body, timestamp.ToString(CultureInfo.InvariantCulture));
            var signature = "$1$" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(preimage))).ToLowerInvariant();
            request.Headers.Add("X-Ovh-Application", applicationKey);
            request.Headers.Add("X-Ovh-Consumer", consumerKey);
            request.Headers.Add("X-Ovh-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-Ovh-Signature", signature);

            using (var response = await http.SendAsync(request, ct))
            {
                var responseBody = await response.Content.ReadAsStringAsync(ct);
                if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound)
                    return null;
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ProviderException("OVHcloud rejected the credentials or API permissions. Check the application and consumer keys.");
                if (!response.IsSuccessStatusCode)
                    throw new ProviderException($"OVHcloud API error ({(int)response.StatusCode}): {ReadErrorMessage(responseBody)}");
                return responseBody;
            }
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("The OVHcloud API could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("The OVHcloud API did not respond in time.", ex);
        }
    }

    private async Task<long> GetTimestampAsync(CancellationToken ct)
    {
        if (_serverTimeOffsetSeconds is { } cached && DateTimeOffset.UtcNow < _timeOffsetExpiresAt)
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + cached;

        await _timeGate.WaitAsync(ct);
        try
        {
            if (_serverTimeOffsetSeconds is { } afterWait && DateTimeOffset.UtcNow < _timeOffsetExpiresAt)
                return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + afterWait;

            using var response = await http.GetAsync("auth/time", ct);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"Could not read the OVHcloud API clock ({(int)response.StatusCode}).");
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var serverTime))
                throw new ProviderException("OVHcloud returned an invalid API timestamp.");

            _serverTimeOffsetSeconds = serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _timeOffsetExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() + _serverTimeOffsetSeconds.Value;
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("The OVHcloud API clock could not be reached.", ex);
        }
        finally
        {
            _timeGate.Release();
        }
    }

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("message", out var message))
                return message.GetString() ?? "no details";
            return body;
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? "no details" : body;
        }
    }
}
