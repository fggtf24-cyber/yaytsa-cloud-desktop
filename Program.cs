using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace YaytsaCloud
{
    static class Program
    {
        public const string Name = "Яйца Cloud";
        public const string Repo = "fggtf24-cyber/yaytsa-cloud-desktop";
        public static readonly string Site = "https://fggtf24-cyber.github.io/yaytsa-cloud/";

        static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public static readonly string InstallDir = Path.Combine(Local, "Programs", "YaytsaCloud");
        public static readonly string DataDir = Path.Combine(Local, "YaytsaCloud");
        public static readonly string InstalledExe = Path.Combine(InstallDir, "YaytsaCloud.exe");
        public static string Exe => Assembly.GetExecutingAssembly().Location;
        public static Version Version => Assembly.GetExecutingAssembly().GetName().Version;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YaytsaCloud";
        static readonly string StartMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
        static readonly string Desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

        public static EventWaitHandle ShowSignal;

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--uninstall")) { Uninstall(); return; }

            // Первый запуск скачанного файла: ставим себя в профиль пользователя (без прав администратора) и запускаемся оттуда.
            if (!string.Equals(Path.GetFullPath(Exe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase) && !args.Contains("--portable"))
            {
                if (Install()) Process.Start(InstalledExe);
                return;
            }

            // Второй экземпляр только просит первый показать окно.
            ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "YaytsaCloud.Show", out bool first);
            if (!first) { ShowSignal.Set(); return; }

            Directory.CreateDirectory(DataDir);
            Extract(Path.GetDirectoryName(Exe));
            try { File.Delete(Exe + ".old"); } catch { }

            var url = args.SkipWhile(a => a != "--url").Skip(1).FirstOrDefault() ?? Site;
            Run(url, args.Contains("--tray"));
        }

        // Отдельный метод: типы WebView2 подгружаются только после того, как библиотеки распакованы.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Run(string url, bool tray)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(url, tray));
        }

        static void Extract(string dir)
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll" })
            {
                using var src = asm.GetManifestResourceStream(name);
                var path = Path.Combine(dir, name);
                if (File.Exists(path) && new FileInfo(path).Length == src.Length) continue;
                using var dst = File.Create(path);
                src.CopyTo(dst);
            }
        }

        public static System.Drawing.Icon Icon()
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico");
            return new System.Drawing.Icon(s);
        }

        static bool Install()
        {
            try
            {
                // Если уже запущена установленная копия, закрываем её, иначе exe не перезаписать.
                foreach (var p in Process.GetProcessesByName("YaytsaCloud").Where(p => p.Id != Process.GetCurrentProcess().Id))
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
                Directory.CreateDirectory(InstallDir);
                File.Copy(Exe, InstalledExe, true);
                Extract(InstallDir);
                Shortcut(StartMenu, InstalledExe);
                Shortcut(Desktop, InstalledExe);
                SetAutostart(true);
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
                {
                    k.SetValue("DisplayName", Name);
                    k.SetValue("DisplayVersion", Version.ToString(3));
                    k.SetValue("Publisher", Name);
                    k.SetValue("DisplayIcon", InstalledExe);
                    k.SetValue("InstallLocation", InstallDir);
                    k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                    k.SetValue("NoModify", 1);
                    k.SetValue("NoRepair", 1);
                }
                return true;
            }
            catch (Exception e)
            {
                MessageBox.Show("Не получилось установить: " + e.Message, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        static void Uninstall()
        {
            if (MessageBox.Show($"Удалить {Name}? Программы, установленные через него, останутся.", Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var p in Process.GetProcessesByName("YaytsaCloud").Where(p => p.Id != Process.GetCurrentProcess().Id))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
            SetAutostart(false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            foreach (var f in new[] { StartMenu, Desktop }) try { File.Delete(f); } catch { }
            // Запущенный exe не может удалить сам себя: папки удалит cmd через пару секунд.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 >nul & rmdir /s /q \"{InstallDir}\" & rmdir /s /q \"{DataDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }

        public static bool Autostart
        {
            get { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("YaytsaCloud") != null; }
        }

        public static void SetAutostart(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("YaytsaCloud", $"\"{InstalledExe}\" --tray");
            else k.DeleteValue("YaytsaCloud", false);
        }

        public static void Shortcut(string lnk, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            dynamic s = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            s.TargetPath = target;
            s.WorkingDirectory = Path.GetDirectoryName(target);
            s.IconLocation = target + ",0";
            s.Save();
        }
    }
}
