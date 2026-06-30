using System.Collections.Concurrent;
using System.Threading;

namespace Projekat
{
    public class Kes
    {
        public long LimitUBajtovima => Podesavanja.limitKesaUBajtovima;
        
        // ConcurrentDictionary nam daje performanse bez ručnog zaključavanja
        private readonly ConcurrentDictionary<string, Slika> _kes = new ConcurrentDictionary<string, Slika>();
        
        // Koristimo za praćenje redosleda pristupa (LRU pristup)
        private readonly ConcurrentQueue<string> _redosledKljuceva = new ConcurrentQueue<string>();
        
        private long _trenutnaVelicina = 0;

        public long Count => Interlocked.Read(ref _trenutnaVelicina);

        public void DodajStavku(string link, Slika slika)
        {
            if (_kes.TryAdd(link, slika))
            {
                Interlocked.Add(ref _trenutnaVelicina, slika.VelicinaUBajtovima);
                _redosledKljuceva.Enqueue(link);

                // Efikasna evikcija (FIFO/LRU princip)
                while (_trenutnaVelicina > LimitUBajtovima)
                {
                    if (_redosledKljuceva.TryDequeue(out string stariLink))
                    {
                        if (_kes.TryRemove(stariLink, out Slika obrisana))
                        {
                            Interlocked.Add(ref _trenutnaVelicina, -obrisana.VelicinaUBajtovima);
                        }
                    }
                }
            }
        }

        public Slika? this[string link]
        {
            get
            {
                return _kes.TryGetValue(link, out Slika? slika) ? slika : null;
            }
        }
    }
}