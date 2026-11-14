using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OpenWebServices.Application.Abstractions;

namespace OpenWebServices.Infrastructure.Ovh;

/// <summary>OVHcloud API v1 client. Each request is signed with the account's application and consumer keys.</summary>
internal sealed class OvhApiClient(
    HttpClient http, ILogger<OvhApiClient> logger, string applicationKey, string applicationSecret, string consumerKey)
{
    private static readonly HttpStatusCode[] NotFoundOnly = [HttpStatusCode.NotFound];
    private static readonly TimeSpan ClockOffsetLifetime = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _clockGate = new(1, 1);
    private ClockOffset? _clock;

    /// <summary>Escapes a value for use as one URL path segment or query value.</summary>
    public static string Escape(string value) => Uri.EscapeDataString(value);

    public async Task<List<T>> GetAllAsync<T>(string path, CancellationToken ct) =>
        await GetAsync<List<T>>(path, ct) ?? throw new ProviderException($"OVHcloud returned no data for '{path}'.");

    /// <summary>Returns null when the resource does not exist and <paramref name="allowNotFound"/> is set.</summary>
    public Task<T?> GetAsync<T>(string path, CancellationToken ct, bool allowNotFound = false) where T : class =>
        GetAsync<T>(path, ct, allowNotFound ? NotFoundOnly : []);

    /// <summary>Returns null when OVH answers with one of <paramref name="missingWhen"/> instead of failing.</summary>
    public async Task<T?> GetAsync<T>(string path, CancellationToken ct, IReadOnlyCollection<HttpStatusCode> missingWhen)
        where T : class
    {
        var body = await SendAsync(HttpMethod.Get, path, null, ct, missingWhen);
        return body is null ? null : Deserialize<T>(body, path);
    }

    /// <summary>Reads an unauthenticated endpoint, such as the public order catalog.</summary>
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

    public Task DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct, NotFoundOnly);

    /// <summary>Performs a GET and discards the body; throws <see cref="ProviderException"/> on any error.</summary>
    public Task GetRawAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    /// <summary>OVH signature: "$1$" + SHA1(secret+consumer+method+url+body+timestamp), hex encoded.</summary>
    internal static string Sign(string secret, string consumer, string method, string url, string body, long timestamp)
    {
        var preimage = string.Join("+", secret, consumer, method, url, body, timestamp.ToString(CultureInfo.InvariantCulture));
        return "$1$" + Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(preimage)));
    }

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
        HttpMethod method, string path, object? payload, CancellationToken ct,
        IReadOnlyCollection<HttpStatusCode>? missingWhen = null)
    {
        var body = payload is null ? "" : JsonSerializer.Serialize(payload, Json);
        using var request = new HttpRequestMessage(method, new Uri(http.BaseAddress!, path));
        if (payload is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        try
        {
            var timestamp = await GetTimestampAsync(ct);
            request.Headers.Add("X-Ovh-Application", applicationKey);
            request.Headers.Add("X-Ovh-Consumer", consumerKey);
            request.Headers.Add("X-Ovh-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-Ovh-Signature",
                Sign(applicationSecret, consumerKey, method.Method, request.RequestUri!.AbsoluteUri, body, timestamp));

            using var response = await http.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return responseBody;

            var status = (int)response.StatusCode;
            var reason = ReadErrorMessage(responseBody);
            var tolerated = missingWhen?.Contains(response.StatusCode) == true;
            logger.Log(tolerated ? LogLevel.Information : LogLevel.Warning,
                "OVHcloud answered {Method} {Path} with {Status}: {Reason} (query id {QueryId})",
                method.Method, path, status, reason, QueryId(response));
            if (tolerated)
                return null;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(
                    $"OVHcloud refused {method.Method} {path} ({status}): {reason}. " +
                    "Check the application and consumer keys and the access rules granted to the consumer key.");
            throw new ProviderException($"OVHcloud API error on {method.Method} {path} ({status}): {reason}");
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

    /// <summary>Signed requests must carry the API server's clock, so the local offset to it is cached briefly.</summary>
    private async Task<long> GetTimestampAsync(CancellationToken ct)
    {
        if (_clock is { } cached && cached.IsValid)
            return cached.Now;

        await _clockGate.WaitAsync(ct);
        try
        {
            if (_clock is { } afterWait && afterWait.IsValid)
                return afterWait.Now;

            using var response = await http.GetAsync("auth/time", ct);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException($"Could not read the OVHcloud API clock ({(int)response.StatusCode}).");
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var serverTime))
                throw new ProviderException("OVHcloud returned an invalid API timestamp.");

            var clock = new ClockOffset(serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow + ClockOffsetLifetime);
            _clock = clock;
            return clock.Now;
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException("The OVHcloud API clock could not be reached.", ex);
        }
        finally
        {
            _clockGate.Release();
        }
    }

    private static string? QueryId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Ovh-QueryId", out var values) ? values.FirstOrDefault() : null;

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? "no details"
                : body;
        }
        catch (JsonException)
        {
            return string.IsNullOrWhiteSpace(body) ? "no details" : body;
        }
    }

    private sealed record ClockOffset(long Seconds, DateTimeOffset ExpiresAt)
    {
        public bool IsValid => DateTimeOffset.UtcNow < ExpiresAt;
        public long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Seconds;
    }
}
