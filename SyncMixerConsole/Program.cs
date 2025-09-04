namespace SyncMixerConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Starting Syncing!...");
            new SyncMixerLogic.SyncManager();

            Console.ReadKey();
        }
    }
}
