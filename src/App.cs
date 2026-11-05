using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Markup;

namespace PCaJour
{
    public static class Program
    {
        public static string LogFile;

        [STAThread]
        public static void Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            var a = args.ToList();

            // --scan-json fichier : analyse sans fenêtre (test)
            int json = a.IndexOf("--scan-json");
            if (json >= 0) { ScanToJson(a[json + 1]); return; }

            // --update-test fichier : lance la mise à jour sans fenêtre (test)
            int upd = a.IndexOf("--update-test");
            if (upd >= 0)
            {
                string result;
                try { result = Updater.CheckAndInstall() ?? "rien à faire"; }
                catch (Exception e) { result = "erreur : " + e.Message; }
                File.WriteAllText(a[upd + 1], AppInfo.Version + " -> " + result);
                return;
            }

            // --sig fichier éditeur : teste la vérification de signature
            int sig = a.IndexOf("--sig");
            if (sig >= 0)
            {
                File.WriteAllText(a[sig + 1] + ".sig.txt", Trust.IsValid(a[sig + 1]) + " " + Trust.IsSignedBy(a[sig + 1], a[sig + 2]));
                return;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                MessageBox.Show("Erreur inattendue :\n\n" + e.Exception.Message, "PC à Jour", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCaJour.Theme.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(res));

            // --do tâche… : processus administrateur qui installe. --dry-run ne change rien au système.
            int run = a.IndexOf("--do");
            if (run >= 0)
            {
                bool dry = a.Contains("--dry-run");
                int log = a.IndexOf("--log");
                if (log >= 0) LogFile = a[log + 1];
                var jobs = a.Skip(run + 1).Where(j => Array.IndexOf(new[] { "wu", "winget", "nv" }, j.Split('=')[0]) >= 0).ToList();
                app.Run(new RunWindow(jobs, dry, log >= 0));
                return;
            }

            var win = new MainWindow();
            int snap = a.IndexOf("--snap");
            win.NoUpdate = snap >= 0;
            if (snap >= 0)
                win.ScanFinished += () => win.Dispatcher.BeginInvoke(new Action(() => { win.Snapshot(a[snap + 1]); win.Close(); }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            app.Run(win);
        }

        static void ScanToJson(string path)
        {
            var all = new List<Item>();
            var h = Checks.ReadHardware();
            Scanner.Run(h, (found, done, total) => { lock (all) all.AddRange(found); }).Wait();
            var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            File.WriteAllText(path, js.Serialize(new { h.Summary, Items = all }), Encoding.UTF8);
        }
    }
}
