namespace SyncMixerApi;

using Newtonsoft.Json;
using System.Reflection.Metadata.Ecma335;

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
        var existing = this.Tracks ?? Array.Empty<Track>();
        var incoming = tracks ?? Array.Empty<Track>();
        this.Tracks = existing
            .Concat(incoming)
            .Where(t => t != null && !string.IsNullOrWhiteSpace(t.Uri))
            .DistinctBy(t => t.Uri!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void ShuffleTracks(int? seed = null)
    {
        if (this.Tracks == null || this.Tracks.Length <= 1) return;

        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;

        for (int i = this.Tracks.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (this.Tracks[i], this.Tracks[j]) = (this.Tracks[j], this.Tracks[i]);
        }
    }

    public void ClearPlaylist()
    {
        this.Tracks = Array.Empty<Track>();
    }
}
