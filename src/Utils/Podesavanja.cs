namespace Projekat
{
    public class Podesavanja
    {
        public const int brojPorta = 8080;
        public static readonly string direktorijumSlika = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../assets")
        );
        public const long limitKesaUBajtovima = 3_461_760;
    }
}