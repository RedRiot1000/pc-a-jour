using System.Reflection;

// release.ps1 change ces valeurs à chaque publication
[assembly: AssemblyVersion("1.0.0")]
[assembly: AssemblyFileVersion("1.0.0")]
[assembly: AssemblyTitle("PC à Jour")]
[assembly: AssemblyProduct("PC à Jour")]

namespace PCaJour
{
    public static class AppInfo
    {
        public const string Version = "1.0.0";
        public const string Repo = "RedRiot1000/pc-a-jour";   // dépôt GitHub qui publie les mises à jour
    }
}
