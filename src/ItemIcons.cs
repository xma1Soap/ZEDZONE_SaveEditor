using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace ZedZoneSaveEditor
{
    /// <summary>
    /// 物品图标：tools/export_icons.py 从游戏资源导出，拼成一张 icons.png（位置在 icons.tsv），打进 exe。
    /// 游戏是像素画，放大时用"最近邻"不糊。
    /// </summary>
    internal static class ItemIcons
    {
        private static Bitmap _atlas;
        private static Dictionary<int, Rectangle> _rects;
        private static readonly Dictionary<int, Bitmap> _cache = new Dictionary<int, Bitmap>();

        private static void Load()
        {
            if (_rects != null) return;
            _rects = new Dictionary<int, Rectangle>();
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var st = asm.GetManifestResourceStream("icons.png"))
                    if (st != null) _atlas = new Bitmap(st);
                using (var st = asm.GetManifestResourceStream("icons.tsv"))
                {
                    if (st == null) return;
                    using (var rd = new StreamReader(st, Encoding.UTF8))
                    {
                        string line;
                        while ((line = rd.ReadLine()) != null)
                        {
                            if (line.Length == 0 || line[0] == '#') continue;
                            var p = line.Split('\t');
                            if (p.Length < 5) continue;
                            int I(int i) => int.Parse(p[i], CultureInfo.InvariantCulture);
                            _rects[I(0)] = new Rectangle(I(1), I(2), I(3), I(4));
                        }
                    }
                }
            }
            catch
            {
                _atlas = null;    // 图标读不出来就不显示，别的功能照常
            }
        }

        public static bool Has(int id)
        {
            Load();
            return _atlas != null && _rects.ContainsKey(id);
        }

        /// <summary>原始大小的图标；没有返回 null。</summary>
        public static Bitmap Get(int id)
        {
            Load();
            if (_atlas == null || !_rects.TryGetValue(id, out var r)) return null;
            if (_cache.TryGetValue(id, out var b)) return b;
            b = _atlas.Clone(r, _atlas.PixelFormat);
            _cache[id] = b;
            return b;
        }

        /// <summary>画到矩形里（保持比例、居中）。rotated = 物品横放，图转 90 度。</summary>
        public static void Draw(Graphics g, int id, RectangleF dst, bool rotated = false, float alpha = 1f)
        {
            var img = Get(id);
            if (img == null || dst.Width < 2 || dst.Height < 2) return;
            float iw = rotated ? img.Height : img.Width, ih = rotated ? img.Width : img.Height;
            float s = Math.Min(dst.Width / iw, dst.Height / ih);
            if (s >= 1) s = (float)Math.Floor(s);          // 整数倍放大，像素不变形
            float w = iw * s, h = ih * s;
            float x = dst.X + (dst.Width - w) / 2, y = dst.Y + (dst.Height - h) / 2;
            var state = g.Save();
            g.InterpolationMode = s >= 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            System.Drawing.Imaging.ImageAttributes attr = null;
            if (alpha < 1f)
            {
                attr = new System.Drawing.Imaging.ImageAttributes();
                attr.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
            }
            if (rotated)
            {
                g.TranslateTransform(x + w / 2, y + h / 2);
                g.RotateTransform(90);
                var r = new Rectangle((int)Math.Round(-h / 2), (int)Math.Round(-w / 2), (int)Math.Round(h), (int)Math.Round(w));
                g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attr);
            }
            else
            {
                var r = new Rectangle((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(h));
                g.DrawImage(img, r, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attr);
            }
            attr?.Dispose();
            g.Restore(state);
        }

        /// <summary>做一张固定大小的小图（列表里用）。</summary>
        public static Bitmap Thumb(int id, int w, int h)
        {
            var b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) Draw(g, id, new RectangleF(1, 1, w - 2, h - 2));
            return b;
        }
    }
}
