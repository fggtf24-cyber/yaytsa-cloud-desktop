using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YaytsaCloud
{
    // Меню значка в трее в цветах сайта: тёмное или светлое вместе с темой, со скруглёнными углами на Windows 11.
    class ThemedMenu : ContextMenuStrip
    {
        Color bg, hover, line, text, muted;

        public ThemedMenu()
        {
            Font = new Font("Segoe UI", 10f);
            ShowImageMargin = false;
            ShowCheckMargin = true;
            Padding = new Padding(4);
            DropShadowEnabled = true;
            SetTheme(false);
        }

        public void SetTheme(bool light)
        {
            bg = light ? Color.White : Color.FromArgb(35, 35, 35);         // --card
            hover = light ? Color.FromArgb(232, 232, 232) : Color.FromArgb(46, 46, 46); // --fill
            line = light ? Color.FromArgb(225, 225, 225) : Color.FromArgb(58, 58, 58);  // --line
            text = light ? Color.FromArgb(17, 17, 17) : Color.FromArgb(240, 240, 240);
            muted = light ? Color.FromArgb(110, 110, 110) : Color.FromArgb(189, 189, 189);
            BackColor = bg;
            ForeColor = text;
            Renderer = new Painter(this);
            foreach (ToolStripItem i in Items) { i.ForeColor = text; i.Padding = new Padding(4, 6, 4, 6); }
            Invalidate();
            if (IsHandleCreated) Frame();
        }

        void Frame()
        {
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            int border = ColorTranslator.ToWin32(line);
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Frame();
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        class Painter : ToolStripRenderer
        {
            readonly ThemedMenu m;
            public Painter(ThemedMenu menu) => m = menu;

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(m.bg);

            // Рамку рисует сама Windows 11 по скруглённому краю (цвет задаём в SetTheme). Своя квадратная обрезалась бы на углах.
            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var r = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(r, 6);
                using var brush = new SolidBrush(m.hover);
                e.Graphics.FillPath(brush, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? m.text : m.muted;
                // По высоте ровно по центру пункта: иначе с отступами текст прилипает к верху подсветки.
                e.TextRectangle = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, e.Item.Height);
                e.TextFormat = (e.TextFormat & ~(TextFormatFlags.Top | TextFormatFlags.Bottom)) | TextFormatFlags.VerticalCenter;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // Своя галочка: системная рисуется в голубой рамке и не подходит к теме.
                var r = e.ImageRectangle;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(m.text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float x = r.Left + r.Width / 2f, y = e.Item.Height / 2f;
                e.Graphics.DrawLines(pen, new[] { new PointF(x - 5, y), new PointF(x - 1.5f, y + 3.5f), new PointF(x + 5, y - 4) });
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using var pen = new Pen(m.line);
                int y = e.Item.Height / 2;
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
            }

            static GraphicsPath Rounded(Rectangle r, int radius)
            {
                var p = new GraphicsPath();
                int d = radius * 2;
                p.AddArc(r.Left, r.Top, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }
    }
}
