namespace SyncMixerLogic;

using CSharpFunctionalExtensions;
using SyncMixerApi;

public class SyncManager
{
    private const string SyncMixerPlaylistName = "SyncMixer -m";
    private const string MonthlyNewbiesPlaylistName = "Monthly newbies -m";

    private readonly TaskCompletionSource<bool> finishedTcs = new (TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> WhenFinished => this.finishedTcs.Task;

    private MixerClient? SpotifyClient { get; set; }

    private PlayList[]? PlayLists { get; set; }

    private PlayList? MixerPlayList { get; set; }

    private PlayList? MonthlyPlayList { get; set; }

    private bool IsNewMixerPlayList { get; set; }

    private bool IsNewMonthlyNewbiesPlayList { get; set; }

    public async Task Init(string clientId, CancellationToken ct = default)
    {
        try
        {
            var result = await MixerClient.Create(clientId);
            if (result.IsFailure)
            {
                Console.WriteLine("Error Creating User");
                this.finishedTcs.TrySetResult(false);
                return;
            }

            this.SpotifyClient = result.Value;
            this.PlayLists = await this.GetPlaylists();

            if (this.PlayLists != null)
            {
                foreach (var playlist in this.PlayLists)
                {
                    await this.SetPlayListTrackItems(playlist);
                }
            }

            this.ResolveTargetPlayLists();

            Console.WriteLine("Finished Retrieving Playlist Data");

            await this.PreparePlayList(
                this.MixerPlayList!,
                this.IsNewMixerPlayList,
                "SyncMixer");

            await this.PreparePlayList(
                this.MonthlyPlayList!,
                this.IsNewMonthlyNewbiesPlayList,
                "Monthly Newbies");

            this.SetSyncMixerPlaylistTracks();
            this.SetMonthlyNewbiesPlaylistTracks();

            await this.SetPlayListTracks(this.MixerPlayList!, "SyncMixer");
            await this.SetPlayListTracks(this.MonthlyPlayList!, "Monthly Newbies");

            Console.Clear();
            Console.WriteLine("SyncMixer Successful");
            this.finishedTcs.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            this.finishedTcs.TrySetCanceled(ct);
            throw;
        }
        catch (Exception ex)
        {
            this.finishedTcs.TrySetException(ex);
            throw;
        }
    }

    private void ResolveTargetPlayLists()
    {
        this.MixerPlayList = this.GetMixerPlayList(this.PlayLists);
        this.IsNewMixerPlayList = this.MixerPlayList == null;

        if (this.IsNewMixerPlayList)
        {
            this.MixerPlayList = this.CreateSyncMixerPlayList();
        }

        this.MonthlyPlayList = this.GetMonthlyPlayList(this.PlayLists);
        this.IsNewMonthlyNewbiesPlayList = this.MonthlyPlayList == null;

        if (this.IsNewMonthlyNewbiesPlayList)
        {
            this.MonthlyPlayList = this.CreateMonthlyNewbiesPlayList();
        }
    }

    private async Task<PlayList[]?> GetPlaylists()
    {
        Console.Clear();
        Console.WriteLine("Starting Retrieving Playlist Data");

        var playListsTask = await this.SpotifyClient!.GetUserPlaylists();
        if (playListsTask.IsFailure)
        {
            Console.WriteLine("Error Getting playlists");
            return null;
        }

        return playListsTask.Value;
    }

    private async Task<PlayList> SetPlayListTrackItems(PlayList playList)
    {
        var result = await this.GetPlaylistTracks(playList);

        if (result != null && result.Length > 0)
        {
            playList.AddTrackItems(result);
        }

        return playList;
    }

    private async Task<PlaylistTrackItem[]?> GetPlaylistTracks(PlayList playlist)
    {
        var tracksResult = await this.SpotifyClient!.GetPlayListTracks(playlist);
        if (tracksResult.IsFailure)
        {
            Console.WriteLine("Error playlist tracks");
            return null;
        }

        return tracksResult.Value;
    }

    private async Task PreparePlayList(PlayList playlist, bool isNewPlaylist, string label)
    {
        Console.WriteLine($"Starting Preparing {label} Playlist");

        var result = isNewPlaylist
            ? await this.SpotifyClient!.CreateNewPlayList(playlist)
            : await this.SpotifyClient!.DeleteTracks(playlist);

        if (result.IsFailure)
        {
            Console.WriteLine($"Error Preparing {label} playlist");
            return;
        }

        Console.WriteLine($"Finished Preparing {label} Playlist");
    }

    private async Task SetPlayListTracks(PlayList playlist, string label)
    {
        Console.WriteLine($"Starting Setting {label} Playlist tracks");

        var result = await this.SpotifyClient!.SetPlayListTracks(playlist);
        if (result.IsFailure)
        {
            Console.WriteLine($"Error adding {label} playlist tracks");
            return;
        }

        Console.WriteLine($"Finished Setting {label} Playlist tracks");
    }

    private void SetSyncMixerPlaylistTracks()
    {
        foreach (var sourcePlaylist in this.FilterSourcePlayLists(this.PlayLists))
        {
            this.MixerPlayList!.AddTrackItems(sourcePlaylist.TrackItems);
        }

        this.MixerPlayList!.ShuffleTracks();
    }

    private void SetMonthlyNewbiesPlaylistTracks()
    {
        var firstDayOfMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);

        var monthlyTracks = this.FilterSourcePlayLists(this.PlayLists)
            .SelectMany(x => x.TrackItems ?? Array.Empty<PlaylistTrackItem>())
            .Where(x => x.AddedAt >= firstDayOfMonth)
            .ToArray();

        this.MonthlyPlayList!.AddTrackItems(monthlyTracks);
        this.MonthlyPlayList!.ShuffleTracks();
    }

    private PlayList CreateSyncMixerPlayList()
    {
        return new PlayList
        {
            Name = SyncMixerPlaylistName,
            Description = "This playlist is generated by the SyncMixer app. " +
                          "Source: Mixer integration. " +
                          $"©{DateTime.Now.Year} - Zuipskuur Invent",
            Collaborative = true,
        };
    }

    private PlayList CreateMonthlyNewbiesPlayList()
    {
        return new PlayList
        {
            Name = MonthlyNewbiesPlaylistName,
            Description = "This playlist is generated by the SyncMixer app. " +
                          "Source: Mixer integration. " +
                          $"©{DateTime.Now.Year} - Zuipskuur Invent",
            Collaborative = true,
        };
    }

    private PlayList[] FilterSourcePlayLists(PlayList[]? playLists)
    {
        return playLists?
            .Where(x => !x.Collaborative && x.Name.EndsWith("-s"))
            .ToArray()
            ?? Array.Empty<PlayList>();
    }

    private PlayList? GetMixerPlayList(PlayList[]? playLists)
    {
        return playLists?
            .FirstOrDefault(x => x.Name == SyncMixerPlaylistName);
    }

    private PlayList? GetMonthlyPlayList(PlayList[]? playLists)
    {
        return playLists?
            .FirstOrDefault(x => x.Name == MonthlyNewbiesPlaylistName);
    }
}