using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace BloomPrintAgent
{
    static class Program
    {
        public const string Name = AppInfo.Name;
        public const string Version = AppInfo.Version;

        const string MutexName = "Local\\BloomPrintAgent.SingleInstance";

        [STAThread]
        static int Main(string[] args)
        {
            // build helper: BloomPrintAgent.exe --write-icon <path.ico>
            if (args.Length == 2 && args[0] == "--write-icon")
            {
                Icons.WriteIco(args[1]);
                return 0;
            }

            // --keepalive: started by the scheduled task (at sign-in and every 2 minutes) - stay silent
            bool keepAlive = Array.IndexOf(args, "--keepalive") >= 0;
            bool minimized = keepAlive || Array.IndexOf(args, "--minimized") >= 0;

            bool created;
            using (var mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    if (keepAlive) return 0; // already running: nothing to do
                    // already running: ask that instance to show its window
                    try
                    {
                        using (var ev = EventWaitHandle.OpenExisting(AppInfo.ShowEventName)) ev.Set();
                    }
                    catch { }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Log.Error("UI error: " + e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Log.Error("Fatal: " + e.ExceptionObject); };

                Config.Load();
                // the person chose Exit in the tray: the keep-alive task must not bring it back
                // until they start it themselves or sign in again
                if (keepAlive && AutoStart.StoppedByUser) return 0;
                AutoStart.StoppedByUser = false;

                if (Config.Current.FirstRun)
                {
                    Config.Current.FirstRun = false;
                    Config.Save();
                    AutoStart.Set(Config.Current.StartWithWindows);
                }
                else AutoStart.EnsureInBackground();

                var app = new AgentContext(!minimized);
                var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ShowEventName);
                var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AppInfo.ExitEventName);
                // another instance asks us to show the window; the installer asks us to quit before upgrading
                var watcher = new Thread(delegate()
                {
                    var events = new WaitHandle[] { showEvent, exitEvent };
                    while (true)
                    {
                        if (WaitHandle.WaitAny(events) == 0) app.ShowWindowFromAnyThread();
                        else { app.ExitFromAnyThread(); return; }
                    }
                });
                watcher.IsBackground = true;
                watcher.Start();

                Application.Run(app);
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    /// Tray icon + hidden main window + local HTTP server.
    class AgentContext : ApplicationContext
    {
        readonly NotifyIcon tray;
        readonly MainForm form;
        readonly HttpServer server;

        public AgentContext(bool showWindow)
        {
            form = new MainForm(this);
            var h = form.Handle; // create the handle now so other threads can Invoke on it

            server = new HttpServer(Api.Handle);
            Api.ApprovePairing = form.AskPairing;
            Api.Changed += form.RefreshAppsFromAnyThread;

            tray = new NotifyIcon();
            tray.Text = Program.Name;
            tray.Icon = Icons.Tray();
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open " + Program.Name, null, delegate { ShowWindow(); });
            menu.Items.Add("Print test page (default printer)", null, delegate { form.TestPrintDefault(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitByUser(); });
            tray.ContextMenuStrip = menu;

            StartServer();
            if (showWindow) ShowWindow();
        }

        public bool Running { get { return server.Running; } }
        public string Address { get { return "http://127.0.0.1:" + Config.Current.Port; } }

        public void StartServer()
        {
            server.Stop();
            try
            {
                server.Start(Config.Current.Port);
                Log.Info("Listening on " + Address);
                tray.Text = Program.Name + " - running on port " + Config.Current.Port;
            }
            catch (Exception e)
            {
                Log.Error("Could not listen on port " + Config.Current.Port + ": " + e.Message);
                tray.Text = Program.Name + " - port " + Config.Current.Port + " busy";
                tray.ShowBalloonTip(5000, Program.Name, "Port " + Config.Current.Port + " is already in use. Change the port in Settings.", ToolTipIcon.Error);
            }
            form.RefreshStatus();
        }

        public void ShowWindow()
        {
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            form.BringToFront();
        }

        public void ShowWindowFromAnyThread()
        {
            form.BeginInvoke(new Action(ShowWindow));
        }

        void ExitByUser()
        {
            if (MessageBox.Show("Stop " + Program.Name + "?\r\n\r\nWeb apps on this computer can't print until it starts again (from the Start menu, or automatically when the computer restarts).",
                    Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            AutoStart.StoppedByUser = true;
            Log.Info("Stopped by the user");
            ExitAgent();
        }

        public void ExitFromAnyThread()
        {
            form.BeginInvoke(new Action(ExitAgent));
        }

        public void Notify(string text)
        {
            tray.ShowBalloonTip(3000, Program.Name, text, ToolTipIcon.Info);
        }

        public void ExitAgent()
        {
            server.Stop();
            tray.Visible = false;
            tray.Dispose();
            form.AllowClose = true;
            form.Close();
            ExitThread();
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        public static event Action<string> Line;

        public static void Info(string msg) { Write("INFO ", msg); }
        public static void Error(string msg) { Write("ERROR", msg); }

        static void Write(string level, string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + level + "  " + msg;
            lock (gate)
            {
                try
                {
                    string path = Path.Combine(Config.Dir, "agent.log");
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(path);
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch { }
            }
            var h = Line;
            if (h != null) h(line);
        }
    }

    /// Keeps the agent running across restarts, two ways (either one is enough):
    ///  - HKCU Run entry: starts it at sign-in.
    ///  - Scheduled task: starts it at sign-in and re-checks every 2 minutes, so a crash or an
    ///    accidental kill heals itself, and it still starts if the Run entry gets disabled in
    ///    Task Manager's Startup tab. Neither needs admin rights.
    static class AutoStart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "BloomPrintAgent";
        public const string TaskName = "BloomPrintAgent Keep Running";
        static readonly string StopMarker = Path.Combine(Config.Dir, "stopped-by-user");

        public static void Set(bool on)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\" --minimized");
                    else if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName);
                }
            }
            catch (Exception e) { Log.Error("Auto-start (Run entry): " + e.Message); }
            if (on) CreateTask(Application.ExecutablePath);
            else DeleteTask();
        }

        public static bool IsOn()
        {
            return RunTarget() != null || TaskExists();
        }

        /// Repairs missing pieces without stealing auto-start from another copy of the exe
        /// (e.g. a portable copy run for testing next to the installed one).
        public static void EnsureInBackground()
        {
            if (!Config.Current.StartWithWindows) return;
            var t = new Thread(delegate()
            {
                try
                {
                    string target = RunTarget();
                    if (target == null || !File.Exists(target)) { Set(true); return; }
                    if (!TaskExists()) CreateTask(target);
                }
                catch (Exception e) { Log.Error("Auto-start check: " + e.Message); }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// Set when the person exits from the tray; cleared by any normal start. Only counts until
        /// the next restart, so a reboot always brings the agent back.
        public static bool StoppedByUser
        {
            get
            {
                try
                {
                    if (!File.Exists(StopMarker)) return false;
                    long boot = long.Parse(File.ReadAllText(StopMarker).Trim());
                    return Math.Abs(boot - BootTime().Ticks) < TimeSpan.FromMinutes(2).Ticks;
                }
                catch { return false; }
            }
            set
            {
                try
                {
                    if (value) File.WriteAllText(StopMarker, BootTime().Ticks.ToString());
                    else if (File.Exists(StopMarker)) File.Delete(StopMarker);
                }
                catch { }
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern ulong GetTickCount64();

        static DateTime BootTime() { return DateTime.UtcNow - TimeSpan.FromMilliseconds(GetTickCount64()); }

        static string RunTarget()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    var v = key == null ? null : key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(v)) return null;
                    if (v.StartsWith("\"")) { int end = v.IndexOf('"', 1); return end > 0 ? v.Substring(1, end - 1) : null; }
                    int space = v.IndexOf(' ');
                    return space > 0 ? v.Substring(0, space) : v;
                }
            }
            catch { return null; }
        }

        static bool TaskExists() { return Schtasks("/query /tn \"" + TaskName + "\"") == 0; }

        static void DeleteTask()
        {
            if (TaskExists()) Schtasks("/delete /f /tn \"" + TaskName + "\"");
        }

        static void CreateTask(string exe)
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <RegistrationInfo><Description>Keeps " + Program.Name + " running so web apps on this computer can print.</Description></RegistrationInfo>\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger><Enabled>true</Enabled><UserId>" + Escape(user) + "</UserId></LogonTrigger>\r\n" +
                "    <TimeTrigger><Enabled>true</Enabled><StartBoundary>2020-01-01T00:00:00</StartBoundary>\r\n" +
                "      <Repetition><Interval>PT2M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition></TimeTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Escape(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" + // never kill the agent for running "too long"
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\"><Exec><Command>\"" + Escape(exe) + "\"</Command><Arguments>--keepalive</Arguments></Exec></Actions>\r\n" +
                "</Task>\r\n";
            string file = Path.Combine(Path.GetTempPath(), "BloomPrintAgent-task.xml");
            try
            {
                File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
                int code = Schtasks("/create /f /tn \"" + TaskName + "\" /xml \"" + file + "\"");
                if (code != 0) Log.Error("Auto-start: could not create the keep-alive task (schtasks exit " + code + ")");
            }
            finally { try { File.Delete(file); } catch { } }
        }

        static string Escape(string s) { return System.Security.SecurityElement.Escape(s); }

        static int Schtasks(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit(15000);
                return p.ExitCode;
            }
        }
    }
}
