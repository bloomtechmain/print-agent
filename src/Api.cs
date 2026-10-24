using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace BloomPrintAgent
{
    /// Routes for the local HTTP API (see README.md for the contract).
    static class Api
    {
        /// Asks the person at this PC to approve a pairing (origin, app name) - set by the UI.
        public static Func<string, string, bool> ApprovePairing;
        /// Raised when pairings change (paired / revoked / used).
        public static event Action Changed;

        static readonly object pairGate = new object();
        static readonly object printGate = new object();

        public static HttpResponse Handle(HttpRequest r)
        {
            if (r.Method == "OPTIONS") return new HttpResponse { Status = 204 };
            if (!HostAllowed(r.Header("Host"))) return Error(421, "BAD_HOST", "Requests must be addressed to 127.0.0.1 or localhost.");

            string path = r.Path.TrimEnd('/').ToLowerInvariant();
            switch (path)
            {
                case "":
                    return Html(LandingPage());
                case "/v1/status":
                    return Status(r);
                case "/v1/pair":
                    return r.Method == "POST" ? Pair(r) : MethodNotAllowed();
            }

            Pairing who;
            var denied = Authorize(r, out who);
            if (denied != null) return denied;

            switch (path)
            {
                case "/v1/printers":
                    return Json(200, new Dictionary<string, object> { { "ok", true }, { "printers", Printers.List().Select(p => p.ToJson()).ToList() } });
                case "/v1/print":
                    return r.Method == "POST" ? Print(r, who, false) : MethodNotAllowed();
                case "/v1/print/test":
                    return r.Method == "POST" ? Print(r, who, true) : MethodNotAllowed();
                case "/v1/unpair":
                    if (r.Method != "POST") return MethodNotAllowed();
                    Config.RemovePairing(who.Token);
                    Log.Info("Unpaired " + who.Origin);
                    RaiseChanged();
                    return Json(200, new Dictionary<string, object> { { "ok", true } });
            }
            return Error(404, "NOT_FOUND", "Unknown endpoint " + r.Path);
        }

        static HttpResponse Status(HttpRequest r)
        {
            var p = Config.FindByToken(Bearer(r));
            bool paired = p != null && OriginMatches(p, r.Origin);
            return Json(200, new Dictionary<string, object>
            {
                { "ok", true },
                { "agent", Program.Name },
                { "version", Program.Version },
                { "api", 1 },
                { "machine", Environment.MachineName },
                { "paired", paired },
                { "requireApproval", Config.Current.RequireApproval },
            });
        }

        static HttpResponse Pair(HttpRequest r)
        {
            var body = ParseBody(r);
            if (body == null) return Error(400, "BAD_REQUEST", "Body must be JSON.");
            string origin = string.IsNullOrEmpty(r.Origin) ? "local-app" : r.Origin;
            string app = Clip(JsonVal.Str(body, "app"), 60);

            lock (pairGate) // one approval prompt at a time
            {
                bool approved = !Config.Current.RequireApproval;
                if (!approved)
                {
                    var ask = ApprovePairing;
                    approved = ask != null && ask(origin, string.IsNullOrEmpty(app) ? origin : app);
                }
                if (!approved)
                {
                    Log.Info("Pairing denied for " + origin);
                    return Error(403, "PAIRING_DENIED", "The request was declined (or timed out) on the print agent.");
                }
                var p = Config.AddPairing(origin, app);
                Log.Info("Paired " + p.App + " (" + origin + ")");
                RaiseChanged();
                return Json(200, new Dictionary<string, object>
                {
                    { "ok", true },
                    { "token", p.Token },
                    { "machine", Environment.MachineName },
                });
            }
        }

        static HttpResponse Print(HttpRequest r, Pairing who, bool test)
        {
            var body = ParseBody(r);
            if (body == null) return Error(400, "BAD_REQUEST", "Body must be JSON.");
            PrintJob job;
            try { job = test ? PrintJob.Test(body, who.App) : PrintJob.Parse(body); }
            catch (FormatException e) { return Error(400, "BAD_REQUEST", e.Message); }

            string printer = string.IsNullOrEmpty(job.Printer) ? Printers.DefaultName() : job.Printer;
            if (string.IsNullOrEmpty(printer) || !Printers.Exists(printer))
                return Error(404, "PRINTER_NOT_FOUND", "Printer '" + (job.Printer ?? "(default)") + "' is not installed on " + Environment.MachineName + ".");
            job.Printer = printer;

            try
            {
                PrintResult result;
                lock (printGate) result = Printers.Print(job);
                Log.Info("Printed '" + job.Title + "' on " + printer + " (" + result.Mode + ", " + result.Bytes + " bytes) for " + who.App);
                return Json(200, new Dictionary<string, object>
                {
                    { "ok", true },
                    { "printer", printer },
                    { "mode", result.Mode },
                    { "jobId", result.JobId },
                    { "bytes", result.Bytes },
                });
            }
            catch (Exception e)
            {
                Log.Error("Print on " + printer + " failed: " + e.Message);
                return Error(502, "PRINT_FAILED", e.Message);
            }
        }

        static HttpResponse Authorize(HttpRequest r, out Pairing who)
        {
            who = Config.FindByToken(Bearer(r));
            if (who == null) return Error(401, "UNAUTHORIZED", "Not paired. Call POST /v1/pair first.");
            if (!OriginMatches(who, r.Origin)) { who = null; return Error(401, "UNAUTHORIZED", "This token belongs to a different website."); }
            who.LastUsed = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            return null;
        }

        static bool OriginMatches(Pairing p, string origin)
        {
            // browsers always send Origin on cross-origin calls; native local apps may not
            if (string.IsNullOrEmpty(origin)) return true;
            return string.Equals(p.Origin, origin, StringComparison.OrdinalIgnoreCase);
        }

        static string Bearer(HttpRequest r)
        {
            string a = r.Header("Authorization");
            if (a != null && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return a.Substring(7).Trim();
            return null;
        }

        // blocks DNS-rebinding: a page on evil.example resolving to 127.0.0.1 still sends Host: evil.example
        static bool HostAllowed(string host)
        {
            if (string.IsNullOrEmpty(host)) return true;
            string h = host.ToLowerInvariant();
            int colon = h.LastIndexOf(':');
            if (colon > 0 && h.IndexOf(']') < colon) h = h.Substring(0, colon);
            return h == "127.0.0.1" || h == "localhost" || h == "[::1]";
        }

        static Dictionary<string, object> ParseBody(HttpRequest r)
        {
            if (r.Body.Length == 0) return new Dictionary<string, object>();
            try { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(r.BodyText) as Dictionary<string, object>; }
            catch { return null; }
        }

        static void RaiseChanged()
        {
            var h = Changed;
            if (h != null) h();
        }

        static string Clip(string s, int max)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        static HttpResponse MethodNotAllowed() { return Error(405, "METHOD_NOT_ALLOWED", "Method not allowed."); }

        public static HttpResponse Error(int status, string code, string message)
        {
            return Json(status, new Dictionary<string, object> { { "ok", false }, { "code", code }, { "error", message } });
        }

        public static HttpResponse Json(int status, object o)
        {
            var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            return new HttpResponse { Status = status, Body = Encoding.UTF8.GetBytes(s.Serialize(o)) };
        }

        static HttpResponse Html(string html)
        {
            return new HttpResponse { ContentType = "text/html; charset=utf-8", Body = Encoding.UTF8.GetBytes(html) };
        }

        static string LandingPage()
        {
            return "<!doctype html><meta charset=utf-8><title>" + Program.Name + "</title>" +
                "<body style=\"font:15px system-ui;margin:40px;color:#1f2d4a\"><h2>&#x1F5A8; " + Program.Name + " is running</h2>" +
                "<p>Version " + Program.Version + " on <b>" + Environment.MachineName + "</b>. " +
                "Web apps connect to this address from their printer settings.</p></body>";
        }
    }

    /// Small helpers for the Dictionary&lt;string, object&gt; trees JavaScriptSerializer produces.
    static class JsonVal
    {
        public static object Get(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            return v == null ? null : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static int Int(Dictionary<string, object> d, string key, int def)
        {
            var v = Get(d, key);
            if (v == null) return def;
            try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        public static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            var v = Get(d, key);
            return v is bool ? (bool)v : def;
        }
    }
}
