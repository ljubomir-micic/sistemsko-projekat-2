using System;
using System.Net;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Text;
using System.IO;

namespace Projekat
{
    class HttpServ
    {
        private static readonly Channel<HttpListenerContext> redZahteva =
            Channel.CreateBounded<HttpListenerContext>(
                new BoundedChannelOptions(100)
                {
                    FullMode    = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
        private static readonly ConcurrentDictionary<string, Task<Slika?>> obradeUToku =
            new ConcurrentDictionary<string, Task<Slika?>>();
        private static readonly SemaphoreSlim semaforKonverzije = new SemaphoreSlim(4, 4);
        private static readonly SemaphoreSlim semaforSafety = new SemaphoreSlim(100, 100); // ovo sam dodao: zabrana prevelikog broja zahteva u jednom trenutku i sprecavanje system panic aktivacije

        public static void StartServ()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{Podesavanja.brojPorta}/");
            listener.Start();
            Logger.Log("Server je pokrenut");
            Logger.Log($"http://localhost:{Podesavanja.brojPorta}/");

            Thread graceful = new Thread(() =>
            {
                Logger.Log("Za graceful shutdown pritisnite taster 'q'.");
                while (listener.IsListening)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Q)
                        {
                            listener.Stop();
                            break;
                        }
                    }
                    else
                    {
                        Thread.Sleep(50);
                    }
                }
            });
            graceful.Start();

            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext? context = null;
                    
                    try
                    {
                        context = await listener.GetContextAsync();
                        await redZahteva.Writer.WriteAsync(context);
                    }
                    catch (Exception)
                    {
                        if (context != null) {
                            Logger.Log("[WARN] Red zahteva je pun — zahtev odbijen (503).");
                            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            context.Response.ContentLength64 = 0;
                            context.Response.Close();
                        }
                        break;
                    }
                }
                redZahteva.Writer.Complete();
            });

            _ = ProcesirajRedZahteva();

            graceful.Join();
        }

        private static async Task ProcesirajRedZahteva()
        {
            await foreach (var context in redZahteva.Reader.ReadAllAsync())
            {
                await semaforSafety.WaitAsync();
                _ = ObradaZahtevaAsync(
                    context
                ).ContinueWith(
                    t => {
                        if (t.Status == TaskStatus.RanToCompletion) semaforSafety.Release();
                        else Logger.Log($"[Logger.Log] Neočekivana greška: {t.Exception}");
                    }
                );
            }
        }

        public static async Task ObradaZahtevaAsync(HttpListenerContext context)
        {
            string query = context.Request.RawUrl!.Substring(1);

            if (string.IsNullOrEmpty(query))
            {
                string respStr = "<html><body>" +
                    "<h1>Error 400: Bad Request!</h1>" +
                    "<form id=\"forma\">" +
                    "<input type=\"text\" id=\"unos\" placeholder=\"ime slike\">" +
                    "<button id=\"search\">pretraga</button>" +
                    "</form></body>" +
                    "<script>" +
                    "document.getElementById('forma').onsubmit = (e) => {" +
                    "  e.preventDefault();" +
                    "  const val = document.getElementById('unos').value;" +
                    "  if (val) { window.location.href = '/' + encodeURIComponent(val); }" +
                    "};" +
                    "</script></html>";

                context.Response.ContentType = "text/html";
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                byte[] buff = Encoding.UTF8.GetBytes(respStr);
                context.Response.ContentLength64 = buff.Length;
                await context.Response.OutputStream.WriteAsync(buff, 0, buff.Length);
                context.Response.OutputStream.Close();
                context.Response.Close();
                return;
            }

            Slika? slika = Program.kes[query];

            if (slika == null)
            {
                Task<Slika?> obradaTask = obradeUToku.GetOrAdd(query, async (kljuc) =>
                {
                    await semaforKonverzije.WaitAsync();
                    try
                    {
                        Slika? rezultat = await Task.Run(() => Slika.ObradiSliku(kljuc));

                        if (rezultat != null)
                            Program.kes.DodajStavku(kljuc, rezultat);

                        return rezultat;
                    }
                    finally
                    {
                        semaforKonverzije.Release();

                        obradeUToku.TryRemove(kljuc, out _);
                    }
                });

                _ = obradaTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Logger.Log($"[LOG] Greška pri obradi slike {query}.");
                    else if (t.Result == null)
                        Logger.Log($"[LOG] Konverzija neuspešna — slika '{query}' ne postoji na disku.");
                    else
                        Logger.Log($"[LOG] Konverzija uspešno završena za '{query}'.");
                }, TaskContinuationOptions.ExecuteSynchronously);

                slika = await obradaTask;

                if (slika == null)
                {
                    string respStr = $"<html><body><h1>Error 404: Item not found!</h1></body></html>";
                    context.Response.ContentType = "text/html";
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    byte[] buff = Encoding.UTF8.GetBytes(respStr);
                    context.Response.ContentLength64 = buff.Length;
                    await context.Response.OutputStream.WriteAsync(buff, 0, buff.Length);
                    context.Response.OutputStream.Close();
                    context.Response.Close();
                    Logger.Log($"Slika '{query}' nije pronađena.");
                    return;
                }
            }

            string responseString = $"<html><body>" +
                $"<img src='data:image/jpeg;base64,{Convert.ToBase64String(slika.GetData())}' />" +
                $"</body></html>";

            context.Response.ContentType = "text/html";
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
            context.Response.Close();

            Logger.Log($"Zahtev uspešno obrađen! [memorija: {Program.kes.Count}/{Program.kes.LimitUBajtovima}]");
        }
    }
}