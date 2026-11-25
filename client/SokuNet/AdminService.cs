using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SokuNet;

internal sealed record AdminParticipant(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("firstConnectedAt")] DateTimeOffset FirstConnectedAt,
    [property: JsonPropertyName("lastConnectedAt")] DateTimeOffset LastConnectedAt,
    [property: JsonPropertyName("isOnline")] bool IsOnline);

internal sealed record AdminAuthFailure(
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("attemptedAt")] DateTimeOffset AttemptedAt,
    [property: JsonPropertyName("attemptNumber")] int AttemptNumber,
    [property: JsonPropertyName("password")] string Password);

internal sealed class AdminAuthenticationException : Exception { }
internal sealed class AdminNotConfiguredException : Exception { }
internal sealed class AdminEndpointUnavailableException : Exception { }

internal static class AdminService
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<AdminParticipant>> GetParticipantsAsync(
        string relayUrl,
        string adminPassword,
        string installationId,
        string displayName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(relayUrl, "/admin/participants", adminPassword, installationId, displayName);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
        EnsureAdminResponse(response);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
        List<AdminParticipant>? participants = await JsonSerializer.DeserializeAsync<List<AdminParticipant>>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(true);
        return participants is null ? Array.Empty<AdminParticipant>() : participants;
    }

    public static async Task<IReadOnlyList<AdminAuthFailure>> GetAuthFailuresAsync(
        string relayUrl,
        string adminPassword,
        string installationId,
        string displayName,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(relayUrl, "/admin/auth-failures", adminPassword, installationId, displayName);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(true);
        EnsureAdminResponse(response);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
        List<AdminAuthFailure>? failures = await JsonSerializer.DeserializeAsync<List<AdminAuthFailure>>(
            stream, JsonOptions, cancellationToken).ConfigureAwait(true);
        return failures is null ? Array.Empty<AdminAuthFailure>() : failures;
    }

    private static HttpRequestMessage CreateRequest(
        string relayUrl, string path, string adminPassword, string installationId, string displayName)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpointUri(relayUrl, path));
        // Base64-encode the UTF-8 value for transport so even spaces or non-ASCII
        // text entered by mistake can reach the Worker and be logged if confirmed.
        string encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(adminPassword));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", encodedPassword);
        request.Headers.TryAddWithoutValidation("X-SokuNet-Admin-Password-Encoding", "base64-utf8");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        request.Headers.TryAddWithoutValidation("X-SokuNet-Installation-Id", installationId);
        string displayNameBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(displayName ?? string.Empty));
        request.Headers.TryAddWithoutValidation("X-SokuNet-Display-Name", displayNameBase64);
        // OpenAdminWindowAsync creates admin requests only after both confirmation dialogs.
        request.Headers.TryAddWithoutValidation("X-SokuNet-Input-Confirmed", "twice");
        return request;
    }

    private static void EnsureAdminResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new AdminAuthenticationException();
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
            throw new AdminNotConfiguredException();
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new AdminEndpointUnavailableException();
        response.EnsureSuccessStatusCode();
    }

    private static Uri BuildEndpointUri(string relayUrl, string endpointPath)
    {
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out Uri? uri) ||
            uri.UserInfo.Length > 0 || uri.Scheme is not ("https" or "wss"))
            throw new UriFormatException("管理者用APIには有効なHTTPSサーバーURLが必要です。");

        var builder = new UriBuilder(uri) { Scheme = "https" };
        if (builder.Port == 443)
            builder.Port = -1;

        string path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
            path = path[..^3];
        builder.Path = $"{path}{endpointPath}";
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        return builder.Uri;
    }
}
