using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace YaytsaCloud
{
    // Своё всплывающее уведомление, как в Telegram: справа внизу, поверх окон, в цветах сайта.
    // Не зависит от настроек уведомлений Windows; выключается галочкой в меню трея.
    class Popup : Form
    {
        static readonly List<Popup> open = new List<Popup>();
        const int W = 360, Gap = 10, Life = 6000;

        readonly Action click;
        readonly Timer life = new Timer { Interval = Life };
        readonly Timer fade = new Timer { Interval = 15 };
        bool closing;

        public static void Notify(string title, string body, bool light, Icon icon, Action onClick)
        {
            // Не больше трёх сразу: самое старое уходит.
            if (open.Count >= 3) open[0].Dismiss();
            var p = new Popup(title, body, light, icon, onClick);
            open.Add(p);
            Stack();
            p.Opacity = 0;
            p.Show();
            p.fade.Start();
        }

        // Стопкой снизу вверх над панелью задач, новые снизу.
        static void Stack()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            int y = area.Bottom - Gap;
            for (int i = open.Count - 1; i >= 0; i--)
            {
                var p = open[i];
                y -= p.Height;
                p.Location = new Point(area.Right - p.Width - Gap, y);
                y -= Gap;
            }
        }

        Popup(string title, string body, bool light, Icon icon, Action onClick)
        {
            click = onClick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            var bg = light ? Color.White : Color.FromArgb(35, 35, 35);
            var text = light ? Color.FromArgb(17, 17, 17) : Color.FromArgb(240, 240, 240);
            var muted = light ? Color.FromArgb(110, 110, 110) : Color.FromArgb(189, 189, 189);
            BackColor = bg;
            Cursor = Cursors.Hand;

            var pic = new PictureBox { Image = new Icon(icon, 40, 40).ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(16, 16, 40, 40) };
            var head = new Label { Text = Program.Name, ForeColor = muted, Font = new Font("Segoe UI", 8.5f), AutoSize = false, Bounds = new Rectangle(68, 12, W - 68 - 36, 18) };
            var t = new Label { Text = Clip(title, 80), ForeColor = text, Font = new Font("Segoe UI Semibold", 10.5f), AutoSize = false, AutoEllipsis = true, Bounds = new Rectangle(68, 30, W - 68 - 16, 22) };
            var b = new Label { Text = Clip(body, 160), ForeColor = muted, Font = new Font("Segoe UI", 9.5f), AutoSize = false, AutoEllipsis = true, MaximumSize = new Size(W - 68 - 16, 40), Location = new Point(68, 54) };
            b.Size = string.IsNullOrEmpty(b.Text) ? Size.Empty : TextRenderer.MeasureText(b.Text, b.Font, new Size(W - 68 - 16, 40), TextFormatFlags.WordBreak);
            if (b.Height > 40) b.Height = 40;
            var x = new Label { Text = "✕", ForeColor = muted, Font = new Font("Segoe UI", 9f), AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(W - 34, 8, 26, 26), Cursor = Cursors.Default };
            Controls.AddRange(new Control[] { pic, head, t, b, x });
            ClientSize = new Size(W, Math.Max(72, b.Bottom + 14));

            foreach (Control c in Controls)
            {
                if (c == x) continue;
                c.Click += (s, e) => Open();
                c.MouseEnter += (s, e) => life.Stop();
                c.MouseLeave += (s, e) => life.Start();
            }
            Click += (s, e) => Open();
            MouseEnter += (s, e) => life.Stop();
            MouseLeave += (s, e) => life.Start();
            x.Click += (s, e) => Dismiss();
            x.MouseEnter += (s, e) => { life.Stop(); x.ForeColor = text; };
            x.MouseLeave += (s, e) => { life.Start(); x.ForeColor = muted; };

            life.Tick += (s, e) => Dismiss();
            fade.Tick += (s, e) =>
            {
                Opacity = Math.Max(0, Math.Min(1, Opacity + (closing ? -0.1 : 0.12)));
                if (!closing && Opacity >= 1) { fade.Stop(); life.Start(); }
                if (closing && Opacity <= 0) { fade.Stop(); Close(); }
            };
            FormClosed += (s, e) => { open.Remove(this); life.Dispose(); fade.Dispose(); Stack(); };
        }

        void Open()
        {
            click?.Invoke();
            Dismiss();
        }

        void Dismiss()
        {
            if (closing) return;
            closing = true;
            life.Stop();
            fade.Start();
        }

        static string Clip(string s, int n) => string.IsNullOrWhiteSpace(s) ? "" : s.Length > n ? s.Substring(0, n - 1) + "…" : s;

        // Не забираем фокус у того, в чём человек сейчас работает.
        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: не в Alt+Tab
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; // скругление и рамка от Windows 11, как у меню в трее
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
            int border = ColorTranslator.ToWin32(BackColor == Color.White ? Color.FromArgb(225, 225, 225) : Color.FromArgb(58, 58, 58));
            DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
