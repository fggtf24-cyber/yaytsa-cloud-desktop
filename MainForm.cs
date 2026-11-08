using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace YaytsaCloud
{
    public class MainForm : Form
    {
        readonly string site;
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(11, 11, 11) };
        readonly NotifyIcon tray;
        readonly Apps apps = new Apps();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly System.Windows.Forms.Timer updates = new System.Windows.Forms.Timer { Interval = 6 * 60 * 60 * 1000 };
        bool quitting, hintShown, startHidden;
        string balloonHref;

        public MainForm(string url, bool startInTray)
        {
            site = url;
            startHidden = startInTray;
            Text = Program.Name;
            Icon = Program.Icon();
            MinimumSize = new Size(420, 500);
            StartPosition = FormStartPosition.Manual;
            var area = Screen.PrimaryScreen.WorkingArea;
            Size = new Size(Math.Min(1280, area.Width - 80), Math.Min(860, area.Height - 80));
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
            BackColor = Color.FromArgb(11, 11, 11);
            Controls.Add(web);

            tray = new NotifyIcon { Icon = Icon, Text = Program.Name, Visible = true, ContextMenuStrip = TrayMenu() };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.BalloonTipClicked += (s, e) =>
            {
                ShowWindow();
                if (balloonHref != null) Send(new { type = "go", href = balloonHref });
            };

            apps.Changed += () => BeginInvoke((Action)(() => Send(new { type = "apps", apps = apps.Snapshot() })));
            apps.UpdateFound += name => BeginInvoke((Action)(() => Balloon("Вышло обновление", name + ": откройте карточку и нажмите «Обновить».", "")));
            updates.Tick += async (s, e) => { await apps.CheckUpdates(); await SelfUpdate(); };
            updates.Start();

            // Второй запуск (ярлык, «Пуск») показывает уже открытое окно.
            new Thread(() => { while (Program.ShowSignal.WaitOne()) BeginInvoke((Action)ShowWindow); }) { IsBackground = true }.Start();

            Load += async (s, e) => await Init();
        }

        protected override void SetVisibleCore(bool value)
        {
            // При автозапуске окно не показываем, сразу в трей. Хэндл всё равно создаём, чтобы сайт загрузился.
            if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; OnLoad(EventArgs.Empty); }
            base.SetVisibleCore(value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Тема с прошлого запуска, чтобы окно сразу открылось в нужных цветах.
            try { var t = File.ReadAllText(ThemeFile).Split('|'); ApplyTheme(ColorTranslator.FromHtml(t[0]), t[1] == "light"); }
            catch { ApplyTheme(Color.FromArgb(11, 11, 11), false); }
        }

        static readonly string ThemeFile = Path.Combine(Program.DataDir, "theme.txt");

        // Заголовок окна в цвет шапки сайта: на Windows 11 красим его, рамку и текст, на Windows 10 хотя бы тёмный режим.
        void ApplyTheme(Color bg, bool light)
        {
            BackColor = bg;
            web.DefaultBackgroundColor = bg;
            menu.SetTheme(light);
            int dark = light ? 0 : 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            int caption = ColorTranslator.ToWin32(bg);
            int text = ColorTranslator.ToWin32(light ? Color.FromArgb(17, 17, 17) : Color.FromArgb(240, 240, 240));
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(Handle, 34, ref caption, sizeof(int)); // DWMWA_BORDER_COLOR
            DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));    // DWMWA_TEXT_COLOR
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        bool loaded;
        async Task Init()
        {
            if (loaded) return;
            loaded = true;
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Program.DataDir, "web"));
            await web.EnsureCoreWebView2Async(env);
            var core = web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = site != Program.Site;
            core.Settings.IsZoomControlEnabled = true;
            // По этой строке сайт понимает, что открыт в приложении.
            core.Settings.UserAgent += " YaytsaCloud/" + Program.Version.ToString(3);

            core.NavigationStarting += (s, e) =>
            {
                if (Inside(e.Uri) || Auth(e.Uri)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            core.NewWindowRequested += (s, e) =>
            {
                // Окно входа через Google открываем внутри, остальные ссылки — в обычном браузере.
                if (Auth(e.Uri)) return;
                e.Handled = true;
                if (Inside(e.Uri)) core.Navigate(e.Uri);
                else OpenExternal(e.Uri);
            };
            core.WebMessageReceived += (s, e) => OnMessage(e.WebMessageAsJson);
            core.Navigate(site);
            await SelfUpdate();
        }

        bool Inside(string uri) => uri.StartsWith(site, StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:") || uri.StartsWith("data:") || uri.StartsWith("blob:");

        static bool Auth(string uri) =>
            Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
            (u.Host == "accounts.google.com" || u.Host.EndsWith(".firebaseapp.com") || u.Host == "apis.google.com");

        static void OpenExternal(string uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http" || u.Scheme == "mailto"))
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }

        void Send(object msg)
        {
            if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsJson(json.Serialize(msg));
        }

#pragma warning disable CS0649 // поля заполняет JavaScriptSerializer
        class Message { public string type, id, title, body, href, bg; public bool light; public List<CatalogApp> apps; }

        async void OnMessage(string raw)
        {
            Message m;
            try { m = json.Deserialize<Message>(raw); } catch { return; }
            if (m == null) return;
            try
            {
                switch (m.type)
                {
                    case "theme":
                        if (!System.Text.RegularExpressions.Regex.IsMatch(m.bg ?? "", "^#[0-9A-Fa-f]{6}$")) break;
                        var color = ColorTranslator.FromHtml(m.bg);
                        ApplyTheme(color, m.light);
                        File.WriteAllText(ThemeFile, ColorTranslator.ToHtml(color) + "|" + (m.light ? "light" : "dark"));
                        break;
                    case "catalog":
                        apps.SetCatalog(m.apps ?? new List<CatalogApp>());
                        break;
                    case "install":
                        await apps.Install(m.id);
                        break;
                    case "open":
                        apps.Open(m.id);
                        break;
                    case "remove":
                        await apps.Remove(m.id);
                        break;
                    case "google":
                        var cred = await GoogleLogin.Run(site);
                        if (cred == null) break;
                        ShowWindow();
                        Send(new { type = "google-cred", idToken = cred["id"], accessToken = cred.TryGetValue("at", out var at) ? at : "" });
                        break;
                    case "notify":
                        // Пока окно перед глазами, сайт сам показывает уведомления.
                        if (!Visible || WindowState == FormWindowState.Minimized || !ContainsFocus) Balloon(m.title, m.body, m.href);
                        break;
                }
            }
            catch (Exception e)
            {
                Send(new { type = "error", id = m.id, text = e.Message });
            }
        }

        void Balloon(string title, string body, string href)
        {
            balloonHref = href;
            tray.ShowBalloonTip(5000, Clip(title, 60) ?? Program.Name, Clip(body, 200) ?? " ", ToolTipIcon.None);
        }

        static string Clip(string s, int n) => string.IsNullOrWhiteSpace(s) ? null : s.Length > n ? s.Substring(0, n - 1) + "…" : s;

        readonly ThemedMenu menu = new ThemedMenu();

        ContextMenuStrip TrayMenu()
        {
            menu.Items.Add("Открыть", null, (s, e) => ShowWindow());
            var auto = new ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true };
            menu.Opening += (s, e) => auto.Checked = Program.Autostart;
            auto.Click += (s, e) => Program.SetAutostart(auto.Checked);
            menu.Items.Add(auto);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Выйти", null, (s, e) => { quitting = true; Close(); });
            menu.SetTheme(false);
            return menu;
        }

        void ShowWindow()
        {
            startHidden = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Крестик прячет в трей: уведомления и обновления продолжают приходить.
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                if (!hintShown)
                {
                    hintShown = true;
                    Balloon(Program.Name + " работает в трее", "Сюда придут сообщения и обновления программ. Выйти можно через значок у часов.", null);
                }
                return;
            }
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        // Обновление самого приложения: свежий exe из последнего релиза на GitHub подменяет текущий.
        // Запущенный exe нельзя перезаписать, но можно переименовать — новая версия заработает со следующего запуска.
        bool selfUpdated;
        async Task SelfUpdate()
        {
            if (selfUpdated || site != Program.Site) return;
            try
            {
                var body = await Apps.Http.GetStringAsync($"https://api.github.com/repos/{Program.Repo}/releases/latest");
                var rel = json.Deserialize<Dictionary<string, object>>(body);
                if (!Version.TryParse(((string)rel["tag_name"]).TrimStart('v'), out var v) || v <= Program.Version) return;
                var asset = ((System.Collections.ArrayList)rel["assets"]).Cast<Dictionary<string, object>>()
                    .FirstOrDefault(a => (string)a["name"] == "YaytsaCloud.exe");
                if (asset == null) return;
                var exe = Program.Exe;
                var bytes = await Apps.Http.GetByteArrayAsync((string)asset["browser_download_url"]);
                File.WriteAllBytes(exe + ".new", bytes);
                try { File.Delete(exe + ".old"); } catch { }
                File.Move(exe, exe + ".old");
                File.Move(exe + ".new", exe);
                selfUpdated = true;
                Balloon($"{Program.Name} {v.ToString(3)}", "Обновление скачано и включится при следующем запуске.", null);
            }
            catch { }
        }
    }
}
