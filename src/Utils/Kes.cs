using System.Collections.Concurrent;

namespace Projekat
{
    public class Kes {
        // filename originala i sama obradjena slika
        public readonly int ObjLimit = 100;
        private List<string> ListOfLinks = new List<string>();
        public readonly ConcurrentDictionary<string, Slika> kes = new ConcurrentDictionary<string, Slika>();
        public object _lock = new object();

        public Kes()
        {
            
        }

        public void DodajStavku(string link, Slika slika)
        {
            lock (_lock)
            {   
                if(!ListOfLinks.Contains(link))
                    ListOfLinks.Add(link);

                if(kes.Count < ObjLimit)
                {
                    kes[link] = slika;
                }
                else
                {
                    if(kes.TryRemove(ListOfLinks.First(), out _))
                    {
                        if(kes.TryAdd(link, slika))
                            Console.WriteLine("Slika uspesno dodata.");
                    }
                    else {
                        System.Console.WriteLine("Doslo je do problema pri oslobadjanju prostora u kesu.");
                    }
                }
            }
        }

        protected Slika? PronadjiStavku(string link)
        {
            lock (_lock)
            {
                if (kes.ContainsKey(link))
                {
                    return kes[link];
                }
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
