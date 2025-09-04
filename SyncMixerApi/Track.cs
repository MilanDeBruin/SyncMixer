namespace SyncMixerApi;

using Newtonsoft.Json;

public class PlaylistTrackItem
{
    [JsonProperty("track")]
    public Track Track { get; set; } = default!;
}

public class Track
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("name")]
    public string Name { get; set; } = default!;
}

