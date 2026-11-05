using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PCaJour
{
    public static class Ui
    {
        public static Brush Res(string key) { return (Brush)Application.Current.Resources[key]; }
        public static SolidColorBrush Color(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }
        public static TextBlock Text(string text, double size, string brush, FontWeight? weight = null)
        {
            var t = new TextBlock { Text = text, FontSize = size, Foreground = Res(brush), TextWrapping = TextWrapping.Wrap };
            if (weight != null) t.FontWeight = weight.Value;
            return t;
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        public static void DarkTitleBar(Window w)
        {
            w.SourceInitialized += (s, e) => { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, 4); };
        }
    }

    public class MainWindow : Window
    {
        readonly StackPanel list = new StackPanel { Margin = new Thickness(28, 4, 22, 28) };
        readonly TextBlock summary, machine, status;
        readonly StackPanel counters = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        readonly ProgressBar bar = new ProgressBar { Height = 3, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Maximum = 1 };
        readonly Button scanBtn, installBtn, restartBtn;
        public bool NoUpdate;
        readonly List<Item> items = new List<Item>();
        Hardware hw;
        bool scanning;

        public event Action ScanFinished;

        public MainWindow()
        {
            Title = "PC à Jour " + AppInfo.Version;
            Width = 1120; Height = 840; MinWidth = 900; MinHeight = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Res("Bg");
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Ui.DarkTitleBar(this);
            bar.Foreground = Ui.Res("Accent");

            var head = new Grid { Margin = new Thickness(28, 22, 28, 12) };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            summary = Ui.Text("Analyse en cours…", 26, "Text", FontWeights.SemiBold);
            machine = Ui.Text("", 13, "SubText");
            machine.Margin = new Thickness(0, 4, 0, 0);
            left.Children.Add(summary); left.Children.Add(machine); left.Children.Add(counters);
            head.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            scanBtn = new Button { Content = "Relancer l'analyse", Style = (Style)Application.Current.Resources["Ghost"], Padding = new Thickness(16, 11, 16, 11), FontSize = 13.5 };
            installBtn = new Button { Content = "Installer la sélection", Style = (Style)Application.Current.Resources["Primary"], Margin = new Thickness(10, 0, 0, 0), IsEnabled = false };
            scanBtn.Click += (s, e) => StartScan();
            installBtn.Click += (s, e) => InstallSelected();
            restartBtn = new Button { Style = (Style)Application.Current.Resources["Ghost"], Padding = new Thickness(16, 11, 16, 11), FontSize = 13.5, Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed };
            restartBtn.Click += (s, e) => { Process.Start(Updater.ExePath); Close(); };
            right.Children.Add(restartBtn); right.Children.Add(scanBtn); right.Children.Add(installBtn);
            Grid.SetColumn(right, 1);
            head.Children.Add(right);

            status = Ui.Text("", 12, "Faint");
            status.Margin = new Thickness(28, 0, 28, 8);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.Children.Add(head);
            Grid.SetRow(status, 1); root.Children.Add(status);
            Grid.SetRow(bar, 2); root.Children.Add(bar);
            var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 3); root.Children.Add(scroll);
            Content = root;

            ContentRendered += (s, e) => { StartScan(); CheckForUpdate(); };
        }

        async void CheckForUpdate()
        {
            if (NoUpdate) return;
            Updater.CleanupOld();
            var version = await Updater.CheckAndInstallAsync();
            if (version == null) return;
            restartBtn.Content = "Relancer pour passer en " + version;
            restartBtn.Visibility = Visibility.Visible;
        }

        public async void StartScan()
        {
            if (scanning) return;
            scanning = true;
            items.Clear();
            scanBtn.IsEnabled = false; installBtn.IsEnabled = false;
            summary.Text = "Analyse en cours…";
            status.Text = "";
            bar.Value = 0;
            Render();
            try
            {
                if (hw == null) hw = await Task.Run(() => Checks.ReadHardware());
                machine.Text = hw.Summary;
                await Scanner.Run(hw, (found, done, total) => Dispatcher.Invoke(() =>
                {
                    items.AddRange(found);
                    bar.Value = (double)done / total;
                    status.Text = "Vérification " + done + " sur " + total + "…";
                    Render();
                }));
            }
            catch (Exception e) { status.Text = "Erreur : " + e.Message; }
            scanning = false;
            scanBtn.IsEnabled = true;
            bar.Value = 0;
            status.Text = "Analyse terminée à " + DateTime.Now.ToString("HH:mm") + ". Rien n'est installé sans ton clic.";
            Render();
            if (ScanFinished != null) ScanFinished();
        }

        int Count(St s) { return items.Count(i => i.Status == s); }

        void Render()
        {
            list.Children.Clear();
            foreach (var section in Sections.Order)
            {
                var rows = items.Where(i => i.Section == section)
                    .OrderBy(i => Rank(i.Status)).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
                if (rows.Count == 0) continue;
                var h = Ui.Text(section, 15, "Text", FontWeights.SemiBold);
                h.Margin = new Thickness(2, 16, 0, 8);
                list.Children.Add(h);
                var card = new Border { Background = Ui.Res("Card"), CornerRadius = new CornerRadius(12) };
                var inner = new StackPanel();
                for (int i = 0; i < rows.Count; i++) inner.Children.Add(Row(rows[i], i > 0));
                card.Child = inner;
                list.Children.Add(card);
            }
            RenderHeader();
        }

        static int Rank(St s)
        {
            switch (s)
            {
                case St.Error: return 0;
                case St.Update: return 1;
                case St.Tune: return 2;
                case St.Check: return 3;
                case St.Info: return 4;
                default: return 5;
            }
        }

        void RenderHeader()
        {
            int upd = Count(St.Update), tune = Count(St.Tune), chk = Count(St.Check), ok = Count(St.Ok), err = Count(St.Error);
            counters.Children.Clear();
            counters.Children.Add(Counter(upd, "mises à jour", St.Update));
            counters.Children.Add(Counter(tune, "réglages à corriger", St.Tune));
            counters.Children.Add(Counter(chk, "à vérifier", St.Check));
            counters.Children.Add(Counter(ok, "à jour", St.Ok));
            if (err > 0) counters.Children.Add(Counter(err, "non vérifiés", St.Error));
            if (!scanning)
                summary.Text = upd + tune + chk + err == 0 ? "Ton PC est à jour" : upd + tune + chk + " point" + (upd + tune + chk > 1 ? "s" : "") + " à traiter";
            UpdateInstallButton();
        }

        UIElement Counter(int n, string label, St s)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 20, 0) };
            var num = new TextBlock { Text = n.ToString(), FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = StatusBrush(s) };
            var lab = Ui.Text(label, 13, "SubText");
            lab.Margin = new Thickness(6, 0, 0, 0); lab.VerticalAlignment = VerticalAlignment.Center;
            p.Children.Add(num); p.Children.Add(lab);
            return p;
        }

        void UpdateInstallButton()
        {
            int n = items.Count(i => i.Installable && i.Selected);
            installBtn.Content = n > 0 ? "Installer la sélection (" + n + ")" : "Installer la sélection";
            installBtn.IsEnabled = n > 0 && !scanning;
        }

        static SolidColorBrush StatusBrush(St s)
        {
            switch (s)
            {
                case St.Ok: return Ui.Color("#34D399");
                case St.Update: return Ui.Color("#38BDF8");
                case St.Check: return Ui.Color("#FBBF24");
                case St.Tune: return Ui.Color("#FB923C");
                case St.Error: return Ui.Color("#F87171");
                default: return Ui.Color("#9CA3B4");
            }
        }

        static string StatusText(St s)
        {
            switch (s)
            {
                case St.Ok: return "À jour";
                case St.Update: return "Mise à jour";
                case St.Check: return "À vérifier";
                case St.Tune: return "À corriger";
                case St.Error: return "Non vérifié";
                default: return "Info";
            }
        }

        UIElement Row(Item it, bool separator)
        {
            var g = new Grid { Margin = new Thickness(16, 11, 16, 11) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (it.Installable)
            {
                var c = new CheckBox { IsChecked = it.Selected, VerticalAlignment = VerticalAlignment.Center };
                c.Click += (s, e) => { it.Selected = c.IsChecked == true; UpdateInstallButton(); };
                g.Children.Add(c);
            }

            var badge = new TextBlock { Text = StatusText(it.Status), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = StatusBrush(it.Status), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(badge, 1); g.Children.Add(badge);

            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            mid.Children.Add(Ui.Text(it.Title, 13.5, "Text", FontWeights.Medium));
            if (!string.IsNullOrEmpty(it.Detail))
            {
                var d = Ui.Text(it.Detail, 12, "SubText");
                d.Margin = new Thickness(0, 2, 0, 0);
                mid.Children.Add(d);
            }
            Grid.SetColumn(mid, 2); g.Children.Add(mid);

            if (it.Installed != null || it.Latest != null)
            {
                string v = (it.Installed ?? "?") + (it.Latest != null && it.Latest != it.Installed ? "  →  " + it.Latest : "");
                var ver = Ui.Text(v, 12, "SubText");
                ver.VerticalAlignment = VerticalAlignment.Center;
                ver.TextAlignment = TextAlignment.Right;
                ver.Margin = new Thickness(0, 0, 12, 0);
                Grid.SetColumn(ver, 3); g.Children.Add(ver);
            }

            var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (it.Help != null) acts.Children.Add(Action("Comment faire", () => MessageBox.Show(this, it.Help, it.Title, MessageBoxButton.OK, MessageBoxImage.Information)));
            if (it.LinkUrl != null) acts.Children.Add(Action(it.LinkText, () => Open(it.LinkUrl)));
            Grid.SetColumn(acts, 4); g.Children.Add(acts);

            return new Border { Child = g, BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(0, separator ? 1 : 0, 0, 0) };
        }

        Button Action(string text, Action click)
        {
            var b = new Button { Content = text, Style = (Style)Application.Current.Resources["Ghost"], Margin = new Thickness(6, 0, 0, 0) };
            b.Click += (s, e) => click();
            return b;
        }

        void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, "Impossible d'ouvrir : " + target + "\n\n" + e.Message, "PC à Jour"); }
        }

        async void InstallSelected()
        {
            var chosen = items.Where(i => i.Installable && i.Selected).ToList();
            if (chosen.Count == 0) return;
            string text = chosen.Count + " mise(s) à jour vont être installées.\n\n"
                + "Windows va demander l'autorisation administrateur. Ferme les applis concernées, sinon elles peuvent échouer. "
                + "En validant, tu acceptes aussi les conditions de licence des logiciels mis à jour.";
            if (chosen.Any(i => i.Kind != "winget")) text += "\n\nUn point de restauration sera créé avant les pilotes et les mises à jour Windows.";
            if (MessageBox.Show(this, text, "PC à Jour", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;

            var args = "--do " + string.Join(" ", chosen.Select(i => "\"" + i.Kind + "=" + i.Payload + "\""));
            var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, args) { UseShellExecute = true, Verb = "runas" };
            Process p;
            try { p = Process.Start(psi); }
            catch (Win32Exception) { return; }   // UAC refusée
            installBtn.IsEnabled = false; scanBtn.IsEnabled = false;
            status.Text = "Installation en cours dans l'autre fenêtre…";
            await Task.Run(() => p.WaitForExit());
            StartScan();
        }

        /// Capture toute la liste (pas seulement la partie visible) pour vérifier le rendu.
        public void Snapshot(string path)
        {
            var root = (FrameworkElement)Content;
            int w = (int)root.ActualWidth, top = (int)list.TranslatePoint(new Point(0, 0), root).Y, h = top + (int)list.ActualHeight;
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(Ui.Res("Bg"), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, root.ActualHeight));
                dc.DrawRectangle(Ui.Res("Bg"), null, new Rect(0, top, w, h - top));
                dc.DrawRectangle(new VisualBrush(list) { Stretch = Stretch.None }, null, new Rect(list.Margin.Left, top, list.ActualWidth, list.ActualHeight));
            }
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using (var fs = System.IO.File.Create(path)) enc.Save(fs);
        }
    }

    /// Fenêtre du processus administrateur : déroule les installations et affiche le journal.
    public class RunWindow : Window
    {
        readonly TextBox box;
        readonly ProgressBar bar = new ProgressBar { Height = 4, BorderThickness = new Thickness(0), Background = Brushes.Transparent, IsIndeterminate = true };
        readonly TextBlock head;

        public RunWindow(List<string> jobs, bool dryRun, bool autoClose)
        {
            Title = "PC à Jour : installation";
            Width = 760; Height = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Res("Bg");
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Ui.DarkTitleBar(this);
            bar.Foreground = Ui.Res("Accent"); bar.Maximum = 1;

            head = Ui.Text("Installation en cours…", 20, "Text", FontWeights.SemiBold);
            head.Margin = new Thickness(22, 18, 22, 12);
            box = new TextBox
            {
                IsReadOnly = true, Background = Ui.Res("Card"), Foreground = Ui.Res("Text"), BorderThickness = new Thickness(0),
                FontFamily = new FontFamily("Consolas"), FontSize = 12.5, Padding = new Thickness(12), Margin = new Thickness(22, 10, 22, 22),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap
            };
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.Children.Add(head);
            Grid.SetRow(bar, 1); root.Children.Add(bar);
            Grid.SetRow(box, 2); root.Children.Add(box);
            Content = root;

            var ins = new Installer(Log, v => Dispatcher.BeginInvoke(new Action(() => { bar.IsIndeterminate = false; bar.Value = v; })), dryRun);
            ContentRendered += async (s, e) =>
            {
                await Task.Run(() => ins.Run(jobs));
                bar.IsIndeterminate = false; bar.Value = 0;
                head.Text = ins.Failures == 0 ? "Terminé" : "Terminé avec " + ins.Failures + " échec(s)";
                if (autoClose) Close();
            };
        }

        void Log(string line)
        {
            Dispatcher.BeginInvoke(new Action(() => { box.AppendText(line + "\n"); box.ScrollToEnd(); }));
            if (Program.LogFile != null) System.IO.File.AppendAllText(Program.LogFile, line + "\n");
        }
    }
}
