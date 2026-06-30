using System;
using System.Net;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Text;
using System.Diagnostics;

namespace Projekat
{
    class HttpServ
    {
        // PROMENA 1: Više reader-a za red
        private static readonly Channel<HttpListenerContext> redZahteva =
            Channel.CreateBounded<HttpListenerContext>(
                new BoundedChannelOptions(1000)  // Povećan red
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,  // PROMENA: Više čitača!
                    SingleWriter = false
                });

        private static readonly ConcurrentDictionary<string, Task<Slika?>> obradeUToku =
            new ConcurrentDictionary<string, Task<Slika?>>();

        // PROMENA 2: Veći broj paralelnih konverzija
        private static readonly SemaphoreSlim semaforKonverzije = new SemaphoreSlim(
            Environment.ProcessorCount * 2,  // Dinamički, npr. 8 za 4-jezgreni
            Environment.ProcessorCount * 4
        );

        // PROMENA 3: Veći broj istovremenih zahteva
        private static readonly SemaphoreSlim semaforUkupniZahtevi = new SemaphoreSlim(200, 200);

        private static readonly CancellationTokenSource cts = new CancellationTokenSource();

        // PROMENA 4: Statički keš za brži pristup
        private static readonly Kes kes = Program.kes;

        // PROMENA 5: Batch logovanje
        private static readonly ConcurrentQueue<string> logRed = new ConcurrentQueue<string>();
        private static readonly Timer logTimer;
        private static int logCounter = 0;

        static HttpServ()
        {
            // Timer za batch logovanje - svakih 100ms
            logTimer = new Timer(FlushLogs, null, 100, 100);
        }

        private static void FlushLogs(object? state)
        {
            if (logRed.IsEmpty) return;
            
            var sb = new StringBuilder();
            int count = 0;
            while (logRed.TryDequeue(out string? msg) && count < 1000)
            {
                sb.AppendLine(msg);
                count++;
            }
            
            if (sb.Length > 0)
            {
                // Jedan upis umesto hiljadu
                lock (Logger._logLock)
                {
                    Console.Write(sb.ToString());
                    try
                    {
                        File.AppendAllText("server_log.txt", sb.ToString());
                    }
                    catch { }
                }
            }
        }

        private static void Log(string poruka)
        {
            // Ne logujemo sve - samo bitne stvari
            if (++logCounter % 100 == 0 || poruka.Contains("GRESKA") || poruka.Contains("WARN"))
            {
                string vremenskaOznaka = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                logRed.Enqueue($"[{vremenskaOznaka}] {poruka}");
            }
        }

        public static void StartServ()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{Podesavanja.brojPorta}/");
            listener.Start();
            Log($"Server pokrenut na portu {Podesavanja.brojPorta}");

