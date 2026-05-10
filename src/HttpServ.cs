using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Text;
using System.IO;

namespace Projekat
{
    class HttpServ {
        public static HttpClient client = new HttpClient();

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
                        else
                        {
                            Console.WriteLine("Nepoznat unos.");
                        }
                    }
                    else
                    {
                        Thread.Sleep(50);
                    }
                }
            });
            graceful.Start();

            while (true)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(new WaitCallback(HttpServ.ObradaZahteva!), context);
                }
                catch (Exception)
                {
                    break;
                }
            }

            graceful.Join();
        }

        public static void ObradaZahteva(object state)
        {
            HttpListenerContext context = (HttpListenerContext) state;
            string query = context.Request.RawUrl!.Substring(1);

            if (string.IsNullOrEmpty(query))
            {
                // DONE: Vrati prazan HTML sa Error 400 Bad Request
                string respStr = "<html><body><h1>Error 400: Bad Request!</h1><form id=\"forma\"><input type=\"text\" id=\"unos\" placeholder=\"ime slike\"><button id=\"search\">pretraga</button></form></body><script>document.getElementById('forma').onsubmit = (e) => { e.preventDefault(); const val = document.getElementById('unos').value; if (val) { window.location.href = '/' + encodeURIComponent(val); }};</script></html>";
                context.Response.ContentType = "text/html";
                context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
                byte[] buff = Encoding.UTF8.GetBytes(respStr);
                context.Response.ContentLength64 = buff.Length;
                context.Response.OutputStream.Write(buff, 0, buff.Length);
                context.Response.OutputStream.Close();
                // context.Response.ContentLength64 = 0;
                context.Response.Close();
                return;
            }

            Slika? slika = Program.kes[query];
            if (slika == null)
            {
                // DONE: Pronadji sliku iz file sistema, obradi je i smesti u kes
                slika = Slika.ObradiSliku(query);

                // DONE: Ako slika ne postoji, vrati prazan HTML sa Error 404 Not Found => RESENO
                if (slika == null)
                {
                    string respStr = $"<html><body><h1>Error 404: Item not found!</h1></body></html>";
                    context.Response.ContentType = "text/html";
                    context.Response.StatusCode = (int) HttpStatusCode.NotFound;
                    byte[] buff = Encoding.UTF8.GetBytes(respStr);
                    context.Response.ContentLength64 = buff.Length;
                    context.Response.OutputStream.Write(buff, 0, buff.Length);
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
            context.Response.OutputStream.Write(buffer, 0, buffer.Length);
            context.Response.OutputStream.Close();
            context.Response.Close();
            Console.WriteLine("Zahtev je uspesno obradjen! [memory: "+Program.kes.Count+"/"+Program.kes.LimitUBajtovima+"]");
        }
    }
}
