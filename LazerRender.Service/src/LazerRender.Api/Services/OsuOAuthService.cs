using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LazerRender.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace LazerRender.Api.Services;

public sealed record OsuTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] long ExpiresIn,
    [property: JsonPropertyName("token_type")] string TokenType);

public sealed record OsuUserResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl,
    [property: JsonPropertyName("country_code")] string? CountryCode);

/// <summary>
/// An osu! API v2 access token together with its remaining validity (in seconds). Used both for the
/// configured bot credential and for a signed-in player's own token.
/// </summary>
public sealed record OsuAccessToken(string AccessToken, long ExpiresIn);

/// <summary>
/// osu! API v2 OAuth client. Implements the authorization-code grant for user login and the
/// <c>GET /api/v2/me</c> identity lookup. Endpoint paths and scopes follow the official docs
/// (<see href="https://osu.ppy.sh/docs/"/>); credentials come from configuration only.
/// </summary>
public sealed class OsuOAuthService
{
    private readonly OsuOAuthOptions options;
    private readonly HttpClient http;

    public OsuOAuthService(IOptions<OsuOAuthOptions> options, HttpClient http)
    {
        this.options = options.Value;
        this.http = http;
    }

    /// <summary>
    /// Builds the authorize URL the browser is redirected to.
    ///
    /// The scope list is built by hand and appended verbatim rather than going through
    /// <see cref="QueryString.Create"/>, which would encode the separator as <c>%20</c>. osu!'s
    /// authorize endpoint answers a space-encoded list with "Invalid request parameter / The client is
    /// not authorized", while the <c>+</c>-separated form (the one osu!'s own documentation uses) is
    /// accepted. A configured list may be separated by spaces, commas or <c>+</c>.
    /// </summary>
    public string BuildAuthorizeUrl(string state)
    {
        var query = QueryString.Create(new[]
        {
            new KeyValuePair<string, string?>("client_id", options.ClientId),
            new KeyValuePair<string, string?>("redirect_uri", options.RedirectUri),
            new KeyValuePair<string, string?>("response_type", "code"),
            new KeyValuePair<string, string?>("state", state),
        });

        return options.AuthorizeUrl + query.ToUriComponent() + "&scope=" + encodeScopes(options.Scopes);
    }

    /// <summary>
    /// Normalises a configured scope list into osu!'s <c>+</c>-separated form. Scope names are a safe
    /// alphabet, so only the wildcard is passed through unescaped along with the separator.
    /// </summary>
    private static string encodeScopes(string scopes) => string.Join('+', scopes
        .Split(new[] { ' ', ',', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(scope => scope == "*" ? "*" : Uri.EscapeDataString(scope)));

    public async Task<OsuTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["redirect_uri"] = options.RedirectUri,
            ["code"] = code,
        });

        return await PostTokenAsync(content, ct);
    }

    public async Task<OsuTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["refresh_token"] = refreshToken,
        });

        return await PostTokenAsync(content, ct);
    }

    public async Task<OsuUserResponse> GetPublicUserAsync(string username, string bearerToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            options.ApiBaseUrl + "/users/" + Uri.EscapeDataString(username));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.UserAgent.ParseAdd(options.UserAgent);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<OsuUserResponse>(stream, cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty response from osu! users endpoint.");
    }

    public async Task<OsuUserResponse> GetMeAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.ApiBaseUrl + "/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.UserAgent.ParseAdd(options.UserAgent);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<OsuUserResponse>(stream, cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty response from osu! /me.");
    }

    private async Task<OsuTokenResponse> PostTokenAsync(FormUrlEncodedContent content, CancellationToken ct)
    {
        using var response = await http.PostAsync(options.TokenUrl, content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"osu! token endpoint returned {(int)response.StatusCode}: {body}");

        return JsonSerializer.Deserialize<OsuTokenResponse>(body)
            ?? throw new InvalidOperationException("Empty token response from osu!.");
    }
}
