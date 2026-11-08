using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace YaytsaCloud
{
    // Вход через Google в обычном браузере: там пользователь уже вошёл в Google, а внутри WebView Google вход не пускает.
    // Открываем страницу сайта #app-login/p<порт>/<state>. После выбора аккаунта она уводит браузер на
    // http://127.0.0.1:<порт>/cb#… — данные во #фрагменте в сеть не уходят, страница /cb отдаёт их нам POST-запросом.
    public static class GoogleLogin
    {
        static TcpListener current;

        // open — чем открыть ссылку; по умолчанию браузер по умолчанию (в проверке подставляем свой).
        public static async Task<Dictionary<string, string>> Run(string site, Action<string> open = null)
        {
            try { current?.Stop(); } catch { } // прошлый незаконченный вход
            var state = Hex(16);
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            current = listener;
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var url = $"{site}#app-login/p{port}/{state}";
            if (open != null) open(url);
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            var deadline = DateTime.Now.AddMinutes(10);
            try
            {
                while (DateTime.Now < deadline)
                {
                    var accept = listener.AcceptTcpClientAsync();
                    if (await Task.WhenAny(accept, Task.Delay(deadline - DateTime.Now)) != accept) break;
                    using var client = await accept;
                    var result = await Handle(client, state);
                    if (result != null) return result;
                }
                return null;
            }
            catch (ObjectDisposedException) { return null; } // начали новый вход
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        const string Page = @"<!doctype html><meta charset=utf-8><title>Яйца Cloud</title>
<style>body{margin:0;height:100vh;display:grid;place-items:center;background:#0B0B0B;color:#F0F0F0;font:16px Segoe UI,sans-serif}</style>
<p id=t>Передаём вход в приложение…</p>
<script>
fetch('/done', { method: 'POST', body: location.hash.slice(1) })
  .then(r => { t.textContent = r.ok ? 'Готово. Вернитесь в приложение Яйца Cloud, эту вкладку можно закрыть.' : 'Ссылка устарела. Нажмите «Продолжить с Google» в приложении ещё раз.'; history.replaceState(null, '', '/cb'); })
  .catch(() => { t.textContent = 'Приложение закрыто. Откройте Яйца Cloud и войдите ещё раз.'; });
</script>";

        static async Task<Dictionary<string, string>> Handle(TcpClient client, string state)
        {
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            var reader = new StreamReader(stream, Encoding.UTF8);
            var first = await reader.ReadLineAsync() ?? "";
            int length = 0;
            string line;
            while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line.Substring(15).Trim(), out length);

            if (first.StartsWith("GET /cb ")) { await Reply(stream, 200, "text/html; charset=utf-8", Page); return null; }
            if (!first.StartsWith("POST /done ") || length <= 0 || length > 20000) { await Reply(stream, 404, "text/plain", "not found"); return null; }

            var buf = new char[length];
            int read = 0, n;
            while (read < length && (n = await reader.ReadAsync(buf, read, length - read)) > 0) read += n;
            var form = new Dictionary<string, string>();
            foreach (var pair in new string(buf, 0, read).Split('&'))
            {
                var kv = pair.Split(new[] { '=' }, 2);
                if (kv.Length == 2) form[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }
            if (!form.TryGetValue("state", out var s) || s != state || !form.ContainsKey("id")) { await Reply(stream, 403, "text/plain", "bad state"); return null; }
            await Reply(stream, 200, "text/plain", "ok");
            return form;
        }

        static async Task Reply(Stream s, int code, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
            await s.WriteAsync(head, 0, head.Length);
            await s.WriteAsync(bytes, 0, bytes.Length);
        }

        static string Hex(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }
    }
}
