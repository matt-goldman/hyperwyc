using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hyperwyc.Interfaces;

namespace Hyperwyc.Sample.Maui.Services;

public class AuthenticationService(
    IHttpClientFactory clientFactory,
    IHyperwyc hyperwyc)
{
    private readonly HttpClient _client = clientFactory.CreateClient(nameof(AuthenticationService));

    private class LoginResponse
    {
        [JsonPropertyName("tokenType")]
        public required string TokenType { get; set; }
        [JsonPropertyName("accessToken")]
        public required string AccessToken { get; set; }
        [JsonPropertyName("expiresIn")]
        public required int ExpiresIn { get; set; }
        [JsonPropertyName("refreshToken")]
        public required string RefreshToken { get; set; }
    }

    private class StoredToken
    {
        public required string AccessToken { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public required string RefreshToken { get; set; }
    }

    public class LoginStateEventArgs : EventArgs
    {
        public bool IsLoggedIn { get; set; }
    }

    public event EventHandler<LoginStateEventArgs>? LoginStateChanged;

    public async Task RegisterAsync(string email, string password)
    {
        var result = await _client.PostAsJsonAsync("/register", new { email, password });
        result.EnsureSuccessStatusCode();
    }

    public async Task LoginAsync(string email, string password)
    {
        var result = await _client.PostAsJsonAsync("/login", new { email, password });

        result.EnsureSuccessStatusCode();

        var loginResponse = await result.Content.ReadFromJsonAsync<LoginResponse>() ?? throw new Exception("Login failed");

        var storedToken = new StoredToken()
        {
            AccessToken     = loginResponse.AccessToken,
            ExpiresUtc      = DateTime.UtcNow.AddSeconds(loginResponse.ExpiresIn),
            RefreshToken    = loginResponse.RefreshToken
        };

        var storedTokenJson = JsonSerializer.Serialize(storedToken);

        await SecureStorage.Default.SetAsync("token", storedTokenJson);

        LoginStateChanged?.Invoke(this, new LoginStateEventArgs { IsLoggedIn = true });
    }

    public async Task<string?> GetTokenAsync()
    {
        var storedToken = await GetStoredTokenAsync();

        if (storedToken is null)
        {
            return null;
        }

        if (DateTime.UtcNow.AddMinutes(2) <= storedToken.ExpiresUtc)
        {
            return storedToken.AccessToken;
        }

        await RefreshTokenAsync();
        storedToken = await GetStoredTokenAsync();

        return storedToken?.AccessToken;
    }

    public async Task<bool> GetIsLoggedInAsync()
    {
        var storedToken = await GetStoredTokenAsync();
        return storedToken is not null;
    }

    public async Task Logout()
    {
        // Discard this user's cached responses and anything still queued, before the token
        // goes. ResetStoreAsync waits out any flush already running, so nothing gets written
        // back into the store after it is emptied.
        await hyperwyc.ResetStoreAsync();

        SecureStorage.Default.Remove("token");

        LoginStateChanged?.Invoke(this, new LoginStateEventArgs { IsLoggedIn = false });
        await Application.Current!.Windows[0]!.Page!.DisplayAlertAsync("Logout", "You have been logged out.", "OK");
    }

    private async Task RefreshTokenAsync()
    {
        var storedToken = await GetStoredTokenAsync();

        if (storedToken is null)
        {
            return;
        }

        var refreshResult = await _client.PostAsJsonAsync("/refresh", new { refreshToken = storedToken.RefreshToken });

        refreshResult.EnsureSuccessStatusCode();

        var loginResponse = await refreshResult.Content.ReadFromJsonAsync<LoginResponse>() ?? throw new Exception("Refresh token failed");

        var storedTokenJson = JsonSerializer.Serialize(loginResponse);

        await SecureStorage.Default.SetAsync("token", storedTokenJson);
    }

    private static async Task<StoredToken?> GetStoredTokenAsync()
    {
        var storedToken = await SecureStorage.Default.GetAsync("token");
        if (storedToken is null)
        {
            return null;
        }

        var tokenObj = JsonSerializer.Deserialize<StoredToken>(storedToken);

        return tokenObj ?? null;
    }
}


