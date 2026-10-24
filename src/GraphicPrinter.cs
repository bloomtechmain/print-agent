using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;

namespace BloomPrintAgent
{
    /// Prints content blocks through the Windows printer driver (GDI). Works on any printer
    /// (laser, inkjet, thermal with a non-RAW driver) at the cost of speed and native QR/barcodes.
    static class GraphicPrinter
    {
        class Op
        {
            public float Height;
            public Action<Graphics, float, float, float> Draw; // g, x, y, width
        }

        public static void Print(PrintJob job)
        {
            using (var doc = new PrintDocument())
            {
                doc.PrinterSettings.PrinterName = job.Printer;
                if (!doc.PrinterSettings.IsValid) throw new Exception("Printer '" + job.Printer + "' is not available.");
                doc.PrinterSettings.Copies = (short)job.Copies;
                doc.DocumentName = job.Title;
                doc.PrintController = new StandardPrintController(); // no "Printing..." dialog
                doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
                doc.OriginAtMargins = false;
                if (job.OutputFile != null)
                {
                    doc.PrinterSettings.PrintToFile = true;
                    doc.PrinterSettings.PrintFileName = job.OutputFile;
                }

                var fonts = new Dictionary<string, Font>();
                List<Op> ops = null;
                float width = 0;
                int next = 0;
                Exception failure = null;

                doc.PrintPage += delegate(object s, PrintPageEventArgs e)
                {
                    try
                    {
                        var g = e.Graphics;
                        RectangleF area = e.PageSettings.PrintableArea; // 1/100 inch
                        if (e.PageSettings.Landscape) area = new RectangleF(area.Y, area.X, area.Height, area.Width);
                        if (ops == null)
                        {
                            width = area.Width;
                            if (!job.FullWidth) width = Math.Min(width, job.PaperWidth / 25.4f * 100f - 8f);
                            ops = Layout(g, job, width, fonts);
                        }
                        float y = 0;
                        while (next < ops.Count)
                        {
                            var op = ops[next];
                            if (y > 0 && y + op.Height > area.Height) { e.HasMorePages = true; return; }
                            if (op.Draw != null) op.Draw(g, 0, y, width);
                            y += op.Height;
                            next++;
                        }
                        e.HasMorePages = false;
                    }
                    catch (Exception ex) { failure = ex; e.HasMorePages = false; }
                };
                try { doc.Print(); }
                finally { foreach (var f in fonts.Values) f.Dispose(); }
                if (failure != null) throw failure;
            }
        }

        static List<Op> Layout(Graphics g, PrintJob job, float width, Dictionary<string, Font> fonts)
        {
            var ops = new List<Op>();
            Func<int, bool, Font> font = delegate(int size, bool bold)
            {
                string key = size + (bold ? "b" : "r");
                Font f;
                if (!fonts.TryGetValue(key, out f))
                {
                    float pt = job.FontSize * (size == 1 ? 1f : 0.6f + size * 0.55f);
                    f = new Font("Segoe UI", pt, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point);
                    fonts[key] = f;
                }
                return f;
            };
            var black = Brushes.Black;

            foreach (var b in job.Content)
            {
                string type = JsonVal.Str(b, "type").ToLowerInvariant();
                int size = Math.Max(1, Math.Min(4, JsonVal.Int(b, "size", 1)));
                bool bold = JsonVal.Bool(b, "bold", false);
                switch (type)
                {
                    case "text":
                    case "qr":      // no native QR/barcode through the driver: print the value as text
                    case "barcode":
                    {
                        string text = EscPos.Clean(type == "text" ? JsonVal.Str(b, "text") : JsonVal.Str(b, "data"));
                        if (text.Length == 0) text = " ";
                        var f = font(type == "text" ? size : 1, bold); // a QR "size" is module size, not text size
                        var fmt = Format(JsonVal.Str(b, "align") ?? (type == "text" ? "left" : "center"));
                        float h = g.MeasureString(text, f, (int)width, fmt).Height;
                        ops.Add(new Op { Height = h, Draw = (gr, x, y, w) => gr.DrawString(text, f, black, new RectangleF(x, y, w, h + 2), fmt) });
                        break;
                    }
                    case "row":
                    {
                        string left = EscPos.Clean(JsonVal.Str(b, "left"));
                        string right = EscPos.Clean(JsonVal.Str(b, "right"));
                        var f = font(Math.Min(size, 2), bold);
                        float rw = g.MeasureString(right, f).Width;
                        float lw = Math.Max(width * 0.35f, width - rw - 6);
                        float lh = g.MeasureString(left.Length == 0 ? " " : left, f, (int)lw).Height;
                        float rh = g.MeasureString(right.Length == 0 ? " " : right, f).Height;
                        float h = Math.Max(lh, rh);
                        var leftFmt = Format("left");
                        var rightFmt = Format("right");
                        ops.Add(new Op
                        {
                            Height = h,
                            Draw = (gr, x, y, w) =>
                            {
                                gr.DrawString(left, f, black, new RectangleF(x, y, lw, h + 2), leftFmt);
                                gr.DrawString(right, f, black, new RectangleF(x, y + h - rh, w, rh + 2), rightFmt);
                            },
                        });
                        break;
                    }
                    case "hr":
                    case "line":
                    {
                        float h = font(1, false).GetHeight(g) * 0.8f;
                        ops.Add(new Op
                        {
                            Height = h,
                            Draw = (gr, x, y, w) =>
                            {
                                using (var pen = new Pen(Color.Black, 1f) { DashStyle = DashStyle.Dash })
                                    gr.DrawLine(pen, x, y + h / 2, x + w, y + h / 2);
                            },
                        });
                        break;
                    }
                    case "feed":
                        ops.Add(new Op { Height = font(1, false).GetHeight(g) * Math.Max(0, JsonVal.Int(b, "lines", 1)) });
                        break;
                    case "image":
                    {
                        var img = EscPos.DecodeImage(JsonVal.Str(b, "data"));
                        // "width" is in printer dots (203 dpi); convert to 1/100 inch
                        float w100 = Math.Min(width, JsonVal.Int(b, "width", (int)(img.Width / 2.03f)) / 2.03f);
                        float h100 = img.Height * (w100 / img.Width);
                        string align = JsonVal.Str(b, "align") ?? "center";
                        ops.Add(new Op
                        {
                            Height = h100 + 4,
                            Draw = (gr, x, y, w) =>
                            {
                                float left = align == "left" ? x : align == "right" ? x + w - w100 : x + (w - w100) / 2;
                                gr.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                gr.DrawImage(img, left, y, w100, h100);
                            },
                        });
                        break;
                    }
                    case "cut":
                    case "drawer":
                    case "raw":
                        break; // not meaningful through a driver
                    default:
                        throw new FormatException("Unknown block type '" + type + "'.");
                }
            }
            return ops;
        }

        static StringFormat Format(string align)
        {
            var f = new StringFormat(StringFormat.GenericDefault);
            f.Alignment = align == "center" ? StringAlignment.Center : align == "right" ? StringAlignment.Far : StringAlignment.Near;
            f.Trimming = StringTrimming.Word;
            return f;
        }
    }
}
