using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle(BloomPrintAgent.AppInfo.Name + " Setup")]
[assembly: AssemblyProduct(BloomPrintAgent.AppInfo.Name)]
[assembly: AssemblyCompany(BloomPrintAgent.AppInfo.Publisher)]
[assembly: AssemblyCopyright("(c) " + BloomPrintAgent.AppInfo.Publisher)]
[assembly: AssemblyVersion(BloomPrintAgent.AppInfo.Version + ".0")]
[assembly: AssemblyFileVersion(BloomPrintAgent.AppInfo.Version + ".0")]

namespace BloomPrintAgent.Setup
{
    /// Setup.exe [/silent] [/dir="C:\path"] [/nolaunch]      install or update
    /// Setup.exe /uninstall [/silent] [/purge]                 remove (/purge also deletes settings)
    /// The installed copy is saved as Uninstall.exe, which uninstalls when started.
    static class SetupProgram
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            bool silent = Has(args, "/silent") || Has(args, "/quiet") || Has(args, "/s");
            bool uninstall = Has(args, "/uninstall") ||
                string.Equals(Path.GetFileName(Application.ExecutablePath), Installer.UninstallerName, StringComparison.OrdinalIgnoreCase);

            if (uninstall) return Uninstall(silent, Has(args, "/purge"));

