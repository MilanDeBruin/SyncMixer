namespace SyncMixer
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Start Sync! .....");
            new SyncMixerLogic.SyncManager();

            Console.ReadKey();
        }
    }
}
