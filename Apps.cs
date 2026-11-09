using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace YaytsaCloud
{
    // Программа из каталога сайта.
    // silent — ключи тихой установки, если установщик самодельный и сам их не угадать.
    public class CatalogApp { public string id, name, file, silent; }

    // Что мы знаем об установленной программе.
    // kind: setup — ставилась установщиком, её видно в «Программы и компоненты»; portable — один exe в нашей папке;
    // shortcut — найдена по ярлыку в «Пуске» (программа без установщика, лежит где угодно): открыть можно, удалить — нет.
    // fp — отпечаток файла на сервере при установке. Поменялся — вышла новая версия.
    // local — как программа выглядела у нас на диске (версия в Windows, дата и размер exe). Поменялось без нас —
    // программу обновили в обход приложения (например, пока оно было закрыто): считаем её свежей.
    public class Installed
    {
        public string kind, key, exe, fp, local;
        public string Stamp()
        {
            var v = kind == "setup" ? Uninstaller.Version(key) : null;
            try { if (exe != null && File.Exists(exe)) { var f = new FileInfo(exe); v += "|" + f.LastWriteTimeUtc.Ticks + "|" + f.Length; } } catch { }
            return v ?? "";
        }
    }

    public class Apps
    {
        static readonly string StateFile = Path.Combine(Program.DataDir, "apps.json");
        static readonly string Downloads = Path.Combine(Program.DataDir, "downloads");
        static readonly string DefaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        static readonly string RootFile = Path.Combine(Program.DataDir, "install-dir.txt");

        // Папка, куда ставим новые программы: спрашиваем при первой установке, меняется в аккаунте на сайте.
        // Уже установленные остаются где были. Пока не выбрана, установщики ставят в свою обычную папку.
        public static bool RootChosen => File.Exists(RootFile);
        public static string Root
        {
            get { try { var d = File.ReadAllText(RootFile).Trim(); if (d.Length > 0) return d; } catch { } return DefaultRoot; }
            set
            {
                // Проверяем, что можем писать без прав администратора (Program Files — нет).
                var probe = Path.Combine(value, ".yaytsa-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(value);
                File.WriteAllText(probe, "");
                File.Delete(probe);
                Directory.CreateDirectory(Program.DataDir);
                File.WriteAllText(RootFile, value);
            }
        }

        static string DirName(CatalogApp a) => Regex.Replace(a.name, @"[^\w\- ]", "").Trim() is var n && n.Length > 0 ? n : a.id;
        static readonly string StartMenuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Program.Name);
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        public static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        static Apps() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("YaytsaCloud/" + Program.Version.ToString(3));

        public List<CatalogApp> Catalog = new List<CatalogApp>();
        Dictionary<string, Installed> state = new Dictionary<string, Installed>();
        readonly Dictionary<string, string> latest = new Dictionary<string, string>(); // id -> свежий отпечаток
        readonly Dictionary<string, double> busy = new Dictionary<string, double>();   // id -> прогресс 0..1, -1 — идёт установщик, -2 — удаление
        readonly Dictionary<string, DateTime> missUntil = new Dictionary<string, DateTime>(); // не нашли — не ищем заново 15 секунд

        public event Action Changed;
        public event Action<string> UpdateFound;

        public Apps()
        {
            try { state = Json.Deserialize<Dictionary<string, Installed>>(File.ReadAllText(StateFile)) ?? state; } catch { }
        }

        void Save()
        {
            Directory.CreateDirectory(Program.DataDir);
            File.WriteAllText(StateFile, Json.Serialize(state));
        }

        void Fire() => Changed?.Invoke();

        // Состояние для сайта: id -> { state: installed | update | busy, progress }.
        public Dictionary<string, object> Snapshot()
        {
            var r = new Dictionary<string, object>();
            foreach (var a in Catalog)
            {
                if (busy.TryGetValue(a.id, out var p)) r[a.id] = new { state = "busy", progress = p };
                else if (Find(a) is Installed i)
                    r[a.id] = new { state = i.fp != null && latest.TryGetValue(a.id, out var fp) && fp != null && fp != i.fp ? "update" : "installed", open = i.exe != null, removable = i.kind != "shortcut" };
            }
            return r;
        }

        public void SetCatalog(List<CatalogApp> apps)
        {
            var first = Catalog.Count == 0;
            Catalog = apps.Where(a => a?.id != null && Uri.TryCreate(a.file, UriKind.Absolute, out var u) && u.Scheme == "https").ToList();
            Fire();
            // Сайт прислал список впервые: сразу проверяем обновления, дальше — по таймеру.
            if (first && Catalog.Count > 0) _ = CheckUpdates();
        }

        // Установлена ли программа. Ищем по нашей записи, а если её нет — по имени в списке программ Windows:
        // так видно и то, что поставили раньше, ещё без Яйца Cloud.
        Installed Find(CatalogApp a)
        {
            if (state.TryGetValue(a.id, out var i))
            {
                if (i.kind == "setup" ? Uninstaller.Exists(i.key) : File.Exists(i.exe)) return i;
                state.Remove(a.id);
                Save();
            }
            // Поиск по реестру и ярлыкам небыстрый, а Snapshot зовут на каждый процент загрузки.
            if (missUntil.TryGetValue(a.id, out var until) && until > DateTime.Now) return null;
            var key = Uninstaller.ByName(a.name);
            var lnk = key == null ? Shortcuts.Find(a) : null;
            if (key == null && lnk == null) { missUntil[a.id] = DateTime.Now.AddSeconds(15); return null; }
            i = key != null ? new Installed { kind = "setup", key = key, exe = Uninstaller.Exe(key) } : new Installed { kind = "shortcut", exe = lnk };
            state[a.id] = i;
            Save();
            return i;
        }

        // Отпечаток файла: адрес после переадресаций (у релизов GitHub в нём номер файла) + ETag + размер.
        static async Task<string> Fingerprint(string url)
        {
            using var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            res.EnsureSuccessStatusCode();
            return res.RequestMessage.RequestUri.GetLeftPart(UriPartial.Path) + "|" + res.Headers.ETag?.Tag + "|" + res.Content.Headers.ContentLength;
        }

        public async Task CheckUpdates()
        {
            foreach (var a in Catalog.ToList())
            {
                var i = Find(a);
                if (i == null || busy.ContainsKey(a.id)) continue;
                string fp;
                try { fp = await Fingerprint(a.file); } catch { continue; }
                // Поставлено до нас, обновлено в обход нас или записано старой версией приложения (без local):
                // какую версию поставили, не знаем, считаем свежей.
                var stamp = i.Stamp();
                if (i.fp == null || i.local != stamp) { i.fp = fp; i.local = stamp; Save(); }
                var was = latest.TryGetValue(a.id, out var old) ? old : i.fp;
                latest[a.id] = fp;
                if (fp != i.fp && fp != was) UpdateFound?.Invoke(a.name);
            }
            Fire();
        }

        public bool IsInstalled(string id) => state.ContainsKey(id);

        public async Task Install(string id)
        {
            var a = Catalog.FirstOrDefault(x => x.id == id);
            if (a == null || busy.ContainsKey(id)) return;
            busy[id] = 0;
            missUntil.Remove(id);
            Fire();
            try
            {
                Directory.CreateDirectory(Downloads);
                string path, fp;
                using (var res = await Http.GetAsync(a.file, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    var uri = res.RequestMessage.RequestUri;
                    fp = uri.GetLeftPart(UriPartial.Path) + "|" + res.Headers.ETag?.Tag + "|" + res.Content.Headers.ContentLength;
                    var name = Path.GetFileName(new Uri(a.file).AbsolutePath);
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Это не установщик Windows.");
                    path = Path.Combine(Downloads, name);
                    var total = res.Content.Headers.ContentLength ?? 0;
                    using var src = await res.Content.ReadAsStreamAsync();
                    using var dst = File.Create(path);
                    var buf = new byte[81920];
                    long done = 0;
                    var last = DateTime.MinValue;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        done += n;
                        if (total > 0 && (DateTime.Now - last).TotalMilliseconds > 150)
                        {
                            last = DateTime.Now;
                            busy[id] = (double)done / total;
                            Fire();
                        }
                    }
                }

                busy[id] = -1;
                Fire();
                var setup = Regex.IsMatch(Path.GetFileName(path), "setup|install", RegexOptions.IgnoreCase) || path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
                // Обновление ставим туда же, где программа уже стоит; новую — в выбранную папку.
                state.TryGetValue(id, out var prev);
                var dir = prev == null && RootChosen ? Path.Combine(Root, DirName(a)) : null;
                var i = setup ? await RunSetup(a, path, dir) : InstallPortable(a, path, prev);
                i.fp = fp;
                i.local = i.Stamp();
                state[id] = i;
                latest[id] = fp;
                Save();
                try { File.Delete(path); } catch { }
            }
            finally
            {
                busy.Remove(id);
                Fire();
            }
        }

        async Task<Installed> RunSetup(CatalogApp a, string path, string dir)
        {
            var before = Uninstaller.All();
            // Как в Creative Cloud: ставим молча, без окна установщика. Не знаем ключей — показываем окно как есть.
            // Папку умеем передать только Inno Setup и NSIS; в своих ключах из каталога и в окне установщика — как решит он сам.
            var silent = !string.IsNullOrWhiteSpace(a.silent) ? a.silent : SilentArgs(path, dir);
            var start = path.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)
                ? new ProcessStartInfo("msiexec.exe", $"/i \"{path}\" /qn /norestart")
                : new ProcessStartInfo(path, silent ?? "");
            start.UseShellExecute = true;
            var p = Process.Start(start);
            await Task.Run(() => p.WaitForExit());
            // Новая или обновлённая запись в списке программ — наша. Иначе ищем по имени.
            var after = Uninstaller.All();
            var key = after.Where(kv => !before.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).FirstOrDefault(k => Uninstaller.Matches(k, a.name))
                ?? after.Keys.Where(k => !before.ContainsKey(k)).FirstOrDefault()
                ?? Uninstaller.ByName(a.name);
            if (key == null) throw new Exception("Установка не завершилась.");
            return new Installed { kind = "setup", key = key, exe = Uninstaller.Exe(key) };
        }

        // Тип установщика по метке в начале файла: у Inno Setup и NSIS тихие ключи стандартные.
        static string SilentArgs(string path, string dir)
        {
            var buf = new byte[4 << 20];
            int n;
            using (var f = File.OpenRead(path)) n = f.Read(buf, 0, buf.Length);
            var head = System.Text.Encoding.ASCII.GetString(buf, 0, n);
            if (head.Contains("Inno Setup")) return "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-" + (dir != null ? $" /DIR=\"{dir}\"" : "");
            if (head.Contains("Nullsoft")) return "/S" + (dir != null ? " /D=" + dir : ""); // /D= — последним и без кавычек, так требует NSIS
            return null;
        }

        Installed InstallPortable(CatalogApp a, string path, Installed prev)
        {
            var dir = prev?.kind == "portable" && prev.exe != null ? Path.GetDirectoryName(prev.exe) : Path.Combine(Root, DirName(a));
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, Path.GetFileName(path));
            try { File.Copy(path, exe, true); }
            catch (IOException) { throw new Exception($"Закройте {a.name} и попробуйте ещё раз."); }
            try { Program.Shortcut(Path.Combine(StartMenuDir, a.name + ".lnk"), exe); } catch { }
            return new Installed { kind = "portable", exe = exe };
        }

        public void Open(string id)
        {
            var a = Catalog.FirstOrDefault(x => x.id == id);
            var i = a == null ? null : Find(a);
            if (i?.exe == null) throw new Exception("Не нашёл, как запустить программу. Откройте её из меню «Пуск».");
            Process.Start(new ProcessStartInfo(i.exe) { WorkingDirectory = Path.GetDirectoryName(i.exe), UseShellExecute = true });
        }

        public async Task Remove(string id)
        {
            var a = Catalog.FirstOrDefault(x => x.id == id);
            var i = a == null ? null : Find(a);
            if (i == null || i.kind == "shortcut" || busy.ContainsKey(id)) return;
            missUntil.Remove(id);
            busy[id] = -2; // -2 — удаление
            Fire();
            try
            {
                if (i.kind == "portable")
                {
                    try { Directory.Delete(Path.GetDirectoryName(i.exe), true); }
                    catch (Exception) { throw new Exception($"Закройте {a.name} и попробуйте ещё раз."); }
                    try { File.Delete(Path.Combine(StartMenuDir, a.name + ".lnk")); } catch { }
                }
                else
                {
                    await Task.Run(() => Uninstaller.Run(i.key));
                    // Многие удалялки копируют себя во временную папку и сразу выходят: ждём, пока запись пропадёт.
                    for (var t = 0; t < 180 && Uninstaller.Exists(i.key); t++) await Task.Delay(1000);
                }
                if (i.kind == "portable" || !Uninstaller.Exists(i.key)) { state.Remove(id); Save(); }
            }
            finally
            {
                busy.Remove(id);
                Fire();
            }
        }
    }

    // Ярлыки в «Пуске»: так видно программы без установщика, например exe из «Загрузок» с ярлыком.
    // Подходит ярлык с именем программы или ярлык на exe с тем же именем файла, что и в каталоге.
    static class Shortcuts
    {
        static readonly string[] Dirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };

        static string Norm(string s) => Regex.Replace(s ?? "", @"[\s\-_.]", "").ToLowerInvariant();

        public static string Find(CatalogApp a)
        {
            var name = Norm(a.name);
            var file = Path.GetFileName(new Uri(a.file).AbsolutePath);
            foreach (var dir in Dirs.Where(Directory.Exists))
            {
                string[] links;
                try { links = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); } catch { continue; }
                foreach (var lnk in links)
                {
                    var byName = name.Length > 0 && Norm(Path.GetFileNameWithoutExtension(lnk)).StartsWith(name);
                    string target;
                    try { target = Target(lnk); } catch { continue; }
                    if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) continue;
                    if (Regex.IsMatch(Path.GetFileName(target), "^unins|uninstall|setup", RegexOptions.IgnoreCase)) continue;
                    if (byName || string.Equals(Path.GetFileName(target), file, StringComparison.OrdinalIgnoreCase)) return target;
                }
            }
            return null;
        }

        static string Target(string lnk) => ShellLink.Target(lnk);
    }

    // Список «Программы и компоненты»: ключи Uninstall в HKCU и HKLM (64 и 32 бита).
    // Ключ пишем строкой "HKCU|64|имя", чтобы хранить в json.
    static class Uninstaller
    {
        const string Path_ = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        static readonly (RegistryHive hive, RegistryView view, string tag)[] Roots =
        {
            (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU|64"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM|64"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM|32"),
        };

        static RegistryKey Open(string key)
        {
            var parts = key.Split(new[] { '|' }, 3);
            if (parts.Length != 3) return null;
            var root = Roots.FirstOrDefault(r => r.tag == parts[0] + "|" + parts[1]);
            if (root.tag == null) return null;
            return RegistryKey.OpenBaseKey(root.hive, root.view).OpenSubKey(Path_ + "\\" + parts[2]);
        }

        public static bool Exists(string key) { using var k = Open(key); return k != null; }

        public static string Version(string key) { using var k = Open(key); return k?.GetValue("DisplayVersion") as string; }

        // ключ -> имя + версия: по изменению видно, что установщик что-то поставил или обновил.
        public static Dictionary<string, string> All()
        {
            var r = new Dictionary<string, string>();
            foreach (var (hive, view, tag) in Roots)
            {
                using var root = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(Path_);
                if (root == null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var k = root.OpenSubKey(name);
                    var display = k?.GetValue("DisplayName") as string;
                    if (string.IsNullOrEmpty(display)) continue;
                    r[tag + "|" + name] = display + "|" + k.GetValue("DisplayVersion");
                }
            }
            return r;
        }

        static string Norm(string s) => Regex.Replace(s ?? "", @"[\s\-_.]", "").ToLowerInvariant();

        public static bool Matches(string key, string appName)
        {
            using var k = Open(key);
            var display = Norm(k?.GetValue("DisplayName") as string);
            var name = Norm(appName);
            return name.Length > 0 && display.StartsWith(name);
        }

        public static string ByName(string appName) => All().Keys.FirstOrDefault(k => Matches(k, appName));

        // Чем запускать: DisplayIcon обычно указывает на сам exe, иначе ищем exe в папке установки.
        public static string Exe(string key)
        {
            using var k = Open(key);
            if (k == null) return null;
            var icon = (k.GetValue("DisplayIcon") as string ?? "").Trim();
            icon = Regex.Replace(icon, @",-?\d+$", "").Trim('"');
            if (icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(icon) && !NotApp(icon)) return icon;
            var dir = (k.GetValue("InstallLocation") as string ?? "").Trim('"');
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*.exe").FirstOrDefault(f => !NotApp(f));
        }

        // Удалялки, установщики и обновлялки — не сама программа.
        static bool NotApp(string exe) => Regex.IsMatch(Path.GetFileName(exe), "^unins|uninstall|setup|update", RegexOptions.IgnoreCase);

        public static void Run(string key)
        {
            string cmd;
            using (var k = Open(key)) cmd = (k?.GetValue("QuietUninstallString") ?? k?.GetValue("UninstallString")) as string;
            if (string.IsNullOrWhiteSpace(cmd)) throw new Exception("У программы нет удаления. Удалите её в параметрах Windows.");
            cmd = cmd.Trim();
            string file, args;
            if (cmd.StartsWith("\""))
            {
                var end = cmd.IndexOf('"', 1);
                file = cmd.Substring(1, end - 1);
                args = cmd.Substring(end + 1).Trim();
            }
            else
            {
                var m = Regex.Match(cmd, @"^(.+?\.exe)\s*(.*)$", RegexOptions.IgnoreCase);
                file = m.Success ? m.Groups[1].Value : cmd;
                args = m.Success ? m.Groups[2].Value : "";
            }
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })?.WaitForExit();
        }
    }
}
