using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;

namespace BloomPrintAgent
{
    /// Turns content blocks into ESC/POS bytes for thermal receipt printers.
    static class EscPos
    {
        static readonly Encoding Cp437 = Encoding.GetEncoding(437, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));

        public static byte[] Render(PrintJob job)
        {
            var o = new List<byte>();
            Add(o, 0x1B, 0x40);          // ESC @  initialise
            Add(o, 0x1B, 0x74, 0x00);    // ESC t 0  code page PC437
            if (job.OpenDrawer) Drawer(o);

            bool cutDone = false;
            foreach (var b in job.Content)
            {
                cutDone = false;
                switch (JsonVal.Str(b, "type").ToLowerInvariant())
                {
                    case "text": Text(o, b, job.Columns); break;
                    case "row": Row(o, b, job.Columns); break;
                    case "hr":
                    case "line":
                        string ch = JsonVal.Str(b, "char");
                        Line(o, new string(string.IsNullOrEmpty(ch) ? '-' : ch[0], job.Columns));
                        break;
                    case "feed": Add(o, 0x1B, 0x64, (byte)Clamp(JsonVal.Int(b, "lines", 1), 0, 20)); break;
                    case "qr": Qr(o, b); break;
                    case "barcode": Barcode(o, b); break;
                    case "image": Image(o, b, job.Dots); break;
                    case "cut": Cut(o); cutDone = true; break;
                    case "drawer": Drawer(o); break;
                    case "raw":
                        var data = JsonVal.Str(b, "data");
                        if (!string.IsNullOrEmpty(data)) o.AddRange(Convert.FromBase64String(data));
                        break;
                    default: throw new FormatException("Unknown block type '" + JsonVal.Str(b, "type") + "'.");
                }
            }
            if (job.Cut && !cutDone) Cut(o);
            return o.ToArray();
        }

        static void Text(List<byte> o, Dictionary<string, object> b, int cols)
        {
            int size = Clamp(JsonVal.Int(b, "size", 1), 1, 4);
            Align(o, JsonVal.Str(b, "align"));
            Style(o, JsonVal.Bool(b, "bold", false), JsonVal.Bool(b, "underline", false), size);
            foreach (var line in Wrap(JsonVal.Str(b, "text") ?? "", cols / size)) Line(o, line);
            Style(o, false, false, 1);
            Align(o, "left");
        }

        static void Row(List<byte> o, Dictionary<string, object> b, int cols)
        {
            int size = Clamp(JsonVal.Int(b, "size", 1), 1, 2);
            int w = cols / size;
            string left = Clean(JsonVal.Str(b, "left") ?? "");
            string right = Clean(JsonVal.Str(b, "right") ?? "");
            Align(o, "left");
            Style(o, JsonVal.Bool(b, "bold", false), false, size);
            if (right.Length >= w - 4)
            {
                foreach (var l in Wrap(left, w)) Line(o, l);
                Line(o, right.PadLeft(w));
            }
            else
            {
                var lines = Wrap(left, w - right.Length - 1);
                for (int i = 0; i < lines.Count - 1; i++) Line(o, lines[i]);
                Line(o, lines[lines.Count - 1].PadRight(w - right.Length) + right);
            }
            Style(o, false, false, 1);
        }

