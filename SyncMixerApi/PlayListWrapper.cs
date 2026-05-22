using Newtonsoft.Json;
using SyncMixerApi;

public class PlayListWrapper
{
    public static List<PlayList> ParsePlaylists(string json)
    {
        var resp = JsonConvert.DeserializeObject<PlaylistsResponse>(json);
        return resp?.Items ?? new List<PlayList>();
    }

    public static List<PlaylistTrackItem> ParseTracks(string json)
    {
        var resp = JsonConvert.DeserializeObject<PlaylistsTracksResponse>(json);
        var item = resp?.Items ?? new List<PlaylistTrackItem>();
        return item.ToList();
    }
}

public sealed class PlaylistsResponse
{
    [JsonProperty("items")]
    public List<PlayList> Items { get; set; } = new ();
}

public sealed class PlaylistsTracksResponse
{
    [JsonProperty("items")]
    public List<PlaylistTrackItem> Items { get; set; } = new ();
}