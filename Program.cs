using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace YaytsaCloud
{
    static class Program
    {
        public const string Name = "Яйца Cloud";
        public const string Repo = "fggtf24-cyber/yaytsa-cloud-desktop";
        public static readonly string Site = "https://yaytsacloud.art/";
        /// <summary>Прежний адрес: GitHub редиректит с него на Site, это не повод открывать браузер.</summary>
        public const string OldSite = "https://fggtf24-cyber.github.io/yaytsa-cloud/";

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

        // Просим запущенную копию закрыться самой, как через «Выйти» в трее. Если убить её сразу, встроенный браузер
        // не успеет аккуратно закрыть своё хранилище, и вход в аккаунт может потеряться. Kill — только если не закрылась.
        public const string QuitName = "YaytsaCloud.Quit";
        static void Quit()
        {
            try { using var e = EventWaitHandle.OpenExisting(QuitName); e.Set(); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Contains("--uninstall")) { Uninstall(); return; }
            if (args.Contains("--quit")) { Quit(); return; }

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
            // После самообновления в «Параметры → Приложения» должна быть новая версия.
            if (string.Equals(Path.GetFullPath(Exe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                try { using var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true); k?.SetValue("DisplayVersion", Version.ToString(3)); } catch { }
            Extract(Path.GetDirectoryName(Exe));
            // Только что обновились: Windows держит старую иконку exe в кэше (панель задач, ярлыки) — просим перечитать.
            if (File.Exists(Exe + ".old")) RefreshIcons();
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
                Quit();
            foreach (var p in Process.GetProcessesByName("YaytsaCloud").Where(p => p.Id != Process.GetCurrentProcess().Id))
                {
                    try { if (!p.WaitForExit(8000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
                }
                Directory.CreateDirectory(InstallDir);
                File.Copy(Exe, InstalledExe, true);
                Extract(InstallDir);
                Shortcut(StartMenu, InstalledExe);
                Shortcut(Desktop, InstalledExe);
                RefreshIcons();
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
            Quit();
            foreach (var p in Process.GetProcessesByName("YaytsaCloud").Where(p => p.Id != Process.GetCurrentProcess().Id))
            {
                try { if (!p.WaitForExit(8000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            }
            SetAutostart(false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            foreach (var f in new[] { StartMenu, Desktop }) try { File.Delete(f); } catch { }
            // Запущенный exe не может удалить сам себя: папки удалит cmd через пару секунд.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 >nul & rmdir /s /q \"{InstallDir}\" & rmdir /s /q \"{DataDir}\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }

        [DllImport("shell32.dll")] static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
        static void RefreshIcons() { try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }

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

        // Через IShellLinkW, а не WScript.Shell: тот пишет путь в ANSI, и на ПК с нерусской локалью
        // «Яйца Cloud.lnk» превращается в «???? Cloud.lnk» — сохранить не получается.
        public static void Shortcut(string lnk, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var s = (ShellLink.IShellLinkW)new ShellLink.CShellLink();
            s.SetPath(target);
            s.SetWorkingDirectory(Path.GetDirectoryName(target));
            s.SetIconLocation(target, 0);
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)s).Save(lnk, true);
        }
    }

    static class ShellLink
    {
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        public class CShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        public interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
            void Resolve(IntPtr hwnd, int fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        public static string Target(string lnk)
        {
            var s = (IShellLinkW)new CShellLink();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)s).Load(lnk, 0);
            var sb = new StringBuilder(1024);
            s.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
    }
}
