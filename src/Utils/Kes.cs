using System.Threading;
using System.Collections.Concurrent;

namespace Projekat
{
    public class Kes
    {
        public long LimitUBajtovima => Podesavanja.limitKesaUBajtovima;
        
        // PROMENA: ConcurrentDictionary umesto Dictionary + lock
        private readonly ConcurrentDictionary<string, Slika> kes = new ConcurrentDictionary<string, Slika>();
        
        private long _trenutnaVelicina = 0;
        private readonly object _lockVelicina = new object();

        public long Count => _trenutnaVelicina;

        public void DodajStavku(string link, Slika slika)
        {
            // Prvo proverimo da li već postoji
            if (kes.ContainsKey(link))
                return;

            // Dodajemo samo ako ima mesta
            lock (_lockVelicina)
            {
                // Provera da li ima mesta
                if (_trenutnaVelicina + slika.VelicinaUBajtovima > LimitUBajtovima)
                {
                    // Brišemo najveće slike dok ne bude mesta
                    while (_trenutnaVelicina + slika.VelicinaUBajtovima > LimitUBajtovima)
                    {
                        if (!kes.TryRemove(kes.MaxBy(x => x.Value.VelicinaUBajtovima).Key, out Slika? obrisana))
                            break;
                        _trenutnaVelicina -= obrisana.VelicinaUBajtovima;
                    }
                }

                // Konačno dodavanje
                if (kes.TryAdd(link, slika))
                {
                    _trenutnaVelicina += slika.VelicinaUBajtovima;
                }
            }
        }

        public Slika? this[string link]
        {
            get
            {
                kes.TryGetValue(link, out Slika? slika);
                return slika;
            }
        }
    }
}