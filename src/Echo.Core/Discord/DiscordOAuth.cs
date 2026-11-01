using System.Text.Json;

namespace Echo.Core.Discord;

/// <summary>OAuth2 tokens of the user's Discord application.</summary>
public sealed record DiscordTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>Expired, or about to (renewed a day ahead).</summary>
    public bool NeedsRefresh(DateTimeOffset now) => now >= ExpiresAt - TimeSpan.FromDays(1);

    public string ToJson() => JsonSerializer.Serialize(this);

    public static DiscordTokens? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var tokens = JsonSerializer.Deserialize<DiscordTokens>(json);
            return tokens is { AccessToken.Length: > 0 } ? tokens : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Discord refused the authorization (wrong secret, revoked access…).</summary>
public sealed class DiscordAuthException(string message, string? error = null) : Exception(message)
{
    /// <summary>OAuth2 error code ("invalid_client", "invalid_grant"…).</summary>
    public string? Error { get; } = error;
}

/// <summary>
/// Turns the code given by the Discord app (AUTHORIZE) into tokens, and renews them.
/// Only these two requests go to discord.com; everything else stays on the PC.
/// </summary>
public sealed class DiscordOAuth(HttpClient http)
{
    public const string TokenUrl = "https://discord.com/api/oauth2/token";

    /// <summary>Redirect to declare in the Discord application (OAuth2 → Redirects).</summary>
    public const string RedirectUri = "http://localhost";

    public async Task<DiscordTokens> ExchangeCodeAsync(string clientId, string clientSecret, string code, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
        };
        try
        {
            return await PostAsync(form, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordAuthException ex) when (ex.Message.Contains("redirect", StringComparison.OrdinalIgnoreCase))
        {
            // Application without the http://localhost redirect: try without it.
            form.Remove("redirect_uri");
            return await PostAsync(form, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<DiscordTokens> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken cancellationToken) =>
        PostAsync(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, cancellationToken);

    private async Task<DiscordTokens> PostAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(TokenUrl, content, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new DiscordAuthException($"Réponse inattendue de Discord (HTTP {(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access))
        {
            string? error = root.TryGetProperty("error", out var e) ? e.ToString() : null;
            string? description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            throw new DiscordAuthException(Describe(error, description, (int)response.StatusCode), error);
        }

        string refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? string.Empty : string.Empty;
        int expiresIn = root.TryGetProperty("expires_in", out var x) && x.TryGetInt32(out int s) ? s : 604800;
        return new DiscordTokens(access.GetString()!, refresh, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    private static string Describe(string? error, string? description, int status) => error switch
    {
        "invalid_client" => "Client Secret refusé par Discord : copiez-le à nouveau depuis le portail développeur (OAuth2).",
        "invalid_grant" when description?.Contains("redirect", StringComparison.OrdinalIgnoreCase) == true =>
            $"Ajoutez la redirection {RedirectUri} dans le portail développeur Discord (OAuth2 → Redirects).",
        "invalid_grant" => "Autorisation Discord expirée ou révoquée : reconnectez Echo à Discord.",
        _ => $"Discord a refusé l'autorisation ({description ?? error ?? $"HTTP {status}"}).",
    };
}
