namespace SyncMixerApi;

using Newtonsoft.Json;

public class PlayList
{
    [JsonProperty("collaborative")]
    public bool Collaborative { get; set; }

    [JsonProperty("description")]
    public string? Description { get; set; }

    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("name")]
    public string Name { get; set; } = default!;

    [JsonProperty("uri")]
    public string Uri { get; set; } = default!;

    public Track[] Tracks { get; private set; } = Array.Empty<Track>();

    public void AddTracks(Track[] tracks)
    {
        this.Tracks = tracks;
    }
}
