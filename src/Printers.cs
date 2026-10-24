using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing.Printing;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace BloomPrintAgent
{
    public class PrinterInfo
    {
        public string Name;
        public bool IsDefault;
        public string Status;   // ready | printing | offline | error | unknown
        public string Driver;
        public string Port;
        public bool IsNetwork;
        public bool IsShared;
        public string Kind;     // thermal | standard | virtual
        public string SuggestedMode;

        public Dictionary<string, object> ToJson()
        {
            return new Dictionary<string, object>
            {
                { "name", Name }, { "isDefault", IsDefault }, { "status", Status }, { "driver", Driver },
                { "port", Port }, { "isNetwork", IsNetwork }, { "isShared", IsShared }, { "kind", Kind },
                { "suggestedMode", SuggestedMode },
            };
        }
    }

    public class PrintResult
    {
        public string Mode;
        public int JobId;
        public int Bytes;
    }

    /// A parsed /v1/print request.
    public class PrintJob
    {
        public string Printer;
        public string Title = "Receipt";
        public string Mode = "escpos";   // escpos | graphic | raw
        public int Copies = 1;
        public int PaperWidth = 80;      // mm
        public bool FullWidth;           // paperWidth 0: use the whole printable page (graphic)
        public int Dots;                 // printable dots across (escpos)
        public int Columns;              // characters per line, Font A (escpos)
        public float FontSize = 9f;      // pt (graphic)
        public bool Cut = true;
        public bool OpenDrawer;
        public List<Dictionary<string, object>> Content = new List<Dictionary<string, object>>();
        public byte[] Raw;
        public string OutputFile;        // graphic mode: print to this file instead (tests / "print to PDF")

        public static PrintJob Parse(Dictionary<string, object> b)
        {
            var j = new PrintJob();
            j.Printer = JsonVal.Str(b, "printer");
            j.Title = JsonVal.Str(b, "title") ?? "Receipt";
            j.Mode = (JsonVal.Str(b, "mode") ?? "escpos").ToLowerInvariant();
            j.Copies = Math.Max(1, Math.Min(10, JsonVal.Int(b, "copies", 1)));
            int paper = JsonVal.Int(b, "paperWidth", 80);
            j.FullWidth = paper <= 0;
            j.PaperWidth = j.FullWidth ? 80 : Math.Max(40, Math.Min(330, paper));
            j.Dots = JsonVal.Int(b, "dots", j.PaperWidth <= 58 ? 384 : j.PaperWidth <= 80 ? 576 : (j.PaperWidth - 8) * 8);
            j.Columns = JsonVal.Int(b, "columns", j.Dots / 12);
            j.FontSize = Math.Max(5, Math.Min(30, JsonVal.Int(b, "fontSize", 9)));
            j.Cut = JsonVal.Bool(b, "cut", true);
            j.OpenDrawer = JsonVal.Bool(b, "openDrawer", false);

            string raw = JsonVal.Str(b, "raw");
            string text = JsonVal.Str(b, "text");
            var content = JsonVal.Get(b, "content") as IEnumerable;

            if (!string.IsNullOrEmpty(raw))
            {
                try { j.Raw = Convert.FromBase64String(raw); }
                catch { throw new FormatException("'raw' must be base64."); }
                j.Mode = "raw";
            }
            else if (content != null && !(content is string))
            {
                foreach (var o in content)
                {
                    var block = o as Dictionary<string, object>;
                    if (block == null || JsonVal.Str(block, "type") == null) throw new FormatException("Every content block needs a 'type'.");
                    j.Content.Add(block);
                }
            }
            else if (text != null)
            {
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    j.Content.Add(new Dictionary<string, object> { { "type", "text" }, { "text", line } });
            }
            else throw new FormatException("Send one of 'content' (blocks), 'text' or 'raw'.");

            if (j.Mode != "escpos" && j.Mode != "graphic" && j.Mode != "raw") throw new FormatException("'mode' must be escpos, graphic or raw.");
            return j;
        }

        public static PrintJob Test(Dictionary<string, object> b, string app)
        {
            var body = new Dictionary<string, object>(b);
            body["title"] = "Test page";
            body["content"] = new object[]
            {
                Block("text", "text", Program.Name, "align", "center", "bold", true, "size", 2),
                Block("text", "text", "Test print", "align", "center"),
                Block("hr"),
                Block("row", "left", "Printer", "right", JsonVal.Str(b, "printer") ?? "(default)"),
                Block("row", "left", "Computer", "right", Environment.MachineName),
                Block("row", "left", "Requested by", "right", app ?? "-"),
                Block("row", "left", "Time", "right", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
                Block("hr"),
                Block("text", "text", "Left aligned"),
                Block("text", "text", "Centered", "align", "center"),
                Block("text", "text", "Right aligned", "align", "right"),
                Block("text", "text", "Bold text", "bold", true),
                Block("row", "left", "TOTAL", "right", "1,234.00", "bold", true),
                Block("feed", "lines", 1),
                Block("qr", "data", "bloom-print-agent-test", "size", 5),
                Block("text", "text", "If you can read this, printing works.", "align", "center"),
            };
            return Parse(body);
        }

        static Dictionary<string, object> Block(string type, params object[] kv)
        {
            var d = new Dictionary<string, object> { { "type", type } };
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
    }

    static class Printers
    {
        static readonly Regex VirtualRx = new Regex(@"pdf|xps|onenote|fax|send to|print to|document writer|snagit", RegexOptions.IgnoreCase);
        static readonly Regex ThermalRx = new Regex(@"\bpos\b|pos-?\d|receipt|thermal|xprinter|xp-|\btm-|tsp\d|epson tm|star |rongta|gprinter|bixolon|citizen|sewoo|zjiang|hprt|sunmi|80mm|58mm|generic / text", RegexOptions.IgnoreCase);

        public static List<PrinterInfo> List()
        {
            var list = new List<PrinterInfo>();
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name, Default, PrinterStatus, WorkOffline, DriverName, PortName, Network, Shared, DetectedErrorState FROM Win32_Printer"))
                foreach (ManagementObject m in q.Get())
                {
                    var p = new PrinterInfo
                    {
                        Name = (string)m["Name"],
                        IsDefault = m["Default"] is bool && (bool)m["Default"],
                        Driver = m["DriverName"] as string ?? "",
                        Port = m["PortName"] as string ?? "",
                        IsNetwork = m["Network"] is bool && (bool)m["Network"],
                        IsShared = m["Shared"] is bool && (bool)m["Shared"],
                    };
                    bool offline = m["WorkOffline"] is bool && (bool)m["WorkOffline"];
                    int status = m["PrinterStatus"] == null ? 2 : Convert.ToInt32(m["PrinterStatus"]);
                    p.Status = offline || status == 7 ? "offline" : status == 4 ? "printing" : status == 3 ? "ready" : status == 6 ? "error" : "unknown";
                    Classify(p);
                    list.Add(p);
                }
            }
            catch (Exception e)
            {
                // WMI can be disabled/broken; the spooler list still works
                Log.Error("WMI printer query failed, using basic list: " + e.Message);
                string def = DefaultName();
                foreach (string name in PrinterSettings.InstalledPrinters)
                {
                    var p = new PrinterInfo { Name = name, IsDefault = name == def, Status = "unknown", Driver = "", Port = "" };
                    Classify(p);
                    list.Add(p);
                }
            }
            return list.OrderByDescending(p => p.Kind == "thermal").ThenByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToList();
        }

        static void Classify(PrinterInfo p)
        {
            string hay = p.Name + " " + p.Driver;
            p.Kind = VirtualRx.IsMatch(hay) ? "virtual" : ThermalRx.IsMatch(hay) ? "thermal" : "standard";
            p.SuggestedMode = p.Kind == "thermal" ? "escpos" : "graphic";
        }

        public static string DefaultName()
        {
            try { return new PrinterSettings().PrinterName; }
            catch { return null; }
        }

        public static bool Exists(string name)
        {
            foreach (string n in PrinterSettings.InstalledPrinters)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static PrintResult Print(PrintJob job)
        {
            if (job.Mode == "graphic")
            {
                GraphicPrinter.Print(job);
                return new PrintResult { Mode = "graphic" };
            }
            byte[] once = job.Mode == "raw" ? job.Raw : EscPos.Render(job);
            var data = new byte[once.Length * job.Copies];
            for (int i = 0; i < job.Copies; i++) Buffer.BlockCopy(once, 0, data, i * once.Length, once.Length);
            int id = RawPrinter.Send(job.Printer, job.Title, data);
            return new PrintResult { Mode = job.Mode, JobId = id, Bytes = data.Length };
        }
    }

    /// Sends bytes straight to a Windows print queue with the RAW datatype (bypasses the driver's rendering).
    static class RawPrinter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class DOCINFO
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool ClosePrinter(IntPtr handle);
        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern int StartDocPrinter(IntPtr handle, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFO di);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool EndDocPrinter(IntPtr handle);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool StartPagePrinter(IntPtr handle);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool EndPagePrinter(IntPtr handle);
        [DllImport("winspool.drv", SetLastError = true)]
        static extern bool WritePrinter(IntPtr handle, byte[] data, int count, out int written);

        public static int Send(string printer, string title, byte[] data)
        {
            IntPtr h;
            if (!OpenPrinter(printer, out h, IntPtr.Zero)) throw Fail("Could not open printer");
            try
            {
                int job = StartDocPrinter(h, 1, new DOCINFO { pDocName = title, pDataType = "RAW" });
                if (job == 0) throw Fail("Printer refused the job");
                try
                {
                    if (!StartPagePrinter(h)) throw Fail("Could not start page");
                    int written;
                    bool ok = WritePrinter(h, data, data.Length, out written);
                    EndPagePrinter(h);
                    if (!ok || written != data.Length) throw Fail("Could not send data to printer");
                }
                finally { EndDocPrinter(h); }
                return job;
            }
            finally { ClosePrinter(h); }
        }

        static Exception Fail(string what)
        {
            return new Exception(what + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
        }
    }
}
