using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Win32;

namespace PCaJour
{
    public class Hardware
    {
        public string BoardMaker, Board, BiosVersion, Cpu, Summary;
        public DateTime BiosDate;
        public List<string> Gpus = new List<string>();
    }

    public static class Checks
    {
        const string NvLookup = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
        const string NvDriver = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={0}&pfid={1}&osID={2}&languageCode=1033&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";
        const string RealtekPage = "https://www.realtek.com/Download/List?cate_id=584";
        const string AmdSite = "https://www.amd.com/fr/support/download/drivers.html";

        // ---------- utilitaires WMI / registre ----------
        static List<ManagementObject> Wmi(string query, string scope = @"root\cimv2")
        {
            using (var s = new ManagementObjectSearcher(scope, query))
                return s.Get().Cast<ManagementObject>().ToList();
        }
        static string S(ManagementObject o, string prop)
        {
            var v = o[prop]; return v == null ? "" : v.ToString().Trim();
        }
        static string TryGet(string url)
        {
            try { return Util.HttpGet(url); } catch { return ""; }
        }
        static string BoardName(string board) { return Regex.Replace(board, @"\s*\(.*\)", "").Trim(); }

        static string UninstallVersion(string exactName)
        {
            foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                using (var root = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (root == null) continue;
                    foreach (var name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name))
                            if (k != null && string.Equals(k.GetValue("DisplayName") as string, exactName, StringComparison.OrdinalIgnoreCase))
                                return k.GetValue("DisplayVersion") as string;
                }
            return null;
        }

        public static Hardware ReadHardware()
        {
            var h = new Hardware();
            var bb = Wmi("SELECT Manufacturer,Product FROM Win32_BaseBoard").First();
            h.BoardMaker = S(bb, "Manufacturer"); h.Board = S(bb, "Product");
            var bios = Wmi("SELECT SMBIOSBIOSVersion,ReleaseDate FROM Win32_BIOS").First();
            h.BiosVersion = S(bios, "SMBIOSBIOSVersion");
            h.BiosDate = ManagementDateTimeConverter.ToDateTime(S(bios, "ReleaseDate"));
            h.Cpu = S(Wmi("SELECT Name FROM Win32_Processor").First(), "Name");
            foreach (var g in Wmi("SELECT Name FROM Win32_VideoController")) h.Gpus.Add(S(g, "Name"));
            var main = h.Gpus.FirstOrDefault(n => n.Contains("NVIDIA")) ?? h.Gpus.FirstOrDefault() ?? "";
            long ram = Wmi("SELECT Capacity FROM Win32_PhysicalMemory").Sum(o => Convert.ToInt64(o["Capacity"]));
            string cpu = Regex.Replace(h.Cpu.Replace("AMD ", ""), @"\s+\d+-Core Processor", "").Trim();
            h.Summary = BoardName(h.Board) + "  ·  " + cpu + "  ·  " + main.Replace("NVIDIA GeForce ", "") + "  ·  " + (ram >> 30) + " Go";
            return h;
        }

        // ---------- Windows ----------
        public static List<Item> WindowsUpdate()
        {
            var res = new List<Item>();
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session"));
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic found = searcher.Search("IsInstalled=0 and IsHidden=0");
            foreach (dynamic u in found.Updates)
            {
                string title = u.Title, id = u.Identity.UpdateID, cls = "";
                bool driver = (int)u.Type == 2;
                if (driver) { try { cls = u.DriverClass ?? ""; } catch { } }
                // Windows Update propose parfois un pilote graphique plus ancien que celui du constructeur
                bool display = cls.Equals("Display", StringComparison.OrdinalIgnoreCase);
                res.Add(new Item(driver ? Sections.Drivers : Sections.Windows, title, St.Update,
                        display ? "Pilote graphique via Windows Update : décoché, préfère celui du constructeur."
                                : driver ? "Pilote proposé par Windows Update." : "Mise à jour Windows.")
                    .Install("wu", id, !display));
            }
            if (!res.Any(i => i.Section == Sections.Windows))
                res.Add(new Item(Sections.Windows, "Windows Update", St.Ok, "Aucune mise à jour Windows en attente."));

            dynamic sys = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo"));
            if ((bool)sys.RebootRequired)
                res.Add(new Item(Sections.Windows, "Redémarrage en attente", St.Tune, "Des mises à jour attendent un redémarrage pour se terminer."));
            return res;
        }

