using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Projekat
{
    public class Slika
    {
        private byte[] data;

        public Slika(byte[] data)
        {
            this.data = data;
        }

        public byte[] GetData()
        {
            return data;
        }

        public long VelicinaUBajtovima { get => data.Length; }

        public static Slika? ObradiSliku(string path)
        {
            if(string.IsNullOrEmpty(path)) return null;
            
            Slika? slika = null;
            try
            {
                string inputPath = Path.GetFullPath(Path.Combine(Podesavanja.direktorijumSlika, path));

                // Osigurava da je putanja stvarno unutar imgs/ direktorijuma
                if (!inputPath.StartsWith(Podesavanja.direktorijumSlika))
                    return null;

                byte[] slikaPolja = File.ReadAllBytes(inputPath);

                using (var image = Image.Load(slikaPolja))
                {
                    image.Mutate(x => x.Grayscale());

                    using (var ms = new MemoryStream())
                    {
                        image.SaveAsJpeg(ms);
                        slika = new Slika(ms.ToArray());
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            return slika;
        }
    }
}
