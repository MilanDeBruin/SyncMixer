namespace SyncMixerApi;

using CSharpFunctionalExtensions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public class MixerClient : IDisposable
{
    private const string RedirectUri = "http://127.0.0.1:8000/callback";
    private static readonly string[] Scopes =
    {
        "playlist-read-private",
        "playlist-read-collaborative",
        "playlist-modify-private",
        "playlist-modify-public",
    };

    private readonly HttpClient http;
    private string clientId;
    private string accessToken;
    private string? refreshToken;
    private DateTimeOffset expiresAtUtc;

    private MixerClient(string clientId, string accessToken, string? refreshToken, DateTimeOffset expiresAtUtc)
    {
        this.clientId = clientId;
        this.accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        this.refreshToken = refreshToken;
        this.expiresAtUtc = expiresAtUtc;

        this.http = new HttpClient { BaseAddress = new Uri("https://api.spotify.com/v1/") };
        this.http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        this.ApplyAuthHeader();
    }

    public static async Task<Result<MixerClient>> Create(string clientId)
    {
        // 0) PKCE + state
        var (verifier, challenge) = MixerClientHelpers.GeneratePkceCodes();
        var state = MixerClientHelpers.Base64Url(RandomNumberGenerator.GetBytes(32));

        // 1) Authorize URL bouwen
        var authUrl =
            "https://accounts.spotify.com/authorize" +
            "?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
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
                "  netsh http add urlacl url=http://127.0.0.1:8000/callback/ user=Everyone");
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

        await Reply(ctx, "Login gelukt", "Je mag dit venster sluiten.", true);
        listener.Stop();

        // 6) Code ruilen voor token(s) (PKCE: geen client secret nodig)
        using var http = new HttpClient();
        var tokenResp = await http.PostAsync(
            "https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("code_verifier", verifier),
            }));

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        if (!tokenResp.IsSuccessStatusCode)
        {
            return Result.Failure<MixerClient>($"Token exchange failed ({(int)tokenResp.StatusCode}): {tokenJson}");
        }

        using var doc = JsonDocument.Parse(tokenJson);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString() !;
        var expiresInSec = doc.RootElement.GetProperty("expires_in").GetInt32();
        var refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresInSec);

        // 7) Client bouwen met auto-refresh
        var client = new MixerClient(clientId, accessToken, refreshToken, expiresAt);
        return Result.Success(client);
    }

    public async Task<Result<PlayList[]>> GetUserPlaylists(CancellationToken ct = default)
    {
        var ensure = await this.EnsureFreshAccessToken(ct);
        if (ensure.IsFailure)
        {
            return Result.Failure<PlayList[]>(ensure.Error);
        }

        var resp = await this.http.GetAsync("me/playlists", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            return Result.Failure<PlayList[]>("Error getting data");
        }

        PlayList[] playlists = PlayListWrapper.ParsePlaylists(body).ToArray();
        return Result.Success<PlayList[]>(playlists);
    }

    public async Task<Result<PlaylistTrackItem[]>> GetPlayListTracks(PlayList playList, CancellationToken ct = default)
    {
        var ensure = await this.EnsureFreshAccessToken(ct);
        if (ensure.IsFailure)
        {
            return Result.Failure<PlaylistTrackItem[]>(ensure.Error);
        }

        var uriParts = playList.Uri.Split(':');

        List<PlaylistTrackItem> playListTracks = new List<PlaylistTrackItem>();
        int amount = 100;
        int offset = 0;

        while (true)
        {
            var resp = await this.http.GetAsync($"playlists/{uriParts[2]}/tracks?limit={amount}&offset={offset}", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return Result.Failure<PlaylistTrackItem[]>("Error getting data");
            }

            PlaylistTrackItem[] playlistItems = PlayListWrapper.ParseTracks(body).ToArray();
            if (playlistItems.Length <= 0)
            {
                break;
            }

            foreach (var item in playlistItems)
            {
                if (item.Track == null || playListTracks.Any(x => x.Track.Id == item.Track.Id))
                {
                    continue;
                }

                playListTracks.Add(item);
            }

            offset += amount;
        }

        return Result.Success<PlaylistTrackItem[]>(playListTracks.ToArray());
    }

    public async Task<Result> CreateNewPlayList(PlayList playList, CancellationToken ct = default)
    {
        var ensure = await this.EnsureFreshAccessToken(ct);
        if (ensure.IsFailure)
        {
            return Result.Failure<PlayList>(ensure.Error);
        }

        var payload = new
        {
            name = playList.Name,
            description = playList.Description,
            @public = true,
        };

        var json = JsonConvert.SerializeObject(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        var resp = await this.http.PostAsync($"me/playlists", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            return Result.Failure<PlayList>($"Error creating playlist: {body}");
        }

        var createdPlaylist = JsonConvert.DeserializeObject<PlayList>(body);

        if (createdPlaylist == null)
        {
            return Result.Failure<PlayList>("Error parsing created playlist response");
        }

        playList.Id = (string?)JObject.Parse(body)["id"] ?? string.Empty;

        if (string.IsNullOrEmpty(playList.Id))
        {
            return Result.Failure<PlayList>("Error response does not contain playlist id!");
        }

        return Result.Success();
    }

    public async Task<Result> SetPlayListTracks(PlayList playList, int? position = null, CancellationToken ct = default)
    {
        if (playList == null || string.IsNullOrWhiteSpace(playList.Id))
        {
            return Result.Failure("Playlist or Playlist.Id is missing.");
        }

        var uris = playList.TrackItems
    .Select(t => MixerClientHelpers.ToSpotifyUri(t.Track))
    .Where(u => !string.IsNullOrWhiteSpace(u))
    .ToArray();

        if (uris.Length == 0)
        {
            return Result.Success();
        }

        var ensure = await this.EnsureFreshAccessToken(ct);
        if (ensure.IsFailure)
        {
            return Result.Failure(ensure.Error);
        }

        const int MAX = 100;
        for (int offset = 0; offset < uris.Length; offset += MAX)
        {
            var batch = uris.Skip(offset).Take(MAX).ToArray();

            var payload = new
            {
                uris = batch,
                position = position.HasValue ? position.Value + offset : (int?)null,
            };

            var json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
            });

            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var resp = await this.http.PostAsync($"playlists/{playList.Id}/tracks", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return Result.Failure($"Error adding tracks (batch {offset}-{offset + batch.Length - 1}): {body}");
            }
        }

        return Result.Success();
    }

    public async Task<Result> DeleteTracks(PlayList playList, string? snapshotId = null, CancellationToken ct = default)
    {
        if (playList == null || string.IsNullOrWhiteSpace(playList.Id))
        {
            return Result.Failure("Playlist or Playlist.Id is missing.");
        }

        var ensure = await this.EnsureFreshAccessToken(ct);
        if (ensure.IsFailure)
        {
            return Result.Failure(ensure.Error);
        }

        var uris = playList.TrackItems
        .Select(t => MixerClientHelpers.ToSpotifyUri(t.Track))
        .Where(u => !string.IsNullOrWhiteSpace(u))
        .ToArray();

        if (uris.Length == 0)
        {
            return Result.Success();
        }

        const int MAX = 100;
        for (int i = 0; i < uris.Length; i += MAX)
        {
            var batchUris = uris.Skip(i).Take(MAX)
                                .Select(u => new { uri = u })
                                .ToArray();

            var payload = new
            {
                tracks = batchUris,
                snapshot_id = snapshotId,
            };

            var json = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
            });

            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            // DELETE met body -> via HttpRequestMessage
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"playlists/{playList.Id}/tracks")
            {
                Content = content,
            };

            var resp = await this.http.SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                return Result.Failure($"Error deleting tracks (batch {i}-{i + batchUris.Length - 1}): {body}");
            }
        }

        return Result.Success();
    }

    public void Dispose()
    {
        this.http.Dispose();
    }

    private async Task<Result> EnsureFreshAccessToken(CancellationToken ct)
    {
        // refresh als minder dan ~60s resterend
        if (this.expiresAtUtc - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60))
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(this.refreshToken))
        {
            return Result.Failure("Geen refresh token beschikbaar; login vereist.");
        }

        using var http = new HttpClient();
        var resp = await http.PostAsync(
            "https://accounts.spotify.com/api/token",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", this.refreshToken!),
                new KeyValuePair<string, string>("client_id", this.clientId),
            }), ct);

        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            return Result.Failure($"Refresh token failed ({(int)resp.StatusCode}): {json}");
        }

        using var doc = JsonDocument.Parse(json);
        var newAccess = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.GetProperty("expires_in").GetInt32();

        // Sommige responses geven ook een nieuwe refresh_token terug:
        if (doc.RootElement.TryGetProperty("refresh_token", out var newRef))
        {
            this.refreshToken = newRef.GetString();
        }

        this.accessToken = newAccess!;
        this.expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        this.ApplyAuthHeader();
        return Result.Success();
    }

    private void ApplyAuthHeader()
    {
        this.http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", this.accessToken);
    }
}
