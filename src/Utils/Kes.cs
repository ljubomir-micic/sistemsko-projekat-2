using System.Threading;

namespace Projekat
{
    public class Kes
    {
        public long LimitUBajtovima => Podesavanja.limitKesaUBajtovima;
        
        private readonly Dictionary<string, Slika> kes = new Dictionary<string, Slika>();
        // ReaderWriterLockSlim - dozvoljava više čitača, jedan pisac
        private readonly ReaderWriterLockSlim kesLock = new ReaderWriterLockSlim();
        
        private long _trenutnaVelicina = 0;

        public long Count => _trenutnaVelicina;

        public Kes() { }

        private string? KljucNajveceSlikeMemorisaneUKesMemoriji
        {
            get
            {
                kesLock.EnterReadLock();
                try
                {
                    if (kes.Count == 0) return null;
                    return kes.MaxBy(x => x.Value.VelicinaUBajtovima).Key;
                }
                finally
                {
                    kesLock.ExitReadLock();
                }
            }
        }

        public void DodajStavku(string link, Slika slika)
        {
            kesLock.EnterWriteLock();
            try
            {
                // Ako već postoji, ne dodajemo ponovo
                if (kes.ContainsKey(link))
                {
                    return;
                }

                // Brisanje dok ne bude dovoljno mesta
                while (_trenutnaVelicina + slika.VelicinaUBajtovima > LimitUBajtovima)
                {
                    string? kljucZaBrisanje = KljucNajveceSlikeMemorisaneUKesMemoriji;
                    if (kljucZaBrisanje != null && kes.Remove(kljucZaBrisanje, out Slika? obrisana))
                    {
                        _trenutnaVelicina -= obrisana.VelicinaUBajtovima;
                        Logger.Log($"[KES] Brisanje '{kljucZaBrisanje}' - {obrisana.VelicinaUBajtovima} B");
                    }
                    else
                    {
                        break;
                    }
                }

                kes[link] = slika;
                _trenutnaVelicina += slika.VelicinaUBajtovima;
                Logger.Log($"[KES] Dodata '{link}' - {slika.VelicinaUBajtovima} B (ukupno: {_trenutnaVelicina}/{LimitUBajtovima} B)");
            }
            finally
            {
                kesLock.ExitWriteLock();
            }
        }

        public Slika? this[string link]
        {
            get
            {
                kesLock.EnterReadLock();
                try
                {
                    if (kes.TryGetValue(link, out Slika? slika))
                    {
                        return slika;
                    }
                    return null;
                }
                finally
                {
                    kesLock.ExitReadLock();
                }
            }
        }

        public void ObrisiStavku(string link)
        {
            kesLock.EnterWriteLock();
            try
            {
                if (kes.Remove(link, out Slika? obrisana))
                {
                    _trenutnaVelicina -= obrisana.VelicinaUBajtovima;
                    Logger.Log($"[KES] Obrisana '{link}' - {obrisana.VelicinaUBajtovima} B");
                }
            }
            finally
            {
                kesLock.ExitWriteLock();
            }
        }
    }
}