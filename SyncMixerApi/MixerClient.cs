namespace SyncMixerApi;

using CSharpFunctionalExtensions;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class MixerClient : IDisposable
{
    private const string ClientId = "bac69a9240a343458fdec0aa6f97dcd5";
    private const string RedirectUri = "http://127.0.0.1:8000/callback";
    private static readonly string[] Scopes =
    {
        "playlist-read-private",
        "playlist-read-collaborative",
        // "playlist-modify-private",
        // "playlist-modify-public"
    };

    private readonly HttpClient _http;
    private string _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAtUtc;

    private MixerClient(string accessToken, string? refreshToken, DateTimeOffset expiresAtUtc)
    {
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _refreshToken = refreshToken;
        _expiresAtUtc = expiresAtUtc;

        _http = new HttpClient { BaseAddress = new Uri("https://api.spotify.com/v1/") };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        ApplyAuthHeader();
    }

    public static async Task<Result<MixerClient>> Create()
    {
        // 0) PKCE + state
        var (verifier, challenge) = MixerClientHelpers.GeneratePkceCodes();
        var state = MixerClientHelpers.Base64Url(RandomNumberGenerator.GetBytes(32));

        // 1) Authorize URL bouwen
        var authUrl =
            "https://accounts.spotify.com/authorize" +
            "?response_type=code" +
            $"&client_id={Uri.EscapeDataString(ClientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
            $"&code_challenge_method=S256&code_challenge={challenge}" +
            $"&scope={Uri.EscapeDataString(string.Join(" ", Scopes))}" +
            $"&state={Uri.EscapeDataString(state)}";

        // 2) Listener starten (LET OP: trailing slash in prefix)
        var listenerPrefix = MixerClientHelpers.EnsureTrailingSlash(RedirectUri);
        using var listener = new HttpListener();
        listener.Prefixes.Add(listenerPrefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            // Windows tip (als je "Access is denied" krijgt):
            //   netsh http add urlacl url=http://127.0.0.1:8000/callback/ user=Everyone
            return Result.Failure<MixerClient>(
                $"HttpListener kon niet starten op {listenerPrefix}: {ex.Message}\n" +
                "Tip (Windows): voer als admin uit:\n" +
                "  netsh http add urlacl url=http://127.0.0.1:8000/callback/ user=Everyone"
            );
        }

        // 3) Browser openen
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = authUrl, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Fallback: log eventueel de URL
            Console.WriteLine("Open handmatig deze URL om in te loggen:");
            Console.WriteLine(authUrl);
        }

        // 4) Wachten op redirect (timeout 3 min)
        HttpListenerContext ctx;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            listener.Stop();
            return Result.Failure<MixerClient>("Login timeout: geen redirect ontvangen. Controleer of de listener draait en de redirect URI exact matcht.");
        }

        // 5) Query parsen
        var query = System.Web.HttpUtility.ParseQueryString(ctx.Request.Url!.Query);
        var code = query["code"];
        var returnedState = query["state"];
        var error = query["error"];

        // Kleine HTML-reply helper
        static async Task Reply(HttpListenerContext c, string title, string msg, bool autoClose = false)
        {
            var autoCloseScript = autoClose
                ? "<script>window.onload = function(){ window.open('', '_self'); window.close(); }</script>"
                : string.Empty;

            var html =
                $"<html><head><title>{WebUtility.HtmlEncode(title)}</title></head>" +
                $"<body style='font-family:sans-serif'>" +
                $"<h2>{WebUtility.HtmlEncode(title)}</h2>" +
                $"<p>{WebUtility.HtmlEncode(msg)}</p>" +
                $"{autoCloseScript}" +
                $"</body></html>";

            var bytes = Encoding.UTF8.GetBytes(html);
            c.Response.ContentType = "text/html; charset=utf-8";
            c.Response.ContentLength64 = bytes.Length;
            await c.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            c.Response.OutputStream.Close();
        }

        if (!string.IsNullOrEmpty(error))
        {
            await Reply(ctx, "Authorisatie afgebroken", $"error={error}");
            listener.Stop();
            return Result.Failure<MixerClient>($"Authorisatie afgebroken: {error}");
        }
        if (string.IsNullOrEmpty(code))
        {
            await Reply(ctx, "Geen code ontvangen", "Er is geen 'code' parameter in de redirect-URL.");
            listener.Stop();
            return Result.Failure<MixerClient>("Geen authorisatiecode ontvangen.");
        }
        if (!StringComparer.Ordinal.Equals(state, returnedState))
        {
            await Reply(ctx, "State mismatch", "Beveiligingscontrole mislukt (mogelijk CSRF).");
            listener.Stop();
            return Result.Failure<MixerClient>("State mismatch (mogelijk CSRF).");
        }

        await Reply(ctx, "Login gelukt", "Je mag dit venster sluiten.");
        listener.Stop();

        // 6) Code ruilen voor token(s) (PKCE: geen client secret nodig)
        using var http = new HttpClient();
        var tokenResp = await http.PostAsync("https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("grant_type","authorization_code"),
                new KeyValuePair<string,string>("code", code),
                new KeyValuePair<string,string>("redirect_uri", RedirectUri),
                new KeyValuePair<string,string>("client_id", ClientId),
                new KeyValuePair<string,string>("code_verifier", verifier),
            }));

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        if (!tokenResp.IsSuccessStatusCode)
            return Result.Failure<MixerClient>($"Token exchange failed ({(int)tokenResp.StatusCode}): {tokenJson}");

        using var doc = JsonDocument.Parse(tokenJson);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresInSec = doc.RootElement.GetProperty("expires_in").GetInt32();
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSec);

        // 7) Client bouwen met auto-refresh
        var client = new MixerClient(accessToken, refreshToken, expiresAt);
        return Result.Success(client);
    }

    public async Task<Result<PlayList[]>> GetUserPlaylists(CancellationToken ct = default)
    {
        var ensure = await this.EnsureFreshAccessToken(ct);
        if (ensure.IsFailure)
        {
            return Result.Failure<PlayList[]>(ensure.Error);
        }

        var resp = await _http.GetAsync("me/playlists", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            return Result.Failure<PlayList[]>("Error getting data");
        }

        PlayList[] playlists = PlayListWrapper.ParsePlaylists(body).ToArray();
        return Result.Success<PlayList[]>(playlists);
    }


    public async Task<Result<Track[]>> GetPlayListTracks(PlayList playList, CancellationToken ct = default)
    {
        var ensure = await this.EnsureFreshAccessToken(ct);
        if (ensure.IsFailure)
        {
            return Result.Failure<Track[]>(ensure.Error);
        }

        var uriParts = playList.Uri.Split(':');

        List<Track> playListTracks = new List<Track>();
        int amount = 100;
        int offset = 0;

        while (true)
        {
            var resp = await _http.GetAsync($"playlists/{uriParts[2]}/tracks?limit={amount}&offset={offset}", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return Result.Failure<Track[]>("Error getting data");
            }

            Track[] tracks = PlayListWrapper.ParseTracks(body).ToArray();
            if (tracks.Length <= 0)
            {
                break;
            }

            foreach (var track in tracks)
            {
                if (playListTracks.Any(x => x.Id == track.Id))
                {
                    continue;
                }

                playListTracks.Add(track);
            }

            offset += amount;
        }

        return Result.Success<Track[]>(playListTracks.ToArray());
    }


    private async Task<Result> EnsureFreshAccessToken(CancellationToken ct)
    {
        // refresh als minder dan ~60s resterend
        if (_expiresAtUtc - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
            return Result.Success();

        if (string.IsNullOrWhiteSpace(_refreshToken))
            return Result.Failure("Geen refresh token beschikbaar; login vereist.");

        using var http = new HttpClient();
        var resp = await http.PostAsync("https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string,string>("grant_type","refresh_token"),
                new KeyValuePair<string,string>("refresh_token", _refreshToken!),
                new KeyValuePair<string,string>("client_id", ClientId),
            }), ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            return Result.Failure($"Refresh token failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var newAccess = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        // Sommige responses geven ook een nieuwe refresh_token terug:
        if (doc.RootElement.TryGetProperty("refresh_token", out var newRef))
            _refreshToken = newRef.GetString();

        _accessToken = newAccess;
        _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        ApplyAuthHeader();
        return Result.Success();
    }

    private void ApplyAuthHeader()
    {
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
