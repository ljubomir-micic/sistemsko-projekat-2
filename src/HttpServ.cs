using System;
using System.Net;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.IO;

namespace Projekat
{
    class HttpServ {
        public static HttpClient client = new HttpClient();
        private static BlockingCollection<HttpListenerContext> redZahteva = new BlockingCollection<HttpListenerContext>();
        private static ConcurrentDictionary<string, Task<Slika?>> obradeUToku = new ConcurrentDictionary<string, Task<Slika?>>();
        private static SemaphoreSlim semaforKonverzije = new SemaphoreSlim(4, 4);

        public static void StartServ()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{Podesavanja.brojPorta}/");
            listener.Start();
            Console.WriteLine("Server je pokrenut");
            Console.WriteLine($"http://localhost:{Podesavanja.brojPorta}/");

            Thread graceful = new Thread(() =>
            {
                Console.WriteLine("Za graceful shutdown pritisnite taster 'q'.");
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
                    else { Thread.Sleep(50); }
                }
            });
            graceful.Start();

            Thread listenerThread = new Thread(() =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        HttpListenerContext context = listener.GetContext();
                        redZahteva.Add(context);
                    }
                    catch (Exception) { break; }
                }
                redZahteva.CompleteAdding();
            });
            listenerThread.IsBackground = true;
            listenerThread.Start();

            Task.Factory.StartNew(ProcesirajRedZahteva, TaskCreationOptions.LongRunning);

            graceful.Join();
        }

        private static async Task ProcesirajRedZahteva()
        {
            foreach (var context in redZahteva.GetConsumingEnumerable())
            {
                _ = ObradaZahtevaAsync(context);
            }
        }

        public static async Task ObradaZahtevaAsync(HttpListenerContext context)
        {
            string query = context.Request.RawUrl!.Substring(1);

            if (string.IsNullOrEmpty(query))
            {
                // DONE: Vrati prazan HTML sa Error 400 Bad Request
                string respStr = "<html><body><h1>Error 400: Bad Request!</h1><form id=\"forma\"><input type=\"text\" id=\"unos\" placeholder=\"ime slike\"><button id=\"search\">pretraga</button></form></body><script>document.getElementById('forma').onsubmit = (e) => { e.preventDefault(); const val = document.getElementById('unos').value; if (val) { window.location.href = '/' + encodeURIComponent(val); }};</script></html>";
                context.Response.ContentType = "text/html";
                context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
                byte[] buff = Encoding.UTF8.GetBytes(respStr);
                context.Response.ContentLength64 = buff.Length;
                
                await context.Response.OutputStream.WriteAsync(buff, 0, buff.Length);
                context.Response.OutputStream.Close();
                // context.Response.ContentLength64 = 0;
                context.Response.Close();
                return;
            }

            Slika? slika = Program.kes[query];
            if (slika == null)
            {
                // Nit koja prva stigne kreira Task, sve ostale niti za istu sliku dobijaju referencu na taj isti Task.
                Task<Slika?> obradaTask = obradeUToku.GetOrAdd(query, async (kljuc) =>
                {
                    await semaforKonverzije.WaitAsync();
                    try
                    {
                        return await Task.Run(() => Slika.ObradiSliku(kljuc));
                    }
                    finally
                    {
                        semaforKonverzije.Release();
                    }
                });

                _ = obradaTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Console.WriteLine($"[LOG] Greška pri obradi slike {query}.");
                    else if (t.Result == null)
                        Console.WriteLine($"[LOG] Konverzija neuspešna - slika {query} ne postoji na disku.");
                    else
                        Console.WriteLine($"[LOG] Konverzija uspešno završena za {query}.");
                    
                    obradeUToku.TryRemove(query, out _);
                }, TaskContinuationOptions.ExecuteSynchronously);

                slika = await obradaTask;

                if (slika == null)
                {
                    string respStr = $"<html><body><h1>Error 404: Item not found!</h1></body></html>";
                    context.Response.ContentType = "text/html";
                    context.Response.StatusCode = (int) HttpStatusCode.NotFound;
                    byte[] buff = Encoding.UTF8.GetBytes(respStr);
                    context.Response.ContentLength64 = buff.Length;
                    await context.Response.OutputStream.WriteAsync(buff, 0, buff.Length);
                    context.Response.OutputStream.Close();
                    // context.Response.ContentLength64 = 0;
                    context.Response.Close();
                    Console.WriteLine($"Slika {query} nije pronadjena.");
                    return;
                }

                Program.kes.DodajStavku(query, slika); // Proveriti da li je kes pun i obrisati stavku ako jeste koristeci
                                            // ogranicenje velicine
            }

            string responseStirng = $"<html><body><img src='data:image/jpeg;base64,{Convert.ToBase64String(slika.GetData())}' /></body></html>";
            context.Response.ContentType = "text/html";
            context.Response.StatusCode = (int) HttpStatusCode.OK;
            byte[] buffer = Encoding.UTF8.GetBytes(responseStirng);
            context.Response.ContentLength64 = buffer.Length;
            
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
            context.Response.Close();
            Console.WriteLine("Zahtev je uspesno obradjen! [memory: "+Program.kes.Count+"/"+Program.kes.LimitUBajtovima+"]");
        }
    }
}