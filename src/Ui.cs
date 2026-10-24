using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace BloomPrintAgent
{
    class MainForm : Form
    {
        static readonly Color Navy = Color.FromArgb(31, 58, 104);
        static readonly Color Muted = Color.FromArgb(110, 104, 94);

        readonly AgentContext app;
        public bool AllowClose;
        bool hintShown;

        readonly Label statusLabel = new Label();
        readonly ListView printerList = new ListView();
        readonly ListView appList = new ListView();
        readonly TextBox logBox = new TextBox();
        readonly NumericUpDown portBox = new NumericUpDown();
        readonly CheckBox autoStartBox = new CheckBox();
        readonly CheckBox approvalBox = new CheckBox();

        public MainForm(AgentContext app)
        {
            this.app = app;
            Text = Program.Name;
            Icon = Icons.App();
            ClientSize = new Size(820, 540);
            MinimumSize = new Size(640, 420);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            ShowInTaskbar = true;

            var header = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Navy, Padding = new Padding(18, 10, 18, 8) };
            var title = new Label { Text = Program.Name, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 14f), AutoSize = true, Location = new Point(18, 8) };
            statusLabel.ForeColor = Color.FromArgb(205, 220, 245);
            statusLabel.AutoSize = true;
            statusLabel.Location = new Point(20, 38);
            header.Controls.Add(title);
            header.Controls.Add(statusLabel);

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
            tabs.TabPages.Add(PrintersTab());
            tabs.TabPages.Add(AppsTab());
            tabs.TabPages.Add(SettingsTab());
            tabs.TabPages.Add(LogTab());

            Controls.Add(tabs);
            Controls.Add(header);

            Log.Line += AppendLogFromAnyThread;
            Shown += delegate { RefreshPrinters(); RefreshApps(); };
        }

        // ---------- tabs ----------

        TabPage PrintersTab()
        {
            var page = new TabPage("Printers") { Padding = new Padding(10) };
            SetupList(printerList, new object[] { "Printer", 220 }, new object[] { "Type", 80 }, new object[] { "Status", 80 }, new object[] { "Driver", 200 }, new object[] { "Port", 120 });
            var bar = ButtonBar(
                Btn("Refresh", delegate { RefreshPrinters(); }),
                Btn("Test print (receipt / ESC-POS)", delegate { TestSelected("escpos"); }),
                Btn("Test print (any printer)", delegate { TestSelected("graphic"); }),
                Btn("Windows printer settings", delegate { Open("ms-settings:printers"); }));
            var hint = Hint("These are the printers installed in Windows on this computer. Web apps that you connect can list them and print directly, without a print dialog.");
            page.Controls.Add(printerList);
            page.Controls.Add(bar);
            page.Controls.Add(hint);
            return page;
        }

        TabPage AppsTab()
        {
            var page = new TabPage("Connected apps") { Padding = new Padding(10) };
            SetupList(appList, new object[] { "App", 160 }, new object[] { "Website", 300 }, new object[] { "Connected", 130 }, new object[] { "Last print", 130 });
            var bar = ButtonBar(
                Btn("Disconnect selected", delegate { RevokeSelected(); }),
                Btn("Refresh", delegate { RefreshApps(); }));
            var hint = Hint("Websites allowed to print on this computer. Disconnecting one means it has to ask (and be approved) again.");
            page.Controls.Add(appList);
            page.Controls.Add(bar);
            page.Controls.Add(hint);
            return page;
        }

        TabPage SettingsTab()
        {
            var page = new TabPage("Settings") { Padding = new Padding(18) };
            int y = 18;
            page.Controls.Add(new Label { Text = "Local port", Location = new Point(18, y + 3), AutoSize = true });
            portBox.Minimum = 1024;
            portBox.Maximum = 65535;
            portBox.Value = Config.Current.Port;
            portBox.Location = new Point(140, y);
            portBox.Width = 90;
            page.Controls.Add(portBox);
            page.Controls.Add(new Label { Text = "Web apps connect to http://127.0.0.1:<port>. Default 9311.", ForeColor = Muted, Location = new Point(240, y + 3), AutoSize = true });

            y += 40;
            autoStartBox.Text = "Start automatically when I sign in to Windows";
            autoStartBox.Checked = AutoStart.IsOn();
            autoStartBox.Location = new Point(18, y);
            autoStartBox.AutoSize = true;
            page.Controls.Add(autoStartBox);

            y += 30;
            approvalBox.Text = "Ask me before a new website can connect (recommended)";
            approvalBox.Checked = Config.Current.RequireApproval;
            approvalBox.Location = new Point(18, y);
            approvalBox.AutoSize = true;
            page.Controls.Add(approvalBox);

            y += 44;
            var save = Btn("Save", delegate { SaveSettings(); });
            save.Location = new Point(18, y);
            page.Controls.Add(save);
            var folder = Btn("Open data folder", delegate { Open(Config.Dir); });
            folder.Location = new Point(save.Right + 8, y);
            page.Controls.Add(folder);

            y += 56;
            page.Controls.Add(new Label
            {
                Text = Program.Name + " " + Program.Version + "\r\nSettings and log: " + Config.Dir + "\r\nClosing this window keeps the agent running in the system tray. Use the tray icon's menu to exit.",
                ForeColor = Muted,
                Location = new Point(18, y),
                AutoSize = true,
            });
            return page;
        }

        TabPage LogTab()
        {
            var page = new TabPage("Log") { Padding = new Padding(10) };
            logBox.Multiline = true;
            logBox.ReadOnly = true;
            logBox.ScrollBars = ScrollBars.Vertical;
            logBox.Dock = DockStyle.Fill;
            logBox.BackColor = Color.White;
            logBox.Font = new Font("Consolas", 9f);
            page.Controls.Add(logBox);
            return page;
        }

        // ---------- actions ----------

        public void RefreshStatus()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshStatus)); return; }
            statusLabel.Text = app.Running
                ? "●  Running on " + app.Address + "   ·   " + Environment.MachineName
                : "●  Not running - port " + Config.Current.Port + " is in use by another program (change it in Settings)";
        }

        void RefreshPrinters()
        {
            Cursor = Cursors.WaitCursor;
            printerList.BeginUpdate();
            printerList.Items.Clear();
            foreach (var p in Printers.List())
            {
                var item = new ListViewItem(p.Name + (p.IsDefault ? "  (default)" : ""));
                item.SubItems.Add(p.Kind);
                item.SubItems.Add(p.Status);
                item.SubItems.Add(p.Driver);
                item.SubItems.Add(p.Port);
                item.Tag = p.Name;
                if (p.Kind == "virtual") item.ForeColor = Muted;
                printerList.Items.Add(item);
            }
            printerList.EndUpdate();
            Cursor = Cursors.Default;
        }

        public void RefreshAppsFromAnyThread()
        {
            if (IsHandleCreated) BeginInvoke(new Action(RefreshApps));
        }

        void RefreshApps()
        {
            appList.BeginUpdate();
            appList.Items.Clear();
            foreach (var p in Config.PairingsSnapshot())
            {
                var item = new ListViewItem(p.App);
                item.SubItems.Add(p.Origin);
                item.SubItems.Add(p.PairedAt);
                item.SubItems.Add(string.IsNullOrEmpty(p.LastUsed) ? "-" : p.LastUsed);
                item.Tag = p.Token;
                appList.Items.Add(item);
            }
            appList.EndUpdate();
        }

        void RevokeSelected()
        {
            if (appList.SelectedItems.Count == 0) return;
            var item = appList.SelectedItems[0];
            if (MessageBox.Show(this, "Disconnect " + item.SubItems[1].Text + "?", Program.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Config.RemovePairing((string)item.Tag);
            Log.Info("Disconnected " + item.SubItems[1].Text);
            RefreshApps();
        }

        void TestSelected(string mode)
        {
            if (printerList.SelectedItems.Count == 0)
            {
                MessageBox.Show(this, "Select a printer first.", Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RunTest((string)printerList.SelectedItems[0].Tag, mode);
        }

        public void TestPrintDefault()
        {
            string name = Printers.DefaultName();
            if (string.IsNullOrEmpty(name)) { app.Notify("No default printer is set in Windows."); return; }
            var p = Printers.List().Find(x => x.Name == name);
            RunTest(name, p != null ? p.SuggestedMode : "graphic");
        }

        void RunTest(string printer, string mode)
        {
            var t = new Thread(delegate()
            {
                try
                {
                    var job = PrintJob.Test(new Dictionary<string, object> { { "printer", printer }, { "mode", mode } }, "Print Agent");
                    job.Printer = printer;
                    Printers.Print(job);
                    Log.Info("Test page sent to " + printer + " (" + mode + ")");
                    BeginInvoke(new Action(delegate { app.Notify("Test page sent to " + printer); }));
                }
                catch (Exception e)
                {
                    Log.Error("Test print on " + printer + " failed: " + e.Message);
                    BeginInvoke(new Action(delegate { MessageBox.Show(this, "Test print failed:\r\n" + e.Message, Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Error); }));
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void SaveSettings()
        {
            int port = (int)portBox.Value;
            bool portChanged = port != Config.Current.Port;
            Config.Current.Port = port;
            Config.Current.RequireApproval = approvalBox.Checked;
            Config.Current.StartWithWindows = autoStartBox.Checked;
            Config.Save();
            AutoStart.Set(autoStartBox.Checked);
            if (portChanged || !app.Running) app.StartServer();
            MessageBox.Show(this, portChanged ? "Saved. The agent now listens on " + app.Address + " - update the address in your web apps." : "Saved.", Program.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// Called from the HTTP thread when a website asks to connect.
        public bool AskPairing(string origin, string appName)
        {
            if (InvokeRequired) return (bool)Invoke(new Func<string, string, bool>(AskPairing), origin, appName);
            using (var dlg = new PairDialog(origin, appName)) return dlg.ShowDialog() == DialogResult.OK;
        }

        void AppendLogFromAnyThread(string line)
        {
            if (!IsHandleCreated) return;
            BeginInvoke(new Action(delegate
            {
                if (logBox.TextLength > 200000) logBox.Text = logBox.Text.Substring(100000);
                logBox.AppendText(line + Environment.NewLine);
            }));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!AllowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!hintShown) { hintShown = true; app.Notify("Still running in the system tray - receipts keep printing."); }
                return;
            }
            Log.Line -= AppendLogFromAnyThread;
            base.OnFormClosing(e);
        }

        // ---------- helpers ----------

        static void SetupList(ListView list, params object[][] columns)
        {
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.Dock = DockStyle.Fill;
            foreach (var c in columns) list.Columns.Add((string)c[0], (int)c[1]);
        }

        static FlowLayoutPanel ButtonBar(params Button[] buttons)
        {
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0) };
            bar.Controls.AddRange(buttons);
            return bar;
        }

        static Button Btn(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, Height = 30, Padding = new Padding(8, 0, 8, 0), UseVisualStyleBackColor = true };
            b.Click += click;
            return b;
        }

        static Label Hint(string text)
        {
            return new Label { Text = text, Dock = DockStyle.Top, Height = 34, ForeColor = Muted };
        }

        static void Open(string target)
        {
            try { Process.Start(target); }
            catch (Exception e) { Log.Error("Could not open " + target + ": " + e.Message); }
        }
    }

    /// "Allow this website to print?" prompt; declines by itself after 60 seconds.
    class PairDialog : Form
    {
        int secondsLeft = 60;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };

        public PairDialog(string origin, string appName)
        {
            Text = Program.Name + " - connection request";
            Icon = Icons.App();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ShowInTaskbar = true;
            ClientSize = new Size(470, 210);
            Font = new Font("Segoe UI", 9.5f);

            Controls.Add(new Label { Text = appName, Font = new Font("Segoe UI Semibold", 13f), Location = new Point(20, 18), AutoSize = true });
            Controls.Add(new Label { Text = origin, ForeColor = Color.FromArgb(53, 112, 173), Location = new Point(22, 50), AutoSize = true });
            Controls.Add(new Label
            {
                Text = "wants to see the printers on this computer and print to them without asking each time.\r\nOnly allow websites you use and trust.",
                Location = new Point(22, 78),
                Size = new Size(430, 50),
            });

            var allow = new Button { Text = "Allow", DialogResult = DialogResult.OK, Size = new Size(100, 32), Location = new Point(240, 150) };
            var deny = new Button { Text = "Deny", DialogResult = DialogResult.Cancel, Size = new Size(110, 32), Location = new Point(348, 150) };
            Controls.Add(allow);
            Controls.Add(deny);
            AcceptButton = allow;
            CancelButton = deny;

            timer.Tick += delegate
            {
                secondsLeft--;
                deny.Text = "Deny (" + secondsLeft + ")";
                if (secondsLeft <= 0) { timer.Stop(); DialogResult = DialogResult.Cancel; }
            };
            Shown += delegate { Activate(); System.Media.SystemSounds.Asterisk.Play(); timer.Start(); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }
}
