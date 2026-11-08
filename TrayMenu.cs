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
            Renderer = new Paint(this);
            foreach (ToolStripItem i in Items) { i.ForeColor = text; i.Padding = new Padding(4, 6, 4, 6); }
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        class Paint : ToolStripRenderer
        {
            readonly ThemedMenu m;
            public Paint(ThemedMenu menu) => m = menu;

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(m.bg);

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using var pen = new Pen(m.line);
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var path = Rounded(r, 6);
                using var brush = new SolidBrush(m.hover);
                e.Graphics.FillPath(brush, path);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled ? m.text : m.muted;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // Своя галочка: системная рисуется в голубой рамке и не подходит к теме.
                var r = e.ImageRectangle;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(m.text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                float x = r.Left + r.Width / 2f, y = r.Top + r.Height / 2f;
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
