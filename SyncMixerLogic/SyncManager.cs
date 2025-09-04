namespace SyncMixerLogic;

using SyncMixerApi;

public class SyncManager
{
    public SyncManager()
    {
        var task = MixerClient.Create();
        task.ConfigureAwait(true).GetAwaiter().OnCompleted(() =>
        {
            var result = task.Result;
            if (result.IsFailure)
            {
                Console.WriteLine("Error Creating User");
                return;
            }

            this.SpotifyClient = task.Result.Value;
            this.RetrievePlaylistData();
        });
    }

    private MixerClient? SpotifyClient { get; set; }

    private void RetrievePlaylistData()
    {
        var playListsTask = this.SpotifyClient!.GetUserPlaylists();
        playListsTask.ConfigureAwait(true).GetAwaiter().OnCompleted(() =>
        {
            var result = playListsTask.Result;
            if (result.IsFailure)
            {
                Console.WriteLine("Error Getting playlists");

                return;
            }

            var filteredPlaylists = this.FilterPlayLists(result.Value);

            foreach (var playlist in filteredPlaylists)
            {
                var songsTask = this.SpotifyClient!.GetPlayListTracks(playlist);
                songsTask.ConfigureAwait(true).GetAwaiter().OnCompleted(() =>
                {
                    var result = songsTask.Result;
                    if (result.IsFailure)
                    {
                        Console.WriteLine("Error Getting playlists");

                        return;
                    }

                    playlist.AddTracks(result.Value);

                });
            }
        });
    }

    private PlayList[] FilterPlayLists(PlayList[] playLists)
    {
        return playLists.Where(x => !x.Collaborative && x.Name.EndsWith("-s")).ToArray();
    }
}
