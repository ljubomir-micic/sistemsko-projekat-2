using System;
using System.Net;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Text;

namespace Projekat
{
    class HttpServ
    {
        // Red zahteva - razdvajanje prijema i obrade
        private static readonly Channel<HttpListenerContext> redZahteva =
            Channel.CreateBounded<HttpListenerContext>(
                new BoundedChannelOptions(100)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

        // Sprečavanje cache stampede-a - taskovi koji su u toku
        private static readonly ConcurrentDictionary<string, Task<Slika?>> obradeUToku =
            new ConcurrentDictionary<string, Task<Slika?>>();

        // Ograničavanje broja paralelnih konverzija (max 4)
        private static readonly SemaphoreSlim semaforKonverzije = new SemaphoreSlim(4, 4);

        // Ograničavanje ukupnog broja istovremenih zahteva
        private static readonly SemaphoreSlim semaforUkupniZahtevi = new SemaphoreSlim(100, 100);

        public static void StartServ()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{Podesavanja.brojPorta}/");
            listener.Start();
            Logger.Log("Server je pokrenut");
            Logger.Log($"http://localhost:{Podesavanja.brojPorta}/");

            // Graceful shutdown - posebna nit
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

            // Task za prijem zahteva - odvojen od obrade
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext? context = null;
                    try
                    {
                        context = await listener.GetContextAsync();
                        
                        // Provera da li red nije pun
                        if (redZahteva.Writer.TryWrite(context))
                        {
                            Logger.Log($"[PRIJEM] Zahtev za '{context.Request.RawUrl}' primljen.");
                        }
                        else
                        {
                            Logger.Log("[WARN] Red zahteva je pun — zahtev odbijen (503).");
                            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            context.Response.ContentLength64 = 0;
                            context.Response.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"[GRESKA] Prijem zahteva: {ex.Message}");
                        if (context != null)
                        {
                            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                            context.Response.ContentLength64 = 0;
                            context.Response.Close();
                        }
                    }
                }
                redZahteva.Writer.Complete();
                Logger.Log("Prijem zahteva zaustavljen.");
            });

            // Pokretanje obrade reda zahteva
            _ = ProcesirajRedZahteva();

            graceful.Join();
            Logger.Log("Server je zaustavljen.");
        }

        private static async Task ProcesirajRedZahteva()
        {
            Logger.Log("Pokretanje obrađivača zahteva...");
            
            await foreach (var context in redZahteva.Reader.ReadAllAsync())
            {
                // Ograničavanje broja istovremenih zahteva
                await semaforUkupniZahtevi.WaitAsync();
                
                // Kreiranje taska za obradu sa kontinuacijom za oslobađanje semafora
                _ = ObradaZahtevaAsync(context)
                    .ContinueWith(t =>
                    {
                        semaforUkupniZahtevi.Release();
                        
                        if (t.IsFaulted)
                        {
                            Logger.Log($"[GRESKA] Obrada zahteva: {t.Exception?.GetBaseException().Message}");
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);
            }
            
            Logger.Log("Obrađivač zahteva zaustavljen.");
        }

        public static async Task ObradaZahtevaAsync(HttpListenerContext context)
        {
            string query = context.Request.RawUrl!.Substring(1);

            // Prazan zahtev - vraćamo HTML formu
            if (string.IsNullOrEmpty(query))
            {
                await PosaljiHtmlOdgovor(context, 
                    "<html><body>" +
                    "<h1>Unesite ime slike</h1>" +
                    "<form id=\"forma\">" +
                    "<input type=\"text\" id=\"unos\" placeholder=\"ime slike\">" +
                    "<button id=\"search\">pretraga</button>" +
                    "</form>" +
                    "<script>" +
                    "document.getElementById('forma').onsubmit = (e) => {" +
                    "  e.preventDefault();" +
                    "  const val = document.getElementById('unos').value;" +
                    "  if (val) { window.location.href = '/' + encodeURIComponent(val); }" +
                    "};" +
                    "</script></body></html>",
                    HttpStatusCode.BadRequest);
                return;
            }

            // Pokušaj dohvatanja iz keša
            Slika? slika = Program.kes[query];
            
            if (slika != null)
            {
                Logger.Log($"[KES POGODAK] '{query}' - veličina: {slika.VelicinaUBajtovima} B");
                await PosaljiSlikuOdgovor(context, slika);
                return;
            }

            Logger.Log($"[KES PROMAŠAJ] '{query}' - pokreće se konverzija");

            // Sprečavanje cache stampede-a - više zahteva za istu sliku
            Task<Slika?> obradaTask = obradeUToku.GetOrAdd(query, async (kljuc) =>
            {
                Logger.Log($"[KONVERZIJA] Početak konverzije za '{kljuc}'");
                
                await semaforKonverzije.WaitAsync();
                try
                {
                    // Samu konverziju izvršavamo na posebnoj niti (compute-bound)
                    Slika? rezultat = await Task.Run(() => Slika.ObradiSliku(kljuc));

                    if (rezultat != null)
                    {
                        Program.kes.DodajStavku(kljuc, rezultat);
                        Logger.Log($"[KONVERZIJA] Uspešno završena za '{kljuc}'");
                    }
                    else
                    {
                        Logger.Log($"[KONVERZIJA] Neuspešna - '{kljuc}' ne postoji na disku");
                    }

                    return rezultat;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[KONVERZIJA] Izuzetak za '{kljuc}': {ex.Message}");
                    return null;
                }
                finally
                {
                    semaforKonverzije.Release();
                    obradeUToku.TryRemove(kljuc, out _);
                }
            });

            // Kontinuacija za logovanje rezultata konverzije
            _ = obradaTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Logger.Log($"[LOG] Greška pri obradi slike '{query}': {t.Exception?.GetBaseException().Message}");
                }
                else if (t.IsCanceled)
                {
                    Logger.Log($"[LOG] Obrada slike '{query}' je otkazana.");
                }
                else if (t.Result == null)
                {
                    Logger.Log($"[LOG] Slika '{query}' nije pronađena na disku.");
                }
                else
                {
                    Logger.Log($"[LOG] Slika '{query}' uspešno konvertovana i keširana.");
                }
            }, TaskContinuationOptions.ExecuteSynchronously);

            // Čekanje rezultata
            slika = await obradaTask;

            if (slika == null)
            {
                await PosaljiHtmlOdgovor(context,
                    "<html><body><h1>Error 404: Item not found!</h1></body></html>",
                    HttpStatusCode.NotFound);
                Logger.Log($"Slika '{query}' nije pronađena.");
                return;
            }

            await PosaljiSlikuOdgovor(context, slika);
            Logger.Log($"Zahtev za '{query}' uspešno obrađen. [Keš: {Program.kes.Count}/{Program.kes.LimitUBajtovima} B]");
        }

        private static async Task PosaljiHtmlOdgovor(HttpListenerContext context, string html, HttpStatusCode status)
        {
            context.Response.ContentType = "text/html";
            context.Response.StatusCode = (int)status;
            byte[] buff = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength64 = buff.Length;
            await context.Response.OutputStream.WriteAsync(buff, 0, buff.Length);
            context.Response.OutputStream.Close();
            context.Response.Close();
        }

        private static async Task PosaljiSlikuOdgovor(HttpListenerContext context, Slika slika)
        {
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
        }
    }
}