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
        // ═══════════════════════════════════════════════════════════════════
        // DELJENI RESURSI
        // ═══════════════════════════════════════════════════════════════════

        // Channel<T> zamenjuje BlockingCollection<T>.
        //
        // BoundedChannel sa kapacitetom 100:
        //   - FullMode.Wait: ako je red pun, Writer.WriteAsync() čeka asinhrono
        //     (back-pressure) umesto da baci izuzetak ili odbaci zahtev.
        //   - SingleReader = true: samo jedan task čita (dispatcher),
        //     što Channel-u dozvoljava interne optimizacije.
        //   - SingleWriter = false: listener nit i potencijalno više izvora
        //     mogu pisati konkurentno.
        //
        // Zašto Channel umesto BlockingCollection:
        //   BlockingCollection.GetConsumingEnumerable() BLOKIRA OS nit dok
        //   nema elementa. To se kompenzuje sa TaskCreationOptions.LongRunning
        //   (dedicated OS nit van ThreadPool-a) — funkcionalno ispravno, ali
        //   neefikasno. Channel.Reader.ReadAllAsync() je prava async operacija:
        //   ThreadPool nit se OSLOBAĐA tokom čekanja i može da radi drugi posao.
        //   Dispatcher task više ne zahteva LongRunning.
        private static readonly Channel<HttpListenerContext> redZahteva =
            Channel.CreateBounded<HttpListenerContext>(
                new BoundedChannelOptions(100)
                {
                    FullMode    = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

        // ConcurrentDictionary čuva Task-ove obrada koje su u toku.
        // Ključ: naziv slike (query string).
        // Vrednost: Task koji vrši konverziju.
        //
        // Svrha: anti-stampede mehanizam — ako 10 zahteva istovremeno traže
        // istu sliku koja nije u kešu, GetOrAdd() garantuje da se kreira
        // samo JEDAN Task za tu sliku. Svih 10 zahteva dobija referencu na
        // isti Task i čeka njegov rezultat (await obradaTask).
        //
        // KRITIČNA SEKCIJA: GetOrAdd() je atomično za rečnik, ali lambda
        // unutar nje NIJE atomična — može se pozvati više puta konkurentno.
        // Međutim, samo jedan rezultat će biti usvojen u rečnik (ostali se
        // odbacuju). Za naš slučaj ovo je prihvatljivo jer je lambda
        // idempotentna (uvek radi istu konverziju istog fajla).
        private static readonly ConcurrentDictionary<string, Task<Slika?>> obradeUToku =
            new ConcurrentDictionary<string, Task<Slika?>>();

        // SemaphoreSlim ograničava broj paralelnih konverzija slike.
        // Inicijalna i maksimalna vrednost: 4 — znači najviše 4 task-a
        // mogu istovremeno da izvršavaju CPU-bound konverziju.
        //
        // Zašto SemaphoreSlim a ne klasični Semaphore:
        //   SemaphoreSlim podržava WaitAsync() — asinhrono čekanje bez
        //   blokiranja ThreadPool niti. Klasični Semaphore.WaitOne() bi
        //   blokirao nit dok slot ne postane slobodan.
        //
        // KRITIČNA SEKCIJA: WaitAsync()/Release() par mora uvek biti u
        //   try/finally bloku da bi se slot oslobodio čak i pri izuzetku.
        private static readonly SemaphoreSlim semaforKonverzije = new SemaphoreSlim(4, 4);

        // ═══════════════════════════════════════════════════════════════════
        // POKRETANJE SERVERA
        // ═══════════════════════════════════════════════════════════════════

        public static void StartServ()
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{Podesavanja.brojPorta}/");
            listener.Start();
            Logger.Log("Server je pokrenut");
            Logger.Log($"http://localhost:{Podesavanja.brojPorta}/");

            // ── Graceful shutdown nit ────────────────────────────────────
            // Klasična nit je opravdana ovde: Console.ReadKey() je blokirajući
            // poziv koji samo čeka korisnički unos. Nema koristi od async-a —
            // nit ne radi ništa korisno između pritisaka tastera.
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

            // ── Listener nit ─────────────────────────────────────────────
            // Klasična nit je opravdana ovde: HttpListener.GetContext() je
            // blokirajući sistemski poziv. Nit je UVEK u stanju čekanja —
            // nema "slobodnog" vremena koje bi async mogao iskoristiti.
            // Koristimo IsBackground = true da nit ne sprečava gašenje procesa.
            Thread listenerThread = new Thread(() =>
            {
                while (listener.IsListening)
                {
                    try
                    {
                        HttpListenerContext context = listener.GetContext();

                        // TryWrite umesto Add():
                        // Pokušavamo ne-blokirajući upis. Ako je red pun
                        // (što se ne bi trebalo desiti uz FullMode.Wait u
                        // WriteAsync scenariju), odbijamo zahtev sa 503.
                        // Listener nit ne sme da bude async, pa koristimo
                        // TryWrite umesto await WriteAsync().
                        if (!redZahteva.Writer.TryWrite(context))
                        {
                            Logger.Log("[WARN] Red zahteva je pun — zahtev odbijen (503).");
                            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            context.Response.ContentLength64 = 0;
                            context.Response.Close();
                        }
                    }
                    catch (Exception)
                    {
                        // HttpListener je zaustavljen (graceful shutdown)
                        break;
                    }
                }

                // Signaliziramo dispatcheru da više nema novih zahteva.
                // ReadAllAsync() će završiti iteraciju nakon što isprazni red.
                // Ekvivalent BlockingCollection.CompleteAdding().
                redZahteva.Writer.Complete();
            });
            listenerThread.IsBackground = true;
            listenerThread.Start();

            // ── Dispatcher task ───────────────────────────────────────────
            // Pokrećemo dispatcher kao običan Task (BEZ LongRunning).
            //
            // Sa BlockingCollection: bio je potreban LongRunning jer je
            // GetConsumingEnumerable() blokirala OS nit — morala je biti
            // dedicated nit van ThreadPool-a da ne bi "zauzimala" pool.
            //
            // Sa Channel<T>: ReadAllAsync() je prava async operacija.
            // Dispatcher task se SUSPENDUJE (ne blokira) dok nema elementa
            // u redu, i ThreadPool nit se vraća u pool za drugi posao.
            // LongRunning više nije ni potreban ni poželjan.
            //
            // Zašto ne await ovde: StartServ() je sinhronа metoda.
            // Koristimo discard (_) jer nas ne zanima Task objekat —
            // greške hvatamo unutar ProcesirajRedZahteva.
            _ = ProcesirajRedZahteva();

            graceful.Join();
        }

        // ═══════════════════════════════════════════════════════════════════
        // DISPATCHER — uzima zahteve iz Channel-a i pokreće obradu
        // ═══════════════════════════════════════════════════════════════════

        private static async Task ProcesirajRedZahteva()
        {
            // ReadAllAsync() vraća IAsyncEnumerable<HttpListenerContext>.
            //
            // Kako funkcioniše:
            //   - Ako ima elementa u Channel-u: odmah ga uzima i nastavlja.
            //   - Ako je Channel prazan: SUSPENDUJE se (await) i oslobađa
            //     ThreadPool nit. Kada novi element stigne, nastavlja od
            //     mesta suspenzije na (potencijalno drugoj) ThreadPool niti.
            //   - Kada Writer.Complete() bude pozvan I red bude prazan:
            //     iteracija se završava prirodno.
            //
            // Ovo je ključna razlika od GetConsumingEnumerable() koji BLOKIRA.
            await foreach (var context in redZahteva.Reader.ReadAllAsync())
            {
                // Ne await-ujemo ObradaZahtevaAsync — dispatcher ne čeka
                // da se zahtev obradi, već odmah uzima sledeći iz reda.
                // ContinueWith hvata neobrađene izuzetke iz task-a.
                _ = ObradaZahtevaAsync(context).ContinueWith(
                    t => Logger.Log($"[Logger.Log] Neočekivana greška: {t.Exception}"),
                    TaskContinuationOptions.OnlyOnFaulted
                );
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // OBRADA JEDNOG ZAHTEVA
        // ═══════════════════════════════════════════════════════════════════

        public static async Task ObradaZahtevaAsync(HttpListenerContext context)
        {
            string query = context.Request.RawUrl!.Substring(1);

            // ── Validacija query stringa ──────────────────────────────────
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

            // ── Provera keša ──────────────────────────────────────────────
            Slika? slika = Program.kes[query];

            if (slika == null)
            {
                // ── Anti-stampede mehanizam ───────────────────────────────
                //
                // Problem: ako 50 zahteva istovremeno traži "slika.jpg" koja
                // nije u kešu, bez zaštite bi svi pokrenuli konverziju.
                //
                // Rešenje: GetOrAdd() kreira Task samo jednom po ključu.
                // Svi konkurentni zahtevi za isti ključ dobijaju ISTI Task.
                // Lambda se može pozvati više puta (nije atomična), ali samo
                // jedan rezultat biva usvojen u rečnik — ostali se tiho odbacuju.
                //
                // await obradaTask: svi zahtevi čekaju asinhrono na isti Task.
                // Kada Task završi, svi se "bude" i dobijaju isti rezultat.
                Task<Slika?> obradaTask = obradeUToku.GetOrAdd(query, async (kljuc) =>
                {
                    // SemaphoreSlim.WaitAsync(): asinhrono čekanje na slobodan slot.
                    // Ako su sva 4 slota zauzeta, ovaj task se suspenduje (ne blokira nit)
                    // dok jedan slot ne postane slobodan.
                    await semaforKonverzije.WaitAsync();
                    try
                    {
                        // Task.Run() za CPU-bound operaciju:
                        // Konverzija slike je čista CPU operacija — nema I/O čekanja.
                        // Task.Run() je opravdan ovde jer eksplicitno prebacujemo
                        // CPU-bound rad na ThreadPool nit, oslobađajući trenutnu nit
                        // za druge async operacije tokom konverzije.
                        Slika? rezultat = await Task.Run(() => Slika.ObradiSliku(kljuc));

                        if (rezultat != null)
                            Program.kes.DodajStavku(kljuc, rezultat);

                        return rezultat;
                    }
                    finally
                    {
                        // UVEK oslobađamo semafor — čak i pri izuzetku.
                        // Bez finally, izuzetak bi zauvek "pojeo" jedan slot.
                        semaforKonverzije.Release();

                        // Uklanjamo završeni Task iz rečnika.
                        // Nakon ovoga, sledeći zahtev za isti fajl će ponovo
                        // proveriti keš (gde će ga naći ako je konverzija uspela).
                        obradeUToku.TryRemove(kljuc, out _);
                    }
                });

                // ContinueWith za logovanje rezultata konverzije.
                //
                // ExecuteSynchronously: nastavak se izvršava na istoj niti
                // koja je završila Task, bez dodatnog ThreadPool scheduling-a.
                // Opravdano za kratke operacije kao što je logovanje.
                _ = obradaTask.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Logger.Log($"[LOG] Greška pri obradi slike {query}.");
                    else if (t.Result == null)
                        Logger.Log($"[LOG] Konverzija neuspešna — slika '{query}' ne postoji na disku.");
                    else
                        Logger.Log($"[LOG] Konverzija uspešno završena za '{query}'.");
                }, TaskContinuationOptions.ExecuteSynchronously);

                // Čekamo rezultat (asinhrono — ne blokira nit).
                // Ako je drugi task već pokrenuo konverziju, mi samo
                // čekamo na njegov rezultat bez ponovnog pokretanja.
                slika = await obradaTask;

                // ── Slika nije pronađena na disku ─────────────────────────
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

            // ── Uspešan odgovor — vraćamo konvertovanu sliku ─────────────
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