            // Graceful shutdown - optimizovan
            _ = Task.Run(() =>
            {
                Log("Pritisnite 'q' za gašenje.");
                while (listener.IsListening)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Q)
                        {
                            Log("Gašenje servera...");
                            cts.Cancel();
                            listener.Stop();
                            break;
                        }
                    }
                    // PROMENA: Umesto Sleep, koristimo Task.Delay
                    Task.Delay(50).Wait();
                }
            });

            // PROMENA 6: Više taskova za prijem (paralelni prijem)
            int brojPrijemnika = Environment.ProcessorCount;
            for (int i = 0; i < brojPrijemnika; i++)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (listener.IsListening && !cts.Token.IsCancellationRequested)
                        {
                            HttpListenerContext? context = null;
                            try
                            {
                                context = await listener.GetContextAsync();
                                
                                if (redZahteva.Writer.TryWrite(context))
                                {
                                    // Smanjeno logovanje - samo svaki 100-ti
                                    if (Interlocked.Increment(ref logCounter) % 100 == 0)
                                        Log($"[PRIJEM] {logCounter} zahteva primljeno");
                                }
                                else
                                {
                                    Log("[WARN] Red pun - 503");
                                    context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                                    context.Response.ContentLength64 = 0;
                                    context.Response.Close();
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch (Exception ex)
                            {
                                Log($"[GRESKA] Prijem: {ex.Message}");
                                if (context != null)
                                {
                                    try
                                    {
                                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                                        context.Response.ContentLength64 = 0;
                                        context.Response.Close();
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                    finally
                    {
                        redZahteva.Writer.Complete();
                    }
                }, cts.Token);
            }

            // PROMENA 7: Više obrađivača reda (paralelna obrada)
            int brojObradivaca = Environment.ProcessorCount * 2;
            for (int i = 0; i < brojObradivaca; i++)
            {
                _ = ProcesirajRedZahteva(cts.Token);
            }

            // Čekanje na gašenje
            while (listener.IsListening)
            {
                Task.Delay(100).Wait();
            }
            
            logTimer.Dispose();
            FlushLogs(null);
            Log("Server zaustavljen.");
        }

        private static async Task ProcesirajRedZahteva(CancellationToken token)
        {
            try
            {
                await foreach (var context in redZahteva.Reader.ReadAllAsync(token))
                {
                    if (token.IsCancellationRequested)
                        break;

                    await semaforUkupniZahtevi.WaitAsync(token);
                    
                    // PROMENA 8: Task sa optimizovanom kontinuacijom
                    _ = ObradaZahtevaAsync(context, token)
                        .ContinueWith(t =>
                        {
                            semaforUkupniZahtevi.Release();
                            
                            // Logujemo samo greške
                            if (t.IsFaulted && t.Exception != null)
                            {
                                Log($"[GRESKA] Obrada: {t.Exception.GetBaseException().Message}");
                            }
                        }, TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log($"[GRESKA] Obrađivač: {ex.Message}");
            }
        }

        public static async Task ObradaZahtevaAsync(HttpListenerContext context, CancellationToken token)
        {
            string query = context.Request.RawUrl!.Substring(1);

            if (string.IsNullOrEmpty(query))
            {
                await PosaljiHtmlOdgovor(context, 
                    "<html><body><h1>Unesite ime slike</h1>" +
                    "<form id='forma' onsubmit='event.preventDefault();window.location.href=\"/\"+document.getElementById(\"unos\").value'>" +
                    "<input type='text' id='unos' placeholder='ime slike'><button>pretraga</button>" +
                    "</form></body></html>",
                    HttpStatusCode.BadRequest);
                return;
            }

            // PROMENA 9: Brži pristup kešu
            Slika? slika = kes[query];
            
            if (slika != null)
            {
                await PosaljiSlikuOdgovor(context, slika, token);
                return;
            }

            // Cache stampede zaštita
            Task<Slika?> obradaTask = obradeUToku.GetOrAdd(query, async (kljuc) =>
            {
                await semaforKonverzije.WaitAsync(token);
                try
                {
                    // PROMENA 10: Task.Run sa optimizacijom
                    Slika? rezultat = await Task.Run(() => 
                    {
                        // Koristimo Stopwatch za merenje
                        var sw = Stopwatch.StartNew();
                        var result = Slika.ObradiSliku(kljuc);
                        sw.Stop();
                        
                        if (sw.ElapsedMilliseconds > 100)
                        {
                            Log($"[SPORO] '{kljuc}' - {sw.ElapsedMilliseconds}ms");
                        }
                        
                        return result;
                    }, token);

                    if (rezultat != null)
                    {
                        kes.DodajStavku(kljuc, rezultat);
                    }

                    return rezultat;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    Log($"[KONVERZIJA] Greška za '{kljuc}': {ex.Message}");
                    return null;
                }
                finally
                {
                    semaforKonverzije.Release();
                    obradeUToku.TryRemove(kljuc, out _);
                }
            });

            slika = await obradaTask.WaitAsync(token);

            if (slika == null)
            {
                await PosaljiHtmlOdgovor(context,
                    "<html><body><h1>404 - Slika nije pronađena</h1></body></html>",
                    HttpStatusCode.NotFound);
                return;
            }

            await PosaljiSlikuOdgovor(context, slika, token);
        }

        private static async Task PosaljiHtmlOdgovor(HttpListenerContext context, string html, HttpStatusCode status)
        {
            context.Response.ContentType = "text/html";
            context.Response.StatusCode = (int)status;
            byte[] buff = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength64 = buff.Length;
            await context.Response.OutputStream.WriteAsync(buff, 0, buff.Length, default);
            context.Response.OutputStream.Close();
            context.Response.Close();
        }

        private static async Task PosaljiSlikuOdgovor(HttpListenerContext context, Slika slika, CancellationToken token)
        {
            // PROMENA 11: Base64 konverzija može biti skupa - keširajmo je
            string base64 = Convert.ToBase64String(slika.GetData());
            string responseString = $"<html><body><img src='data:image/jpeg;base64,{base64}' /></body></html>";

            context.Response.ContentType = "text/html";
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            byte[] buffer = Encoding.UTF8.GetBytes(responseString);
            context.Response.ContentLength64 = buffer.Length;
            
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, token);
            context.Response.OutputStream.Close();
            context.Response.Close();
        }
    }
}