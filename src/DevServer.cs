#if DEBUG
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ResourceLens;

/// <summary>Debug-only: serves the UI and the data API on http://localhost:5178 for browser previews.</summary>
internal sealed class DevServer : IDisposable
{
    readonly HttpListener _http = new();
    readonly Api _api;
    readonly TrayApp _app;
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DevServer(TrayApp app)
    {
        _app = app;
        _api = new Api(app);
        string port = Environment.GetEnvironmentVariable("RESOURCELENS_DEVPORT") is { Length: > 0 } dp ? dp : "5178";
        _http.Prefixes.Add($"http://localhost:{port}/");
        _http.Start();
        Task.Run(Loop);
    }

    async Task Loop()
    {
        while (_http.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _http.GetContextAsync(); } catch { return; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            string path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath.TrimStart('/'));
            if (path == "") path = "index.html";
            if (path == "api" && ctx.Request.HttpMethod == "POST")
            {
                using var sr = new StreamReader(ctx.Request.InputStream);
                var msg = JsonDocument.Parse(sr.ReadToEnd()).RootElement;
                string method = msg.GetProperty("method").GetString()!;
                var p = msg.TryGetProperty("params", out var pp) ? pp : default;
                object payload;
                try { payload = new { result = _api.Query(method, p) }; }
                catch (Exception ex) { payload = new { error = ex.Message }; }
                Write(res, "application/json", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, Json)));
                return;
            }
            if (path == "fileicon")
            {
                var png = IconUtil.Png(ctx.Request.QueryString["p"]);
                if (png is null) { res.StatusCode = 404; res.Close(); return; }
                Write(res, "image/png", png);
                return;
            }
            if (path.StartsWith("icon/") && long.TryParse(path[5..], out long nid))
            {
                var icon = _app.Store.GetIcon(nid);
                if (icon is null) { res.StatusCode = 404; res.Close(); return; }
                Write(res, "image/png", icon);
                return;
            }
            // Serve straight from the source folder when present, so UI edits need no rebuild.
            string disk = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "ui", path));
            using var s = File.Exists(disk) && !path.Contains("..") ? File.OpenRead(disk) : UiResources.Open(path);
            if (s is null) { res.StatusCode = 404; res.Close(); return; }
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            string mime = Path.GetExtension(path) switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css; charset=utf-8", ".woff2" => "font/woff2", _ => "application/octet-stream",
            };
            Write(res, mime, ms.ToArray());
        }
        catch (Exception ex) { Log.Write("devserver", ex); try { res.StatusCode = 500; res.Close(); } catch { } }
    }

    static void Write(HttpListenerResponse res, string mime, byte[] body)
    {
        res.ContentType = mime;
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = body.Length;
        res.OutputStream.Write(body);
        res.Close();
    }

    public void Dispose() => _http.Close();
}
#endif
