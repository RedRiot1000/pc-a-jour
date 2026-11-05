using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PCaJour
{
    // Mise à jour automatique depuis la dernière « release » GitHub du dépôt AppInfo.Repo.
    // Windows autorise à renommer un .exe en cours d'exécution : on renomme l'ancien en .old,
    // on met le nouveau à sa place, et il prend effet au prochain lancement.
    public static class Updater
    {
        public static string ExePath { get { return Assembly.GetExecutingAssembly().Location; } }
        static string OldPath { get { return ExePath + ".old"; } }

        public static void CleanupOld()
        {
            try { if (File.Exists(OldPath)) File.Delete(OldPath); } catch { }
        }

        /// Renvoie la nouvelle version installée, ou null s'il n'y avait rien à faire.
        public static Task<string> CheckAndInstallAsync()
        {
            return Task.Run(() =>
            {
                try { return CheckAndInstall(); }
                catch { return null; }   // pas d'Internet, dossier protégé… : on réessaiera au prochain lancement
            });
        }

        static WebClient Client()
        {
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "PCaJour/" + AppInfo.Version;
            return wc;
        }

        public static string CheckAndInstall()
        {
            string json;
            using (var wc = Client())
            {
                wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = wc.DownloadString("https://api.github.com/repos/" + AppInfo.Repo + "/releases/latest");
            }
            var rel = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var tag = (rel["tag_name"] as string ?? "").TrimStart('v', 'V');
            Version latest, current = new Version(AppInfo.Version);
            if (!Version.TryParse(tag, out latest) || latest <= current) return null;

            string url = null;
            foreach (var a in (IEnumerable)rel["assets"])
            {
                var asset = (Dictionary<string, object>)a;
                if (string.Equals(asset["name"] as string, "PCaJour.exe", StringComparison.OrdinalIgnoreCase))
                    url = asset["browser_download_url"] as string;
            }
            if (url == null) return null;

            var exe = ExePath;
            var tmp = exe + ".download";
            using (var wc = Client()) wc.DownloadFile(url, tmp);

            // Vérifie que c'est bien un exécutable complet avant de remplacer quoi que ce soit
            var bytes = File.ReadAllBytes(tmp);
            if (bytes.Length < 20000 || bytes[0] != 'M' || bytes[1] != 'Z') { File.Delete(tmp); return null; }

            CleanupOld();
            File.Move(exe, OldPath);
            try { File.Move(tmp, exe); }
            catch { File.Move(OldPath, exe); throw; }
            return latest.ToString();
        }
    }
}
