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
        private static readonly ConcurrentDictionary<string, Lazy<Task<Slika?>>> obradeUToku =
            new ConcurrentDictionary<string, Lazy<Task<Slika?>>>();

        private static readonly SemaphoreSlim semaforKonverzije = new SemaphoreSlim(4, 4);
        private static readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public static async Task StartServ()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{Podesavanja.brojPorta}/");
            listener.Start();
            Logger.Log("Server je pokrenut");
            Logger.Log($"http://localhost:{Podesavanja.brojPorta}/");

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                Logger.Log("Ctrl+C detektovan -> pokretanje graceful shutdown-a...");
                _cts.Cancel();
            };
            Thread kTasterListener = new Thread(() =>
            {
                Logger.Log("Za graceful shutdown pritisnite 'q'.");
                while (!_cts.Token.IsCancellationRequested)
                {
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q)
                    {
                        Logger.Log("Taster 'q' detektovan - pokretanje graceful shutdown-a...");
                        _cts.Cancel();
                        break;
                    }
                    Thread.Sleep(50);
                }
            })
            {
                IsBackground = true,
                Name = "ShutdownKeyListener"
            };
            kTasterListener.Start();

            var listenerTask = ListenerLoop(listener, _cts.Token);
            var dispatcherTask = ProcesirajRedZahteva(_cts.Token);

            await Task.WhenAll(listenerTask, dispatcherTask);

            listener.Close();
            Logger.Log("Server zaustavljen");
        }

        private static async Task ListenerLoop(HttpListener listener, CancellationToken ct)
        {
            Logger.Log("Listener task pokrenut.");

            using var registracija = ct.Register(() =>
            {
                try { listener.Stop(); } catch (ObjectDisposedException) { }
            });

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    Logger.Log("Nova konekcija primljena");
                    await redZahteva.Writer.WriteAsync(context, ct);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpListenerException)
            {
                // Očekivano kada listener.Stop() prekine pending GetContextAsync poziv
                // usled cancelacije.
            }
            finally
            {
                redZahteva.Writer.Complete();
                Logger.Log("Listener task zavrsen");
            }
        }

        private static async Task ProcesirajRedZahteva(CancellationToken ct)
        {
            Logger.Log("Dispatcher task pokrenut.");

            await foreach (var context in redZahteva.Reader.ReadAllAsync(ct))
            {
                _ = ObradaZahtevaAsync(context).ContinueWith(
                    t => Logger.Log($"[Logger.Log] Neočekivana greška: {t.Exception}"),
                    TaskContinuationOptions.OnlyOnFaulted
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
                Lazy<Task<Slika?>> lazyObrada = obradeUToku.GetOrAdd(query, kljuc =>
                    new Lazy<Task<Slika?>>(() => ObradiSlikuAsinhrono(kljuc)));

                Task<Slika?> obradaTask = lazyObrada.Value;

                _ = obradaTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Logger.Log($"[LOG] Greška pri obradi slike {query}.");
                    else if (t.Result == null)
                        Logger.Log($"[LOG] Konverzija neuspešna — slika '{query}' ne postoji na disku.");
                    else
                        Logger.Log($"[LOG] Konverzija uspešno završena za '{query}'.");
                }, TaskContinuationOptions.ExecuteSynchronously);

                try
                {
                    slika = await obradaTask;
                }
                finally
                {
                    obradeUToku.TryRemove(query, out _);
                }

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

        private static async Task<Slika?> ObradiSlikuAsinhrono(string kljuc)
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
            }
        }
    }
}