        public static List<Item> WindowsVersion()
        {
            using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                string display = k.GetValue("DisplayVersion") as string, build = k.GetValue("CurrentBuild") as string;
                var ubr = k.GetValue("UBR");
                return new List<Item> { new Item(Sections.Windows, "Version de Windows", St.Info, "Si une version plus récente existe pour ton PC, Windows Update la propose.")
                    .Versions(display + " (build " + build + "." + ubr + ")", null).Link("Windows Update", "ms-settings:windowsupdate") };
            }
        }

        // ---------- Pilotes ----------
        static string NvidiaInstalledVersion(string driverVersion)
        {
            // 32.0.16.2101 : les 5 derniers chiffres donnent 621.01
            var d = driverVersion.Replace(".", "");
            if (d.Length < 5) return driverVersion;
            d = d.Substring(d.Length - 5);
            return d.Substring(0, 3) + "." + d.Substring(3);
        }

        public static List<Item> Nvidia()
        {
            var res = new List<Item>();
            foreach (var g in Wmi("SELECT Name,DriverVersion FROM Win32_VideoController"))
            {
                string name = S(g, "Name");
                if (!name.Contains("NVIDIA")) continue;
                string installed = NvidiaInstalledVersion(S(g, "DriverVersion"));
                string shortName = Regex.Replace(name.Replace("NVIDIA ", ""), @"\s+\d+GB$", "");
                string title = "Pilote graphique " + shortName;

                var xml = new XmlDocument();
                xml.LoadXml(Util.HttpGet(NvLookup));
                string psid = null, pfid = null;
                foreach (XmlElement e in xml.GetElementsByTagName("LookupValue"))
                    if (string.Equals(e["Name"].InnerText.Trim().Replace("NVIDIA ", ""), shortName, StringComparison.OrdinalIgnoreCase))
                    { psid = e.GetAttribute("ParentID"); pfid = e["Value"].InnerText.Trim(); break; }
                if (pfid == null)
                {
                    res.Add(new Item(Sections.Drivers, title, St.Check, "Carte introuvable dans la liste NVIDIA.").Versions(installed, null)
                        .Link("Site NVIDIA", "https://www.nvidia.com/fr-fr/drivers/"));
                    continue;
                }
                int os = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57;
                var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                    .DeserializeObject(Util.HttpGet(string.Format(NvDriver, psid, pfid, os)));
                var info = (Dictionary<string, object>)((Dictionary<string, object>)((object[])root["IDS"])[0])["downloadInfo"];
                Func<string, string> field = k => Uri.UnescapeDataString((string)info[k]);
                string latest = field("Version");
                if (Util.CompareVersions(installed, latest) < 0)
                    res.Add(new Item(Sections.Drivers, title, St.Update, "Game Ready du " + field("ReleaseDateTime") + " (" + field("DownloadURLFileSize") + "). L'installateur NVIDIA s'ouvre après le téléchargement.")
                        .Versions(installed, latest).Install("nv", field("DownloadURL"), true).Link("Notes de version", field("DetailsURL")));
                else
                    res.Add(new Item(Sections.Drivers, title, St.Ok, "Dernier pilote Game Ready.").Versions(installed, latest));
            }
            return res;
        }

        public static List<Item> AmdChipset(Hardware h)
        {
            var res = new List<Item>();
            if (!h.Cpu.Contains("AMD")) return res;
            string installed = UninstallVersion("AMD Chipset Software");
            string title = "Pilote chipset AMD";
            var m = Regex.Match(h.Board, @"\b([ABX]\d{3}E?)\b");
            if (!m.Success)
            {
                res.Add(new Item(Sections.Drivers, title, St.Check, "Chipset non reconnu.").Versions(installed, null).Link("Site AMD", AmdSite));
                return res;
            }
            string chip = m.Groups[1].Value.TrimEnd('E').ToLowerInvariant();
            string socket = chip[1] == '6' || chip[1] == '8' ? "am5" : "am4";
            string page = "https://www.amd.com/en/support/downloads/drivers.html/chipsets/" + socket + "/" + chip + ".html";
            var f = Regex.Match(TryGet(page), @"amd_chipset_software_([\d.]+?)\.exe", RegexOptions.IgnoreCase);
            if (!f.Success)
            {
                res.Add(new Item(Sections.Drivers, title, St.Check, "Version introuvable sur la page AMD.").Versions(installed, null).Link("Page AMD", page));
                return res;
            }
            string latest = f.Groups[1].Value;
            if (installed == null || Util.CompareVersions(installed, latest) < 0)
                res.Add(new Item(Sections.Drivers, title, St.Update, "À télécharger sur la page AMD. Il règle aussi la gestion du processeur par Windows.").Versions(installed ?? "absent", latest).Link("Page AMD", page));
            else
                res.Add(new Item(Sections.Drivers, title, St.Ok, "Gère aussi le processeur, l'USB et le pilote 3D V-Cache.").Versions(installed, latest));
            return res;
        }

        public static List<Item> AmdGraphics(Hardware h)
        {
            var res = new List<Item>();
            if (!h.Gpus.Any(g => g.Contains("AMD") || g.Contains("Radeon"))) return res;
            string installed = UninstallVersion("AMD Software");
            string radeon = h.Gpus.FirstOrDefault(g => Regex.IsMatch(g, @"\bRX \d"));
            string title = radeon != null ? "Pilote graphique " + radeon.Replace("AMD ", "") : "AMD Software (graphique intégré du processeur)";
            string page = null;
            var rx = radeon == null ? Match.Empty : Regex.Match(radeon, @"RX (\d)(\d{3}(?: \w+)*)");
            var c = Regex.Match(h.Cpu, @"Ryzen (\d) (\d)(\d{3}\w*)");
            if (rx.Success)
                page = "https://www.amd.com/en/support/downloads/drivers.html/graphics/radeon-rx/radeon-rx-" + rx.Groups[1].Value + "000-series/amd-radeon-rx-"
                    + (rx.Groups[1].Value + rx.Groups[2].Value).ToLowerInvariant().Replace(' ', '-') + ".html";
            else if (radeon == null && c.Success)
                page = "https://www.amd.com/en/support/downloads/drivers.html/processors/ryzen/ryzen-" + c.Groups[2].Value + "000-series/amd-ryzen-"
                    + c.Groups[1].Value + "-" + (c.Groups[2].Value + c.Groups[3].Value).ToLowerInvariant() + ".html";
            string latest = null;
            if (page != null)
                foreach (Match m in Regex.Matches(TryGet(page), @"whql-amd-software-adrenalin-edition-([\d.]+?)-win11", RegexOptions.IgnoreCase))
                    if (latest == null || Util.CompareVersions(m.Groups[1].Value, latest) > 0) latest = m.Groups[1].Value;
            if (latest == null)
            {
                res.Add(new Item(Sections.Drivers, title, St.Check, "Vérification automatique impossible pour ce modèle : regarde sur le site AMD.").Versions(installed, null).Link("Site AMD", AmdSite));
                return res;
            }
            if (installed == null || Util.CompareVersions(installed, latest) < 0)
                res.Add(new Item(Sections.Drivers, title, St.Update, radeon != null ? "À télécharger sur la page AMD." : "Pour le graphique intégré, pas pour ta carte graphique : sans effet sur tes FPS.").Versions(installed ?? "absent", latest).Link("Page AMD", page));
            else
                res.Add(new Item(Sections.Drivers, title, St.Ok, radeon != null ? "Dernier pilote AMD." : "Graphique intégré du processeur.").Versions(installed, latest));
            return res;
        }

        public static List<Item> DeviceDrivers(Hardware h)
        {
            var res = new List<Item>();
            var seen = new HashSet<string>();
            foreach (var d in Wmi("SELECT DeviceName,DeviceClass,Manufacturer,DriverVersion,DriverDate FROM Win32_PnPSignedDriver"))
            {
                string name = S(d, "DeviceName"), cls = S(d, "DeviceClass"), maker = S(d, "Manufacturer"), ver = S(d, "DriverVersion"), raw = S(d, "DriverDate");
                if (name == "" || raw == "" || !seen.Add(name)) continue;
                if (cls != "NET" && cls != "BLUETOOTH" && cls != "MEDIA") continue;
                if (maker.Contains("Microsoft") || maker.StartsWith("(") || Regex.IsMatch(name, "Virtual|WAN Miniport|Xbox|NVIDIA", RegexOptions.IgnoreCase)) continue;
                DateTime date = ManagementDateTimeConverter.ToDateTime(raw);

                if (cls == "NET" && maker.Contains("Realtek") && name.Contains("2.5G")) { res.Add(RealtekLan(name, ver, date)); continue; }
                if (date.Year > 2010 && date < DateTime.Now.AddYears(-3))
                    res.Add(new Item(Sections.Drivers, name, St.Check, "Pilote du " + date.ToString("dd/MM/yyyy") + " (" + Util.Months(date) + "). Regarde si le site de ta carte mère en a un plus récent.")
                        .Versions(ver, null).Link("Pilotes carte mère", BoardPage(h)));
            }
            var bad = Wmi("SELECT Name,ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0 AND ConfigManagerErrorCode <> 22").Select(o => S(o, "Name")).ToList();
            if (bad.Count > 0)
                res.Add(new Item(Sections.Drivers, bad.Count + " périphérique(s) en erreur", St.Tune, string.Join(", ", bad.Take(4))).Link("Gestionnaire", "devmgmt.msc"));
            else
                res.Add(new Item(Sections.Drivers, "Périphériques", St.Ok, "Aucun périphérique en erreur."));
            return res;
        }

        static Item RealtekLan(string name, string installed, DateTime date)
        {
            var it = new Item(Sections.Drivers, name, St.Check, "").Versions(installed, null).Link("Page Realtek", RealtekPage);
            string page;
            try { page = Regex.Replace(Regex.Replace(Util.HttpGet(RealtekPage), "<[^>]+>", " "), @"\s+", " "); }
            catch (Exception e) { it.Detail = "Site Realtek injoignable : " + e.Message; return it; }
            bool cx = installed.StartsWith("11.");
            var m = Regex.Match(page, cx ? @"Win11 Auto Installation Program \(NetAdapterCx\) ([\d.]+)" : @"Win10/Win11 Auto Installation Program \(NDIS\) ([\d.]+)");
            if (!m.Success) { it.Detail = "Version introuvable sur la page Realtek."; return it; }
            var a = Util.Numbers(installed); var b = Util.Numbers(m.Groups[1].Value);
            bool old = a.Length >= 2 && b.Length >= 2 && (a[0] < b[0] || (a[0] == b[0] && a[1] < b[1]));
            it.Latest = m.Groups[1].Value;
            it.Status = old ? St.Update : St.Ok;
            it.Detail = old ? "Pilote réseau du " + date.ToString("dd/MM/yyyy") + ". À faire en priorité si tu as des rollbacks ou du lag en ligne."
                            : "Pilote réseau à jour.";
            return it;
        }

        // ---------- BIOS / composants ----------
        static string BoardPage(Hardware h)
        {
            if (h.BoardMaker.Contains("Micro-Star"))
                return "https://www.msi.com/Motherboard/" + BoardName(h.Board).Replace(' ', '-') + "/support";
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(h.BoardMaker + " " + h.Board + " support BIOS");
        }

        const string BiosHelp =
            "Le BIOS se met à jour à la main : un flash raté peut empêcher le PC de démarrer, donc PC à Jour ne le fait pas à ta place.\n\n" +
            "1. Sur la page du constructeur, télécharge la version la plus récente qui n'est pas marquée « Beta ».\n" +
            "2. Décompresse le fichier et copie-le sur une clé USB en FAT32.\n" +
            "3. Si BitLocker est actif, note ta clé de récupération avant de commencer.\n" +
            "4. Redémarre, entre dans le BIOS (souvent Suppr ou F2) et lance son outil de mise à jour : M-Flash chez MSI, Q-Flash chez Gigabyte, EZ Flash chez ASUS, Instant Flash chez ASRock.\n" +
            "5. Ne coupe surtout pas le courant : c'est long et le PC peut redémarrer plusieurs fois.\n\n" +
            "Après le flash, les réglages reviennent par défaut : réactive EXPO ou XMP pour ta RAM et vérifie Re-Size BAR. Relance ensuite PC à Jour pour contrôler.\n\n" +
            "Sur un PC portable ou un PC de marque, suis plutôt l'outil de mise à jour du fabricant.";

        public static List<Item> Firmware(Hardware h)
        {
            var res = new List<Item>();
            string code = Regex.Match(h.Board, @"MS-(\w+)").Groups[1].Value;
            string siteName = code != "" && h.BoardMaker.Contains("Micro-Star") ? " Sur le site, ta version s'écrit " + code + "v" + h.BiosVersion.Replace(".", "") + "." : "";
            bool old = h.BiosDate < DateTime.Now.AddMonths(-3);
            res.Add(new Item(Sections.Firmware, "BIOS " + BoardName(h.Board), old ? St.Check : St.Info,
                    "Daté du " + h.BiosDate.ToString("dd/MM/yyyy") + " (" + Util.Months(h.BiosDate) + ")." + siteName + " Compare avec la page du constructeur : PC à Jour ne peut pas le vérifier tout seul.")
                .Versions(h.BiosVersion, null).Link("Page BIOS", BoardPage(h)).WithHelp(BiosHelp));

            res.Add(new Item(Sections.Firmware, h.Cpu.Trim(), St.Info, "Un processeur n'a pas de pilote à lui : ses correctifs passent par le BIOS et le pilote chipset."));

            var sticks = Wmi("SELECT PartNumber,ConfiguredClockSpeed,SMBIOSMemoryType FROM Win32_PhysicalMemory");
            if (sticks.Count > 0)
            {
                int conf = sticks.Min(o => Convert.ToInt32(o["ConfiguredClockSpeed"]));
                string part = S(sticks[0], "PartNumber");
                int rated = Regex.Matches(part, @"(?<!\d)\d{4}(?!\d)").Cast<Match>().Select(x => int.Parse(x.Value)).Where(v => v >= 2133 && v <= 10000).DefaultIfEmpty(0).Max();
                bool ddr5 = Convert.ToInt32(sticks[0]["SMBIOSMemoryType"]) == 34;
                bool slow = rated > 0 ? conf < rated - 100 : (ddr5 && conf <= 4800);
                string label = "RAM : " + sticks.Count + " barrette(s) " + part;
                if (slow)
                    res.Add(new Item(Sections.Perf, label, St.Tune, "La RAM tourne sous sa vitesse prévue. Active EXPO ou XMP dans le BIOS.").Versions(conf + " MT/s", rated > 0 ? rated + " MT/s" : null));
                else
                    res.Add(new Item(Sections.Perf, label, St.Ok, rated > 0 ? "EXPO/XMP actif. La RAM n'a pas de pilote : seule sa vitesse compte." : "La RAM n'a pas de pilote. Sa vitesse prévue n'a pas pu être lue, compare avec sa fiche.").Versions(conf + " MT/s", null));
            }
            return res;
        }

        // ---------- Réglages ----------
        public static List<Item> Settings()
        {
            var res = new List<Item>();
            var scheme = Regex.Match(Util.Capture("powercfg", "/getactivescheme", 15000), @"[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}", RegexOptions.IgnoreCase).Value.ToLowerInvariant();
            if (scheme == "a1841308-3541-4fab-bc81-f71556f20b4a")
                res.Add(new Item(Sections.Perf, "Mode d'alimentation", St.Tune, "Le mode Économie d'énergie bride le processeur.").Link("Réglage", "ms-settings:powersleep"));
            else
                res.Add(new Item(Sections.Perf, "Mode d'alimentation", St.Ok, "Pas de bridage du processeur."));

            bool supported, enabled;
            if (GpuScheduling(out supported, out enabled) && supported)
            {
                if (enabled)
                    res.Add(new Item(Sections.Perf, "Planification GPU matérielle", St.Ok, "Activée. Elle sert notamment à la génération d'images en jeu."));
                else
                    res.Add(new Item(Sections.Perf, "Planification GPU matérielle", St.Tune, "Désactivée. Active-la si tu utilises la génération d'images en jeu.").Link("Réglage", "ms-settings:display-advancedgraphics"));
            }

            object gm = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", null);
            if (gm is int && (int)gm == 0)
                res.Add(new Item(Sections.Perf, "Mode Jeu de Windows", St.Tune, "Désactivé : Windows ne donne pas la priorité au jeu en cours.").Link("Réglage", "ms-settings:gaming-gamemode"));
            else
                res.Add(new Item(Sections.Perf, "Mode Jeu de Windows", St.Ok, "Activé."));

            try
            {
                var d = Wmi("SELECT SecurityServicesRunning FROM Win32_DeviceGuard", @"root\Microsoft\Windows\DeviceGuard").FirstOrDefault();
                var run = d == null ? null : d["SecurityServicesRunning"] as uint[];
                if (run != null && run.Contains(2u))
                    res.Add(new Item(Sections.Perf, "Intégrité de la mémoire", St.Info, "Activée. Cette protection peut coûter quelques % en jeu, mais certains anti-triches l'exigent : c'est ton choix.").Link("Réglage", "windowsdefender://coreisolation"));
            }
            catch { }
            return res;
        }

        // La clé de registre HwSchMode est souvent absente : seul le pilote connaît l'état réel.
        [StructLayout(LayoutKind.Sequential)]
        struct KmtAdapterInfo { public uint hAdapter; public uint luidLow; public int luidHigh; public uint sources; public int precise; }
        [StructLayout(LayoutKind.Sequential)]
        struct KmtEnumAdapters2 { public uint count; public IntPtr adapters; }
        [StructLayout(LayoutKind.Sequential)]
        struct KmtQuery { public uint hAdapter; public int type; public IntPtr data; public uint size; }
        [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref KmtEnumAdapters2 e);
        [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref KmtQuery q);

        static bool GpuScheduling(out bool supported, out bool enabled)
        {
            supported = enabled = false;
            var e = new KmtEnumAdapters2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.count == 0) return false;
            int size = Marshal.SizeOf(typeof(KmtAdapterInfo));
            e.adapters = Marshal.AllocHGlobal(size * (int)e.count);
            IntPtr caps = Marshal.AllocHGlobal(4);
            try
            {
                if (D3DKMTEnumAdapters2(ref e) != 0) return false;
                for (int i = 0; i < e.count; i++)
                {
                    var a = (KmtAdapterInfo)Marshal.PtrToStructure(new IntPtr(e.adapters.ToInt64() + (long)i * size), typeof(KmtAdapterInfo));
                    Marshal.WriteInt32(caps, 0);
                    var q = new KmtQuery { hAdapter = a.hAdapter, type = 70, data = caps, size = 4 };   // KMTQAITYPE_WDDM_2_7_CAPS
                    if (D3DKMTQueryAdapterInfo(ref q) != 0) continue;
                    int flags = Marshal.ReadInt32(caps);
                    if ((flags & 1) != 0) supported = true;
                    if ((flags & 2) != 0) enabled = true;
                }
                return true;
            }
            finally { Marshal.FreeHGlobal(e.adapters); Marshal.FreeHGlobal(caps); }
        }

        public static List<Item> ResizableBar()
        {
            var res = new List<Item>();
            string text;
            try { text = Util.Capture("nvidia-smi", "-q -d MEMORY", 15000); } catch { return res; }
            var m = Regex.Match(text, @"BAR1 Memory Usage\s+Total\s*:\s*(\d+)\s*MiB");
            if (!m.Success) return res;
            if (int.Parse(m.Groups[1].Value) > 256)
                res.Add(new Item(Sections.Perf, "Resizable BAR", St.Ok, "Actif : le processeur accède à toute la mémoire de la carte graphique."));
            else
                res.Add(new Item(Sections.Perf, "Resizable BAR", St.Tune, "Inactif. Active Re-Size BAR dans le BIOS : beaucoup de jeux y gagnent des FPS."));
            return res;
        }

        // ---------- Écrans ----------
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DevMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);

        public static List<Item> Displays()
        {
            var res = new List<Item>();
            int n = 0;
            for (uint i = 0; ; i++)
            {
                var dd = new DisplayDevice(); dd.cb = Marshal.SizeOf(dd);
                if (!EnumDisplayDevices(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & 1) == 0) continue;   // pas relié au bureau
                n++;
                var cur = new DevMode(); cur.dmSize = (short)Marshal.SizeOf(cur);
                if (!EnumDisplaySettings(dd.DeviceName, -1, ref cur)) continue;
                int max = cur.dmDisplayFrequency;
                for (int m = 0; ; m++)
                {
                    var dm = new DevMode(); dm.dmSize = (short)Marshal.SizeOf(dm);
                    if (!EnumDisplaySettings(dd.DeviceName, m, ref dm)) break;
                    if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmDisplayFrequency > max) max = dm.dmDisplayFrequency;
                }
                string title = "Écran " + n + " (" + cur.dmPelsWidth + "×" + cur.dmPelsHeight + ")";
                if (max > cur.dmDisplayFrequency + 1)
                    res.Add(new Item(Sections.Perf, title, St.Tune, "L'écran peut aller plus vite que ce que Windows utilise. Monte la fréquence dans les paramètres d'affichage.")
                        .Versions(cur.dmDisplayFrequency + " Hz", max + " Hz").Link("Réglage", "ms-settings:display-advanced"));
                else
                    res.Add(new Item(Sections.Perf, title, St.Ok, "Fréquence maximale utilisée.").Versions(cur.dmDisplayFrequency + " Hz", null));
            }
            return res;
        }

        // ---------- Disques ----------
        public static List<Item> Disks()
        {
            var res = new List<Item>();
            foreach (var d in Wmi("SELECT FriendlyName,FirmwareVersion,HealthStatus FROM MSFT_PhysicalDisk", @"root\Microsoft\Windows\Storage"))
            {
                string name = S(d, "FriendlyName"), fw = S(d, "FirmwareVersion");
                string tool = null, url = null;
                if (name.StartsWith("Samsung", StringComparison.OrdinalIgnoreCase)) { tool = "Samsung Magician"; url = "https://semiconductor.samsung.com/consumer-storage/support/tools/"; }
                else if (name.StartsWith("CT", StringComparison.Ordinal) || name.Contains("Crucial")) { tool = "Crucial Storage Executive"; url = "https://www.crucial.com/support/storage-executive"; }
                else if (name.StartsWith("ST", StringComparison.Ordinal) || name.Contains("Seagate")) { tool = "SeaTools"; url = "https://www.seagate.com/support/downloads/seatools/"; }

                var it = Convert.ToInt32(d["HealthStatus"]) == 0
                    ? new Item(Sections.Storage, name, St.Ok, "Santé OK." + (tool != null ? " Le firmware se vérifie avec " + tool + "." : " Pas d'outil de firmware connu pour cette marque."))
                    : new Item(Sections.Storage, name, St.Tune, "Windows signale un problème sur ce disque. Sauvegarde tes données dès maintenant.");
                it.Versions("firmware " + fw, null);
                if (tool != null) it.Link(tool, url);
                res.Add(it);
            }
            foreach (var v in System.IO.DriveInfo.GetDrives().Where(x => x.DriveType == System.IO.DriveType.Fixed && x.IsReady))
                if ((double)v.AvailableFreeSpace / v.TotalSize < 0.1)
                    res.Add(new Item(Sections.Storage, "Disque " + v.Name.TrimEnd('\\') + " presque plein", St.Tune, "Plus que " + (v.AvailableFreeSpace >> 30) + " Go libres : un SSD trop plein ralentit.").Link("Stockage", "ms-settings:storagesense"));
            return res;
        }

        // ---------- Applications ----------
        static readonly Regex WingetRow = new Regex(@"^(?<name>.+?)\s+(?<id>\S+)\s+(?<ver>(?:[<>]\s)?\S+)\s+(?<new>\S+)\s+(?<src>\S+)\s*$");

        public static List<Item> ParseWinget(string output)
        {
            var res = new List<Item>();
            bool table = false;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.TrimEnd();
                line = line.Substring(line.LastIndexOf('\r') + 1);
                if (!table) { if (Regex.IsMatch(line, @"^-{10,}")) table = true; continue; }
                if (line.Length == 0) break;
                var m = WingetRow.Match(line);
                if (!m.Success || !Regex.IsMatch(m.Groups["id"].Value, @"^[\w\-+]+(\.[\w\-+]+)+$")) break;
                string id = m.Groups["id"].Value, ver = m.Groups["ver"].Value, next = m.Groups["new"].Value;
                var a = Util.Numbers(ver); var b = Util.Numbers(next);
                bool runtime = Regex.IsMatch(id, @"NodeJS|Python|Java|JDK", RegexOptions.IgnoreCase);
                bool keepOff = runtime && a.Length > 0 && b.Length > 0 && (a[0] != b[0] || (id.Contains("Python") && a.Length > 1 && b.Length > 1 && a[1] != b[1]));
                res.Add(new Item(Sections.Apps, m.Groups["name"].Value, St.Update,
                        keepOff ? "Grosse version d'un outil de dev : décoché pour ne pas casser tes projets." : id)
                    .Versions(ver, next).Install("winget", id, !keepOff));
            }
            return res;
        }

        public static List<Item> Apps()
        {
            string text;
            try { text = Util.Capture("winget", "upgrade --accept-source-agreements --disable-interactivity", 180000); }
            catch (System.ComponentModel.Win32Exception)
            {
                return new List<Item> { new Item(Sections.Apps, "Applications", St.Check, "winget est absent : installe « Programme d'installation d'application » depuis le Microsoft Store.")
                    .Link("Microsoft Store", "ms-windows-store://pdp/?productid=9NBLGGH4NNS1") };
            }
            var res = ParseWinget(text);
            if (res.Count == 0) res.Add(new Item(Sections.Apps, "Applications", St.Ok, "Toutes les applications suivies par winget sont à jour."));
            return res;
        }
    }
}
