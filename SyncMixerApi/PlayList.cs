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

    public PlaylistTrackItem[] TrackItems { get; private set; } = Array.Empty<PlaylistTrackItem>();

    public void AddTrackItems(PlaylistTrackItem[] trackItems)
    {
        var existing = this.TrackItems ?? Array.Empty<PlaylistTrackItem>();
        var incoming = trackItems ?? Array.Empty<PlaylistTrackItem>();

        this.TrackItems = existing
            .Concat(incoming)
            .Where(t => t?.Track != null && !string.IsNullOrWhiteSpace(t.Track.Uri))
            .GroupBy(t => t.Track.Uri.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(t => t.AddedAt)
                .First())
            .ToArray();
    }

    public void ShuffleTracks(int? seed = null)
    {
        if (this.TrackItems == null || this.TrackItems.Length <= 1)
        {
            return;
        }

        var rng = seed.HasValue ? new Random(seed.Value) : Random.Shared;

        for (int i = this.TrackItems.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (this.TrackItems[i], this.TrackItems[j]) = (this.TrackItems[j], this.TrackItems[i]);
        }
    }
}
