using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace PCaJour
{
    public static class Util
    {
        public const string UserAgent = "PCaJour/1.0";

        public static string HttpGet(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = UserAgent;
            req.Timeout = 25000;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var resp = req.GetResponse())
            using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return r.ReadToEnd();
        }

        /// Lance un programme et renvoie sa sortie standard.
        public static string Capture(string file, string args, int timeoutMs)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                p.BeginErrorReadLine();
                var read = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException(file + " ne répond pas");
                }
                return read.Result;
            }
        }

        public static long[] Numbers(string v)
        {
            return Regex.Matches(v ?? "", @"\d+").Cast<Match>().Select(m => { long n; long.TryParse(m.Value, out n); return n; }).ToArray();
        }

        /// Compare deux versions nombre par nombre : négatif si a est plus ancienne que b.
        public static int CompareVersions(string a, string b)
        {
            var x = Numbers(a); var y = Numbers(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                long p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p < q ? -1 : 1;
            }
            return 0;
        }

        public static string Months(DateTime since)
        {
            int m = (DateTime.Now.Year - since.Year) * 12 + DateTime.Now.Month - since.Month;
            if (m < 1) return "ce mois-ci";
            if (m < 24) return "il y a " + m + " mois";
            return "il y a " + (m / 12) + " ans";
        }
    }

    /// Vérifie la signature numérique d'un fichier avant de l'exécuter.
    public static class Trust
    {
        [StructLayout(LayoutKind.Sequential)]
        struct FileData { public uint cbStruct; public IntPtr path; public IntPtr hFile; public IntPtr knownSubject; }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public uint cbStruct; public IntPtr policy; public IntPtr sip;
            public uint uiChoice; public uint revocation; public uint unionChoice;
            public IntPtr file; public uint stateAction; public IntPtr state; public IntPtr url;
            public uint provFlags; public uint uiContext; public IntPtr sigSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        static readonly Guid GenericVerify = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        public static bool IsValid(string path)
        {
            IntPtr pathPtr = Marshal.StringToHGlobalUni(path), filePtr = IntPtr.Zero, dataPtr = IntPtr.Zero;
            try
            {
                var fi = new FileData { cbStruct = (uint)Marshal.SizeOf(typeof(FileData)), path = pathPtr };
                filePtr = Marshal.AllocHGlobal(Marshal.SizeOf(fi));
                Marshal.StructureToPtr(fi, filePtr, false);
                var td = new TrustData { cbStruct = (uint)Marshal.SizeOf(typeof(TrustData)), uiChoice = 2, unionChoice = 1, file = filePtr };
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf(td));
                Marshal.StructureToPtr(td, dataPtr, false);
                return WinVerifyTrust(new IntPtr(-1), GenericVerify, dataPtr) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
                if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
                if (dataPtr != IntPtr.Zero) Marshal.FreeHGlobal(dataPtr);
            }
        }

        /// Vrai seulement si la signature est valide ET émise pour cet éditeur.
        public static bool IsSignedBy(string path, string publisher)
        {
            if (!IsValid(path)) return false;
            try
            {
                var subject = X509Certificate.CreateFromSignedFile(path).Subject;
                return Regex.IsMatch(subject, @"(^|,\s*)O=""?" + Regex.Escape(publisher) + @"""?\s*(,|$)", RegexOptions.IgnoreCase);
            }
            catch { return false; }
        }
    }
}
