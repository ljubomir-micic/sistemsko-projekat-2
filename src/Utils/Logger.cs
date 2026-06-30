public class Logger
{
    private static readonly object _logLock = new object();
    public static void Log(string poruka)
    {
        lock (_logLock)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {poruka}");
        }
    }
}