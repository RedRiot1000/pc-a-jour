using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCaJour
{
    public static class Scanner
    {
        class Check
        {
            public string Section, Name; public Func<List<Item>> Run;
            public Check(string section, string name, Func<List<Item>> run) { Section = section; Name = name; Run = run; }
        }

        /// Lance toutes les vérifications en parallèle ; onResult est appelé à chaque résultat.
        public static Task Run(Hardware h, Action<List<Item>, int, int> onResult)
        {
            var checks = new List<Check>
            {
                new Check(Sections.Windows, "Windows Update", Checks.WindowsUpdate),
                new Check(Sections.Windows, "Version de Windows", Checks.WindowsVersion),
                new Check(Sections.Drivers, "Pilote NVIDIA", Checks.Nvidia),
                new Check(Sections.Drivers, "Pilote chipset AMD", () => Checks.AmdChipset(h)),
                new Check(Sections.Drivers, "AMD Software", () => Checks.AmdGraphics(h)),
                new Check(Sections.Drivers, "Pilotes des périphériques", () => Checks.DeviceDrivers(h)),
                new Check(Sections.Firmware, "BIOS et composants", () => Checks.Firmware(h)),
                new Check(Sections.Perf, "Réglages Windows", Checks.Settings),
                new Check(Sections.Perf, "Resizable BAR", Checks.ResizableBar),
                new Check(Sections.Perf, "Écrans", Checks.Displays),
                new Check(Sections.Storage, "Disques", Checks.Disks),
                new Check(Sections.Apps, "Applications", Checks.Apps),
            };

            int done = 0, total = checks.Count;
            return Task.WhenAll(checks.Select(c => Task.Factory.StartNew(() =>
            {
                List<Item> items;
                try { items = c.Run(); }
                catch (Exception e) { items = new List<Item> { new Item(c.Section, c.Name, St.Error, "Vérification impossible : " + e.Message) }; }
                onResult(items, Interlocked.Increment(ref done), total);
            }, TaskCreationOptions.LongRunning)));
        }
    }
}
