using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PCaJour
{
    /// Exécute les mises à jour choisies. Tourne dans un second processus lancé en administrateur.
    public class Installer
    {
        readonly Action<string> log;
        readonly Action<double> progress;
        readonly bool dryRun;
        public bool RebootNeeded;
        public int Failures;

        public Installer(Action<string> log, Action<double> progress, bool dryRun)
        { this.log = log; this.progress = progress; this.dryRun = dryRun; }

        public void Run(List<string> jobs)
        {
            var winget = jobs.Where(j => j.StartsWith("winget=")).Select(j => j.Substring(7)).ToList();
            var wu = jobs.Where(j => j.StartsWith("wu=")).Select(j => j.Substring(3)).ToList();
            var nv = jobs.Where(j => j.StartsWith("nv=")).Select(j => j.Substring(3)).ToList();

            if (wu.Count + nv.Count > 0) RestorePoint();
            if (wu.Count > 0) Guarded("Windows Update", () => WindowsUpdate(wu));
            // App Installer porte winget lui-même : il passe en dernier
            foreach (var id in winget.OrderBy(i => i == "Microsoft.AppInstaller" ? 1 : 0))
            {
                var current = id;
                Guarded(current, () => Winget(current));
            }
            foreach (var url in nv)
            {
                var current = url;
                Guarded("pilote NVIDIA", () => Nvidia(current));
            }

            log("");
            log(Failures == 0 ? "Terminé, tout s'est bien passé." : "Terminé avec " + Failures + " échec(s), voir ci-dessus.");
            if (RebootNeeded) log("Redémarre le PC pour finir l'installation.");
        }

        void Guarded(string name, Action a)
        {
            try { a(); }
            catch (Exception e) { Failures++; log("ÉCHEC " + name + " : " + e.Message); }
        }

        void RestorePoint()
        {
            log("Création d'un point de restauration…");
            if (dryRun) { log("  (test : ignoré)"); return; }
            int code = -1;
            try { code = Stream("powershell", "-NoProfile -Command \"Checkpoint-Computer -Description 'PC a Jour' -RestorePointType MODIFY_SETTINGS\""); }
            catch { }
            log(code == 0 ? "  Terminé (Windows n'en crée qu'un par 24 h)." : "  Pas de point de restauration : la protection système est peut-être désactivée.");
        }

        void Winget(string id)
        {
            if (!Regex.IsMatch(id, @"^[\w.\-+]+$")) throw new InvalidOperationException("identifiant invalide");
            log("");
            log("Mise à jour de " + id + "…");
            string args = "upgrade --id " + id + " --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity";
            if (dryRun) { log("  (test) winget " + args); return; }
            int code = Stream("winget", args);
            if (code == 0) log("  OK");
            else { Failures++; log("  ÉCHEC (code " + code + "). Si l'appli est ouverte, ferme-la et réessaie."); }
        }

        void WindowsUpdate(List<string> ids)
        {
            log("");
            log("Windows Update : recherche…");
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic found = searcher.Search("IsInstalled=0 and IsHidden=0");
            dynamic coll = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl"));
            var titles = new List<string>();
            foreach (dynamic u in found.Updates)
            {
                string uid = u.Identity.UpdateID;
                if (!ids.Contains(uid)) continue;
                if (!dryRun && !(bool)u.EulaAccepted) u.AcceptEula();
                coll.Add(u);
                titles.Add((string)u.Title);
            }
            foreach (var t in titles) log("  " + t);
            if (titles.Count == 0) { log("  Plus rien à installer."); return; }
            if (dryRun) { log("  (test : téléchargement et installation ignorés)"); return; }

            log("Téléchargement…");
            dynamic dl = session.CreateUpdateDownloader();
            dl.Updates = coll;
            dl.Download();
            log("Installation…");
            dynamic inst = session.CreateUpdateInstaller();
            inst.Updates = coll;
            dynamic result = inst.Install();
            for (int i = 0; i < titles.Count; i++)
            {
                int code = (int)result.GetUpdateResult(i).ResultCode;
                if (code != 2) Failures++;
                log("  " + (code == 2 ? "OK" : "ÉCHEC") + " : " + titles[i]);
            }
            if ((bool)result.RebootRequired) RebootNeeded = true;
        }

        void Nvidia(string url)
        {
            var uri = new Uri(url);
            if (uri.Scheme != "https" || !uri.Host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("adresse refusée : " + uri.Host);
            string dir = Path.Combine(Path.GetTempPath(), "PCaJour");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Path.GetFileName(uri.LocalPath));
            log("");
            if (File.Exists(file) && Trust.IsSignedBy(file, "NVIDIA Corporation")) log("Pilote NVIDIA déjà téléchargé.");
            else Download(url, file);

            log("Vérification de la signature NVIDIA…");
            if (!Trust.IsSignedBy(file, "NVIDIA Corporation"))
            {
                try { File.Delete(file); } catch { }
                throw new InvalidOperationException("signature invalide, fichier supprimé sans être lancé");
            }
            log("  Signature valide.");
            if (dryRun) { log("  (test : installateur non lancé) " + file); return; }
            log("Ouverture de l'installateur NVIDIA : suis ses étapes.");
            using (var p = Process.Start(file))
            {
                p.WaitForExit();
                log("  Installateur fermé (code " + p.ExitCode + ").");
            }
            try { File.Delete(file); } catch { }
        }

        void Download(string url, string file)
        {
            log("Téléchargement de " + Path.GetFileName(file) + "…");
            string part = file + ".part";
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = Util.UserAgent;
                wc.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage / 100.0);
                wc.DownloadFileTaskAsync(new Uri(url), part).GetAwaiter().GetResult();
            }
            progress(1);
            if (File.Exists(file)) File.Delete(file);
            File.Move(part, file);
        }

        int Stream(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = new Process { StartInfo = psi })
            {
                DataReceivedEventHandler h = (s, e) =>
                {
                    if (e.Data == null) return;
                    var t = e.Data.Trim();
                    // winget dessine des barres de progression qu'on ne veut pas dans le journal
                    if (t.Length < 3 || t.IndexOfAny(new[] { '█', '▒' }) >= 0 || Regex.IsMatch(t, @"^[\s\-\\|/]+$")) return;
                    log("  " + t);
                };
                p.OutputDataReceived += h;
                p.ErrorDataReceived += h;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }
    }
}
