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
            Slika? slika = null;
            try
            {
                string inputPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
                // Console.WriteLine(inputPath); // podsetnik: imgs moraju da postoje u sistemsko-projekat-2/src/bin/Debug/net?.0/...
                if (!File.Exists(inputPath) || string.IsNullOrEmpty(path))
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
