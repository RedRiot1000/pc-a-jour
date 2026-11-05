namespace PCaJour
{
    public enum St { Ok, Update, Check, Tune, Info, Error }

    public class Item
    {
        public string Section, Title, Detail, Installed, Latest;
        public St Status;
        public string Kind, Payload;      // Kind : wu / winget / nv = installable depuis l'appli
        public bool Selected;
        public string LinkText, LinkUrl;  // bouton qui ouvre une page ou un réglage
        public string Help;               // bouton « Comment faire »

        public bool Installable { get { return Kind != null; } }
        public string StatusName { get { return Status.ToString(); } }

        public Item(string section, string title, St status, string detail)
        {
            Section = section; Title = title; Status = status; Detail = detail;
        }
        public Item Versions(string installed, string latest) { Installed = installed; Latest = latest; return this; }
        public Item Link(string text, string url) { LinkText = text; LinkUrl = url; return this; }
        public Item Install(string kind, string payload, bool selected) { Kind = kind; Payload = payload; Selected = selected; return this; }
        public Item WithHelp(string help) { Help = help; return this; }
    }

    public static class Sections
    {
        public const string Windows = "Windows";
        public const string Drivers = "Pilotes";
        public const string Firmware = "BIOS et composants";
        public const string Perf = "Réglages de performance";
        public const string Storage = "Disques";
        public const string Apps = "Applications";
        public static readonly string[] Order = { Windows, Drivers, Firmware, Perf, Storage, Apps };
    }
}
