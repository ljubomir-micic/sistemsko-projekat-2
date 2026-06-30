using System;
using System.IO;
using System.Collections.Concurrent;
using System.Threading;

public class Logger
{
    public static readonly object _logLock = new object();
    private static readonly string LogFajl = "server_log.txt";
    private static readonly ConcurrentQueue<string> _logRed = new ConcurrentQueue<string>();
    private static readonly Timer _timer;
    private static int _brojPoruka = 0;
    
    static Logger()
    {
        try
        {
            File.WriteAllText(LogFajl, $"=== Server log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
        }
        catch { }
        
        // Batch upis svakih 500ms
        _timer = new Timer(Flush, null, 500, 500);
    }

    private static void Flush(object? state)
    {
        if (_logRed.IsEmpty) return;
        
        var sb = new System.Text.StringBuilder();
        int count = 0;
        while (_logRed.TryDequeue(out string? msg) && count < 1000)
        {
            sb.AppendLine(msg);
            count++;
        }
        
        if (sb.Length > 0)
        {
            lock (_logLock)
            {
                Console.Write(sb.ToString());
                try
                {
                    File.AppendAllText(LogFajl, sb.ToString());
                }
                catch { }
            }
        }
    }

    public static void Log(string poruka)
    {
        // Ne logujemo sve - samo svaki 50-ti ili greške
        if (++_brojPoruka % 50 == 0 || poruka.Contains("GRESKA") || poruka.Contains("WARN"))
        {
            string vremenskaOznaka = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            _logRed.Enqueue($"[{vremenskaOznaka}] {poruka}");
        }
    }

    public static void FlushNow()
    {
        Flush(null);
        _timer.Dispose();
    }
}