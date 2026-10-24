using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace BloomPrintAgent
{
    /// The app icon is drawn in code so the project has no binary assets to keep in sync.
    static class Icons
    {
        static Icon app;

        public static Icon App()
        {
            if (app == null)
            {
                try { app = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { }
                if (app == null) app = Icon.FromHandle(Draw(32).GetHicon());
            }
            return app;
        }

        public static Icon Tray()
        {
            int s = SystemInformation.SmallIconSize.Width;
            return Icon.FromHandle(Draw(s <= 16 ? 16 : s <= 24 ? 24 : 32).GetHicon());
        }

        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;

                using (var bg = Rounded(new RectangleF(0.5f * s, 0.5f * s, 31 * s, 31 * s), 7 * s))
                using (var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(0, size), Color.FromArgb(58, 104, 178), Color.FromArgb(31, 58, 104)))
                    g.FillPath(brush, bg);

                var white = Brushes.White;
                // paper feeding in at the top
                g.FillRectangle(white, 10 * s, 5 * s, 12 * s, 6 * s);
                // printer body
                using (var body = Rounded(new RectangleF(5 * s, 10 * s, 22 * s, 11 * s), 2.5f * s))
                    g.FillPath(white, body);
                using (var slot = new SolidBrush(Color.FromArgb(31, 58, 104)))
                    g.FillRectangle(slot, 8 * s, 17.5f * s, 16 * s, 1.6f * s);
                using (var led = new SolidBrush(Color.FromArgb(58, 169, 133)))
                    g.FillEllipse(led, 22 * s, 12.5f * s, 2.6f * s, 2.6f * s);
                // receipt coming out
                g.FillRectangle(white, 10 * s, 18.5f * s, 12 * s, 9 * s);
                using (var ink = new Pen(Color.FromArgb(140, 160, 190), Math.Max(1f, 1.1f * s)))
                {
                    g.DrawLine(ink, 12 * s, 22 * s, 20 * s, 22 * s);
                    g.DrawLine(ink, 12 * s, 25 * s, 17 * s, 25 * s);
                }
            }
            return bmp;
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// Writes a multi-size .ico (PNG-compressed entries) - used by build.ps1 for the exe icon.
        public static void WriteIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            var images = new List<byte[]>();
            foreach (int s in sizes)
                using (var bmp = Draw(s))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    images.Add(ms.ToArray());
                }

            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(images[i].Length);
                    w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