        static void Qr(List<byte> o, Dictionary<string, object> b)
        {
            byte[] data = Encoding.UTF8.GetBytes(JsonVal.Str(b, "data") ?? "");
            if (data.Length == 0) return;
            int size = Clamp(JsonVal.Int(b, "size", 6), 1, 16);
            string ecc = (JsonVal.Str(b, "ecc") ?? "M").ToUpperInvariant();
            byte e = (byte)(ecc == "L" ? 48 : ecc == "Q" ? 50 : ecc == "H" ? 51 : 49);
            Align(o, JsonVal.Str(b, "align") ?? "center");
            Add(o, 0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00); // model 2
            Add(o, 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)size); // module size
            Add(o, 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, e);          // error correction
            int len = data.Length + 3;
            Add(o, 0x1D, 0x28, 0x6B, (byte)(len % 256), (byte)(len / 256), 0x31, 0x50, 0x30);
            o.AddRange(data);                                             // store
            Add(o, 0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30);       // print
            Add(o, 0x0A);
            Align(o, "left");
        }

        static void Barcode(List<byte> o, Dictionary<string, object> b)
        {
            string value = Clean(JsonVal.Str(b, "data") ?? "");
            if (value.Length == 0) return;
            if (value.Length > 250) value = value.Substring(0, 250);
            Align(o, JsonVal.Str(b, "align") ?? "center");
            Add(o, 0x1D, 0x68, (byte)Clamp(JsonVal.Int(b, "height", 60), 20, 255)); // height
            Add(o, 0x1D, 0x77, (byte)Clamp(JsonVal.Int(b, "width", 2), 1, 6));     // module width
            Add(o, 0x1D, 0x48, (byte)(JsonVal.Bool(b, "hri", true) ? 2 : 0));      // human-readable text below
            byte[] payload = Cp437.GetBytes("{B" + value);
            Add(o, 0x1D, 0x6B, 73, (byte)payload.Length);                         // CODE128
            o.AddRange(payload);
            Add(o, 0x0A);
            Align(o, "left");
        }

        static void Image(List<byte> o, Dictionary<string, object> b, int maxDots)
        {
            using (var src = DecodeImage(JsonVal.Str(b, "data")))
            {
                int width = Clamp(JsonVal.Int(b, "width", Math.Min(src.Width, maxDots)), 8, maxDots);
                int height = Math.Max(1, (int)Math.Round(src.Height * (width / (double)src.Width)));
                int bytesPerRow = (width + 7) / 8;
                using (var bmp = new Bitmap(width, height))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White); // flattens transparency
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(src, 0, 0, width, height);
                    }
                    Align(o, JsonVal.Str(b, "align") ?? "center");
                    // send in bands so printers with small buffers keep up
                    for (int top = 0; top < height; top += 128)
                    {
                        int band = Math.Min(128, height - top);
                        Add(o, 0x1D, 0x76, 0x30, 0x00, (byte)(bytesPerRow % 256), (byte)(bytesPerRow / 256), (byte)(band % 256), (byte)(band / 256));
                        for (int y = top; y < top + band; y++)
                            for (int xb = 0; xb < bytesPerRow; xb++)
                            {
                                byte v = 0;
                                for (int bit = 0; bit < 8; bit++)
                                {
                                    int x = xb * 8 + bit;
                                    if (x >= width) continue;
                                    Color c = bmp.GetPixel(x, y);
                                    if (c.R * 0.299 + c.G * 0.587 + c.B * 0.114 < 128) v |= (byte)(0x80 >> bit);
                                }
                                o.Add(v);
                            }
                    }
                    Align(o, "left");
                }
            }
        }

        public static Image DecodeImage(string data)
        {
            if (string.IsNullOrEmpty(data)) throw new FormatException("Image block needs 'data' (base64 PNG/JPEG).");
            int comma = data.IndexOf(',');
            if (data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0) data = data.Substring(comma + 1);
            try { return System.Drawing.Image.FromStream(new MemoryStream(Convert.FromBase64String(data))); }
            catch { throw new FormatException("Image block 'data' is not a valid base64 image."); }
        }

        static void Cut(List<byte> o)
        {
            Add(o, 0x1B, 0x64, 0x04); // feed past the cutter
            Add(o, 0x1D, 0x56, 0x01); // partial cut
        }

        static void Drawer(List<byte> o) { Add(o, 0x1B, 0x70, 0x00, 0x19, 0xFA); }

        static void Align(List<byte> o, string a)
        {
            byte n = (byte)(a == "center" ? 1 : a == "right" ? 2 : 0);
            Add(o, 0x1B, 0x61, n);
        }

        static void Style(List<byte> o, bool bold, bool underline, int size)
        {
            Add(o, 0x1B, 0x45, (byte)(bold ? 1 : 0));
            Add(o, 0x1B, 0x2D, (byte)(underline ? 1 : 0));
            Add(o, 0x1D, 0x21, (byte)(((size - 1) << 4) | (size - 1)));
        }

        static void Line(List<byte> o, string s)
        {
            o.AddRange(Cp437.GetBytes(Clean(s)));
            o.Add(0x0A);
        }

        /// Word-wraps to the given width; always returns at least one line.
        public static List<string> Wrap(string text, int width)
        {
            var lines = new List<string>();
            width = Math.Max(1, width);
            foreach (var para in Clean(text).Split('\n'))
            {
                string rest = para.TrimEnd();
                if (rest.Length == 0) { lines.Add(""); continue; }
                while (rest.Length > width)
                {
                    int cut = rest.LastIndexOf(' ', width);
                    if (cut <= 0) cut = width;
                    lines.Add(rest.Substring(0, cut).TrimEnd());
                    rest = rest.Substring(cut).TrimStart();
                }
                lines.Add(rest);
            }
            if (lines.Count == 0) lines.Add("");
            return lines;
        }

        /// Replaces characters that receipt printers' code pages don't have.
        public static string Clean(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.Replace("\r\n", "\n"))
            {
                switch (c)
                {
                    case '–': case '—': case '−': sb.Append('-'); break;
                    case '‘': case '’': case '‚': sb.Append('\''); break;
                    case '“': case '”': case '„': sb.Append('"'); break;
                    case '…': sb.Append("..."); break;
                    case '•': sb.Append('*'); break;
                    case '×': sb.Append('x'); break;
                    case ' ': case ' ': case ' ': case '\t': sb.Append(' '); break;
                    case '₨': sb.Append("Rs"); break;
                    case '€': sb.Append("EUR"); break;
                    default:
                        if (c >= ' ' || c == '\n') sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }

        static void Add(List<byte> o, params byte[] bytes) { o.AddRange(bytes); }
    }
}
