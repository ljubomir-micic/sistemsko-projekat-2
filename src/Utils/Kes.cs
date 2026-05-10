using System.Collections.Concurrent;

namespace Projekat
{
    public class Kes {
        // filename originala i sama obradjena slika
        public readonly long LimitUBajtovima = 3461760;
        public readonly ConcurrentDictionary<string, Slika> kes = new ConcurrentDictionary<string, Slika>();
        public object _lock = new object();

        public Kes()
        {
            
        }

        public long Count {
            get {
                long e = 0;
                foreach(var Kljuc in kes.Keys)
                    e += kes[Kljuc].VelicinaUBajtovima;
                return e;
            }
        }

        public string? KljucNajveceSlikeMemorisaneUKesMemoriji {
            get {
                if (kes.IsEmpty) return null;
                return kes.MaxBy(x => x.Value.VelicinaUBajtovima).Key;
            }
        }

        public void DodajStavku(string link, Slika slika)
        {
            lock (_lock)
            {
                while (this.Count + slika.VelicinaUBajtovima > LimitUBajtovima) {
                    string? kljucZaBrisanje = this.KljucNajveceSlikeMemorisaneUKesMemoriji;
                    
                    if (kljucZaBrisanje != null)
                        kes.TryRemove(kljucZaBrisanje, out _);
                    else
                        break;
                }
                
                kes[link] = slika;
            }
        }

        protected Slika? PronadjiStavku(string link)
        {
            lock (_lock)
            {
                if (kes.ContainsKey(link))
                {
                    Console.WriteLine("Kes pogodak.");
                    return kes[link];
                }
                Console.WriteLine("Kes promasaj.");
                return null;
            }
        }

        public void ObrisiStavku(string link)
        {
            lock (_lock)
            {
                if (kes.ContainsKey(link))
                {
                    kes.TryRemove(link, out _);
                }
            }
        }
    
        public Slika? this[string link] {
            get { return PronadjiStavku(link); }
        }
    }
}
