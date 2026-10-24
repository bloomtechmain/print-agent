using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BloomPrintAgent
{
    public class HttpRequest
    {
        public string Method;
        public string Path;
        public string Query;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public string Origin { get { return Header("Origin"); } }
        public string BodyText { get { return Encoding.UTF8.GetString(Body); } }
    }

    public class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = new byte[0];
    }

    /// Minimal HTTP/1.1 server bound to loopback only. A raw socket server is used instead of
    /// HttpListener so it needs no admin rights / URL ACL and can answer Chrome's
    /// private-network preflight headers.
    public class HttpServer
    {
        const int MaxHeaderBytes = 32 * 1024;
        const int MaxBodyBytes = 25 * 1024 * 1024;

        readonly Func<HttpRequest, HttpResponse> handler;
        readonly List<TcpListener> listeners = new List<TcpListener>();
        volatile bool running;

        public HttpServer(Func<HttpRequest, HttpResponse> handler) { this.handler = handler; }

        public bool Running { get { return running; } }

        public void Start(int port)
        {
            var v4 = new TcpListener(IPAddress.Loopback, port);
            v4.Start(); // throws if the port is taken
            listeners.Add(v4);
            try
            {
                var v6 = new TcpListener(IPAddress.IPv6Loopback, port);
                v6.Start();
                listeners.Add(v6);
            }
            catch { } // IPv6 disabled - IPv4 is enough
            running = true;
            foreach (var l in listeners)
            {
                var listener = l;
                var t = new Thread(delegate() { AcceptLoop(listener); });
                t.IsBackground = true;
                t.Start();
            }
        }

        public void Stop()
        {
            running = false;
            foreach (var l in listeners) { try { l.Stop(); } catch { } }
            listeners.Clear();
        }

        void AcceptLoop(TcpListener l)
        {
            while (running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch { if (!running) return; continue; }
                ThreadPool.QueueUserWorkItem(delegate { Serve(c); });
            }
        }

        void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 15000;
                    client.SendTimeout = 15000;
                    var stream = client.GetStream();
                    var req = Read(stream);
                    if (req == null) return;
                    HttpResponse res;
                    try { res = handler(req); }
                    catch (Exception e)
                    {
                        Log.Error("Request failed: " + e);
                        res = Api.Error(500, "INTERNAL", e.Message);
                    }
                    Write(stream, req, res);
                }
                catch (IOException) { }
                catch (Exception e) { Log.Error("Connection error: " + e.Message); }
            }
        }

        static HttpRequest Read(NetworkStream s)
        {
            var head = new MemoryStream();
            var buf = new byte[8192];
            int headerEnd = -1;
            byte[] extra = null;
            while (headerEnd < 0)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) return null;
                head.Write(buf, 0, n);
                if (head.Length > MaxHeaderBytes) return null;
                headerEnd = IndexOfHeaderEnd(head.GetBuffer(), (int)head.Length);
            }
            byte[] all = head.ToArray();
            int bodyStart = headerEnd + 4;
            extra = new byte[all.Length - bodyStart];
            Buffer.BlockCopy(all, bodyStart, extra, 0, extra.Length);

            string[] lines = Encoding.ASCII.GetString(all, 0, headerEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            var req = new HttpRequest();
            req.Method = first[0].ToUpperInvariant();
            string target = first[1];
            int q = target.IndexOf('?');
            req.Path = q >= 0 ? target.Substring(0, q) : target;
            req.Query = q >= 0 ? target.Substring(q + 1) : "";
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) req.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }

            int len = 0;
            string cl = req.Header("Content-Length");
            if (cl != null && (!int.TryParse(cl, out len) || len < 0 || len > MaxBodyBytes)) return null;
            if (len > 0)
            {
                var body = new byte[len];
                int got = Math.Min(extra.Length, len);
                Buffer.BlockCopy(extra, 0, body, 0, got);
                while (got < len)
                {
                    int n = s.Read(body, got, len - got);
                    if (n <= 0) return null;
                    got += n;
                }
                req.Body = body;
            }
            return req;
        }

        static int IndexOfHeaderEnd(byte[] b, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (b[i] == 13 && b[i + 1] == 10 && b[i + 2] == 13 && b[i + 3] == 10) return i;
            return -1;
        }

        static void Write(NetworkStream s, HttpRequest req, HttpResponse res)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(Reason(res.Status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(res.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(res.Body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n");
            // CORS: every origin may *talk* to the agent; only paired origins get past auth.
            string origin = req.Origin;
            sb.Append("Access-Control-Allow-Origin: ").Append(string.IsNullOrEmpty(origin) ? "*" : origin).Append("\r\n");
            sb.Append("Vary: Origin\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Authorization\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            sb.Append("Access-Control-Max-Age: 600\r\n");
            sb.Append("\r\n");
            var headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            s.Write(headBytes, 0, headBytes.Length);
            if (req.Method != "HEAD" && res.Body.Length > 0) s.Write(res.Body, 0, res.Body.Length);
            s.Flush();
        }

        static string Reason(int code)
        {
            switch (code)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 421: return "Misdirected Request";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                default: return "Status";
            }
        }
    }
}
