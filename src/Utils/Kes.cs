namespace Projekat
{
    public class Kes {
        public long LimitUBajtovima => Podesavanja.limitKesaUBajtovima;
        private readonly Dictionary<string, Slika> kes = new Dictionary<string, Slika>();
        private readonly object _lock = new object();

        public Kes()
        {
            
        }

        private long _trenutnaVelicina = 0;

        public long Count => _trenutnaVelicina;

        private string? KljucNajveceSlikeMemorisaneUKesMemoriji {
            get {
                if (kes.Count == 0) return null;
                return kes.MaxBy(x => x.Value.VelicinaUBajtovima).Key;
            }
        }

        public void DodajStavku(string link, Slika slika)
        {
            lock (_lock)
            {
                while (_trenutnaVelicina + slika.VelicinaUBajtovima > LimitUBajtovima)
                {
                    string? kljucZaBrisanje = this.KljucNajveceSlikeMemorisaneUKesMemoriji;
                    if (kljucZaBrisanje != null)
                    {
                        if (kes.Remove(kljucZaBrisanje, out Slika? obrisana))
                            _trenutnaVelicina -= obrisana.VelicinaUBajtovima;
                    }
                    else break;
                }

                if (!kes.ContainsKey(link))
                {
                    kes[link] = slika;
                    _trenutnaVelicina += slika.VelicinaUBajtovima;
                }
            }
        }

        private Slika? PronadjiStavku(string link)
        {
            lock (_lock)
            {
                if (kes.TryGetValue(link, out Slika? slika))
                {
                    Console.WriteLine("Kes pogodak.");
                    return slika;
                }
                Console.WriteLine("Kes promasaj.");
                return null;
            }
        }

        public void ObrisiStavku(string link)
        {
            lock (_lock)
            {
                if (kes.Remove(link, out Slika? obrisana))
                    _trenutnaVelicina -= obrisana.VelicinaUBajtovima;
            }
        }
    
        public Slika? this[string link] {
            get { return PronadjiStavku(link); }
        }
    }
}