            string dir = Arg(args, "/dir=") ?? Installer.InstalledDir() ?? Installer.DefaultDir;
            if (silent)
            {
                try
                {
                    Installer.Install(new InstallOptions { Dir = dir, Desktop = true, AutoStart = true, Launch = !Has(args, "/nolaunch"), Minimized = true }, delegate { });
                    return 0;
                }
                catch { return 1; }
            }
            Application.Run(new SetupForm(dir));
            return 0;
        }

        static int Uninstall(bool silent, bool purge)
        {
            string dir = Installer.InstalledDir() ?? Path.GetDirectoryName(Application.ExecutablePath);
            if (!silent)
            {
                if (MessageBox.Show("Remove " + AppInfo.Name + " from this computer?\r\n\r\nWeb apps on this PC will fall back to the browser print dialog.",
                        AppInfo.Name + " Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                purge = MessageBox.Show("Keep its settings and the list of connected web apps, in case you install it again?",
                        AppInfo.Name + " Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No;
            }
            try { Installer.Uninstall(dir, purge); }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show("Uninstall did not finish:\r\n" + e.Message, AppInfo.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            if (!silent) MessageBox.Show(AppInfo.Name + " was removed.", AppInfo.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static bool Has(string[] args, string flag)
        {
            foreach (var a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Arg(string[] args, string prefix)
        {
            foreach (var a in args)
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length).Trim('"');
            return null;
        }
    }

    class InstallOptions
    {
        public string Dir;
        public bool Desktop;
        public bool AutoStart;
        public bool Launch;
        public bool Minimized;
    }

    static class Installer
    {
        public const string UninstallerName = "Uninstall.exe";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BloomPrintAgent";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "BloomPrintAgent"; // same value the agent's own "Start with Windows" uses
        const string KeepAliveTask = "BloomPrintAgent Keep Running"; // created by the agent (AutoStart in Program.cs)

        public static readonly string DefaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name);
        static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BloomPrintAgent");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk");
        static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Name + ".lnk");

        public static string InstalledDir() { return ReadUninstallValue("InstallLocation"); }
        public static string InstalledVersion() { return ReadUninstallValue("DisplayVersion"); }

        public static void Install(InstallOptions o, Action<string> progress)
        {
            string previous = InstalledDir();
            progress("Closing the running agent...");
            // the keep-alive task could restart the old copy mid-update; the new agent recreates it on start
            DeleteKeepAliveTask();
            StopAgent();

            progress("Copying files...");
            Directory.CreateDirectory(o.Dir);
            string exe = Path.Combine(o.Dir, AppInfo.ExeName);
            Retry(delegate
            {
                using (var src = Assembly.GetExecutingAssembly().GetManifestResourceStream(AppInfo.ExeName))
                using (var dst = File.Create(exe))
                    src.CopyTo(dst);
            });
            string uninstaller = Path.Combine(o.Dir, UninstallerName);
            if (!SamePath(Application.ExecutablePath, uninstaller))
                Retry(delegate { File.Copy(Application.ExecutablePath, uninstaller, true); });

            // moved to a new folder on update: clear out the old copy
            if (previous != null && !SamePath(previous, o.Dir)) DeleteInstalledFiles(previous, false);

            progress("Creating shortcuts...");
            CreateShortcut(StartMenuLink, exe, o.Dir);
            if (o.Desktop) CreateShortcut(DesktopLink, exe, o.Dir);
            else DeleteFile(DesktopLink);

            progress("Registering...");
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (o.AutoStart) run.SetValue(RunValue, "\"" + exe + "\" --minimized");
                else if (run.GetValue(RunValue) != null) run.DeleteValue(RunValue);
            }
            WriteAgentConfig(o.AutoStart);
            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppInfo.Name);
                key.SetValue("DisplayVersion", AppInfo.Version);
                key.SetValue("Publisher", AppInfo.Publisher);
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", o.Dir);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
                key.SetValue("EstimatedSize", (int)((new FileInfo(exe).Length + new FileInfo(uninstaller).Length) / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            if (o.Launch)
            {
                progress("Starting " + AppInfo.Name + "...");
                Process.Start(new ProcessStartInfo(exe, o.Minimized ? "--minimized" : "") { WorkingDirectory = o.Dir, UseShellExecute = true });
            }
            progress("Done.");
        }

        public static void Uninstall(string dir, bool purge)
        {
            DeleteKeepAliveTask();
            StopAgent();
            using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                if (run != null && run.GetValue(RunValue) != null) run.DeleteValue(RunValue);
            DeleteFile(StartMenuLink);
            DeleteFile(DesktopLink);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            if (purge && Directory.Exists(DataDir)) Directory.Delete(DataDir, true);
            if (dir != null) DeleteInstalledFiles(dir, true);
        }

        /// Deletes only our own files. Uninstall.exe may be the running process, so a short-lived
        /// cmd removes it (and the folder, if empty) a moment after we exit.
        static void DeleteInstalledFiles(string dir, bool selfMayBeRunning)
        {
            DeleteFile(Path.Combine(dir, AppInfo.ExeName));
            string uninstaller = Path.Combine(dir, UninstallerName);
            if (selfMayBeRunning && SamePath(Application.ExecutablePath, uninstaller))
            {
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + uninstaller + "\" & rmdir \"" + dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
                return;
            }
            DeleteFile(uninstaller);
            try { if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }
            catch { }
        }

        static void DeleteKeepAliveTask()
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/delete /f /tn \"" + KeepAliveTask + "\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
            }
        }

        /// Asks a running agent to quit (it listens for this event), then force-closes stragglers.
        static void StopAgent()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(AppInfo.ExitEventName)) ev.Set();
            }
            catch { }
            string name = Path.GetFileNameWithoutExtension(AppInfo.ExeName);
            for (int i = 0; i < 25 && Process.GetProcessesByName(name).Length > 0; i++) Thread.Sleep(200);
            foreach (var p in Process.GetProcessesByName(name))
            {
                try { p.Kill(); p.WaitForExit(3000); }
                catch { }
            }
        }

        /// Settings the agent reads at start: "start with Windows" as chosen here, and no first-run defaults.
        static void WriteAgentConfig(bool autoStart)
        {
            string path = Path.Combine(DataDir, "config.json");
            var json = new JavaScriptSerializer();
            Dictionary<string, object> cfg = null;
            try { if (File.Exists(path)) cfg = json.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch { }
            if (cfg == null)
            {
                cfg = new Dictionary<string, object>
                {
                    { "Port", 9311 }, { "RequireApproval", true }, { "Pairings", new object[0] },
                };
            }
            cfg["StartWithWindows"] = autoStart;
            cfg["FirstRun"] = false;
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(path, json.Serialize(cfg));
        }

        static void CreateShortcut(string link, string target, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                lnk.TargetPath = target;
                lnk.WorkingDirectory = workDir;
                lnk.Description = "Lets web apps print straight to this computer's printers";
                lnk.IconLocation = target + ",0";
                lnk.Save();
                Marshal.FinalReleaseComObject(lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        static string ReadUninstallValue(string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var v = key == null ? null : key.GetValue(name) as string;
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }

        static void Retry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (IOException) { if (i >= 15) throw; Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { if (i >= 15) throw; Thread.Sleep(300); }
            }
        }

        static void DeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }

    class SetupForm : Form
    {
        static readonly Color Navy = Color.FromArgb(31, 58, 104);
        static readonly Color Muted = Color.FromArgb(110, 104, 94);

        readonly TextBox dirBox = new TextBox();
        readonly CheckBox desktopBox = new CheckBox { Text = "Create a desktop shortcut", Checked = true, AutoSize = true };
        readonly CheckBox autoStartBox = new CheckBox { Text = "Start automatically when I sign in to Windows (recommended)", Checked = true, AutoSize = true };
        readonly CheckBox launchBox = new CheckBox { Text = "Open " + AppInfo.Name + " when setup finishes", Checked = true, AutoSize = true };
        readonly Label status = new Label { AutoSize = false, ForeColor = Color.FromArgb(110, 104, 94) };
        readonly Button installBtn = new Button();
        readonly Button cancelBtn = new Button { Text = "Cancel", Size = new Size(96, 32) };
        bool finished;

        public SetupForm(string dir)
        {
            string existing = Installer.InstalledVersion();
            Text = AppInfo.Name + " Setup";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 410);
            Font = new Font("Segoe UI", 9.5f);
            BackColor = Color.White;

            var header = new Panel { Dock = DockStyle.Top, Height = 78, BackColor = Navy };
            var logo = new PictureBox { Image = Icon != null ? new Icon(Icon, 48, 48).ToBitmap() : null, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(44, 44), Location = new Point(20, 17) };
            header.Controls.Add(logo);
            header.Controls.Add(new Label { Text = AppInfo.Name, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 15f), AutoSize = true, Location = new Point(74, 14) });
            header.Controls.Add(new Label { Text = "Version " + AppInfo.Version + "  \u00B7  Setup", ForeColor = Color.FromArgb(205, 220, 245), AutoSize = true, Location = new Point(76, 46) });
            Controls.Add(header);

            int y = 96;
            Controls.Add(new Label
            {
                Text = "Lets web apps (POS, front desk, bookings...) print receipts straight to this computer's printers, without a print dialog. " +
                       "Installs for the current Windows user - no administrator rights needed.",
                Location = new Point(22, y), Size = new Size(516, 44),
            });
            y += 50;
            if (existing != null)
            {
                Controls.Add(new Label
                {
                    Text = "Version " + existing + " is installed. Setup will update it and keep your printer settings and connected apps.",
                    ForeColor = Color.FromArgb(47, 143, 114), Location = new Point(22, y), Size = new Size(516, 36),
                });
                y += 40;
            }

            Controls.Add(new Label { Text = "Install to", Font = new Font("Segoe UI Semibold", 9.5f), Location = new Point(22, y), AutoSize = true });
            y += 24;
            dirBox.Text = dir;
            dirBox.Location = new Point(24, y);
            dirBox.Width = 410;
            Controls.Add(dirBox);
            var browse = new Button { Text = "Browse...", Location = new Point(442, y - 2), Size = new Size(96, 28) };
            browse.Click += delegate { Browse(); };
            Controls.Add(browse);
            y += 42;

            foreach (var cb in new[] { desktopBox, autoStartBox, launchBox })
            {
                cb.Location = new Point(22, y);
                Controls.Add(cb);
                y += 28;
            }

            status.Location = new Point(22, ClientSize.Height - 46);
            status.Size = new Size(300, 24);
            Controls.Add(status);

            installBtn.Text = existing != null ? "Update" : "Install";
            installBtn.Size = new Size(110, 32);
            installBtn.Location = new Point(ClientSize.Width - 230, ClientSize.Height - 50);
            installBtn.Click += delegate { if (finished) Close(); else RunInstall(); };
            Controls.Add(installBtn);
            cancelBtn.Location = new Point(ClientSize.Width - 114, ClientSize.Height - 50);
            cancelBtn.Click += delegate { Close(); };
            Controls.Add(cancelBtn);
            AcceptButton = installBtn;
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Choose where to install " + AppInfo.Name, SelectedPath = Path.GetDirectoryName(dirBox.Text.TrimEnd('\\')) ?? "" })
                if (dlg.ShowDialog(this) == DialogResult.OK) dirBox.Text = Path.Combine(dlg.SelectedPath, AppInfo.Name);
        }

        void RunInstall()
        {
            string dir = dirBox.Text.Trim();
            try { dir = Path.GetFullPath(dir); }
            catch { MessageBox.Show(this, "That install folder isn't a valid path.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            var o = new InstallOptions { Dir = dir, Desktop = desktopBox.Checked, AutoStart = autoStartBox.Checked, Launch = launchBox.Checked };
            foreach (Control c in Controls) if (c is CheckBox || c is TextBox || c is Button) c.Enabled = false;
            UseWaitCursor = true;

            var t = new Thread(delegate()
            {
                Exception error = null;
                try { Installer.Install(o, delegate(string s) { BeginInvoke(new Action(delegate { status.Text = s; })); }); }
                catch (Exception e) { error = e; }
                BeginInvoke(new Action(delegate { Done(error); }));
            });
            t.IsBackground = true;
            t.Start();
        }

        void Done(Exception error)
        {
            UseWaitCursor = false;
            if (error != null)
            {
                status.Text = "Setup did not finish.";
                MessageBox.Show(this, "Setup did not finish:\r\n" + error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                foreach (Control c in Controls) c.Enabled = true;
                return;
            }
            finished = true;
            status.ForeColor = Color.FromArgb(47, 143, 114);
            status.Text = "\u2714  " + AppInfo.Name + " is installed. It runs from the system tray.";
            status.Width = 320;
            installBtn.Text = "Finish";
            installBtn.Enabled = true;
            cancelBtn.Visible = false;
        }
    }
}
