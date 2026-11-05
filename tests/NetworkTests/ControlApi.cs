using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetTrans.Kernel;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Transfer;

namespace NetworkTests;

internal sealed partial class Program
{
    // ── 24. 本地控制 API（P0-1：TcpListener REST，仅环回 403，端口回退） ──

    private static async Task ControlApiBasicsAsync(TestServer server)
    {
        server.Map("api/file.zip", server.RangeFile(Payload(64 * 1024)));
        using var kernel = new RealDownloadKernel(new KernelOptions());
        using var api = new LocalControlApi(kernel, preferredPort: 45010);
        api.Start();
        using var http = new HttpClient();

        // POST /tasks → 201 {id}
        var body = JsonSerializer.Serialize(new
        {
            url = server.Base + "api/file.zip",
            saveTo = TempDir(),
            category = "",
            connections = 2,
            priority = 1,
            startNow = true,
        });
        var post = await http.PostAsync(api.BaseUrl + "tasks",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Equal(HttpStatusCode.Created, post.StatusCode, "POST /tasks 应 201");
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        // GET /tasks → 列表含新任务
        var list = await http.GetAsync(api.BaseUrl + "tasks");
        Equal(HttpStatusCode.OK, list.StatusCode, "GET /tasks 应 200");
        var first = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement.EnumerateArray().First();
        Equal(id, first.GetProperty("id").GetInt32(), "列表应含新任务 id");
        Equal(server.Base + "api/file.zip", first.GetProperty("url").GetString(), "列表应含任务 URL");

        // GET /tasks/{id} → 200；未知 → 404
        var single = await http.GetAsync($"{api.BaseUrl}tasks/{id}");
        Equal(HttpStatusCode.OK, single.StatusCode, "GET /tasks/{id} 应 200");
        var missing = await http.GetAsync($"{api.BaseUrl}tasks/99999");
        Equal(HttpStatusCode.NotFound, missing.StatusCode, "未知任务应 404");

        // DELETE /tasks/{id} → 200，列表不再包含（等 TaskRemoved 事件传播）
        var del = await http.DeleteAsync($"{api.BaseUrl}tasks/{id}");
        Equal(HttpStatusCode.OK, del.StatusCode, "DELETE 应 200");
        bool removed = false;
        for (int i = 0; i < 100 && !removed; i++)
        {
            await Task.Delay(50);
            var current = JsonDocument.Parse(await http.GetStringAsync(api.BaseUrl + "tasks")).RootElement;
            removed = current.EnumerateArray().All(t => t.GetProperty("id").GetInt32() != id);
        }
        That(removed, "删除后列表不应再含该任务");
    }

    private static async Task ControlApiTrustAsync(TestServer server)
    {
        // 来源判定策略（单元）
        That(LocalControlApi.IsLoopbackSource("127.0.0.1"), "127.0.0.1 应可信");
        That(LocalControlApi.IsLoopbackSource("127.9.9.9"), "整个 127/8 应可信");
        That(LocalControlApi.IsLoopbackSource("::1"), "IPv6 环回应可信");
        That(!LocalControlApi.IsLoopbackSource("10.78.95.229"), "局域网地址不可信");
        That(!LocalControlApi.IsLoopbackSource("not-an-ip"), "非法地址不可信");

        using var kernel = new RealDownloadKernel(new KernelOptions());
        using var api = new LocalControlApi(kernel, preferredPort: 45110);
        api.Start();
        using var http = new HttpClient();

        // 环回来源放行
        var ok = await http.GetAsync(api.BaseUrl + "tasks");
        Equal(HttpStatusCode.OK, ok.StatusCode, "环回来源应放行");

        // 非环回来源（本机局域网 IP 直连）→ 403
        var external = NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (external is null)
        {
            Console.WriteLine("    [注] 本机无非环回 IPv4，403 集成断言跳过（策略已由单元断言覆盖）");
            return;
        }

        using var externalClient = new HttpClient { BaseAddress = new Uri($"http://{external}:{api.Port}/") };
        var forbidden = await externalClient.GetAsync("tasks");
        Equal(HttpStatusCode.Forbidden, forbidden.StatusCode, "非环回来源应 403");
    }

    private static async Task ControlApiPortAndToggleAsync(TestServer server)
    {
        server.Map("api/t.zip", server.RangeFile(Payload(64 * 1024)));
        using var kernel = new RealDownloadKernel(new KernelOptions { MaxSimultaneous = 1 });
        using var api = new LocalControlApi(kernel, preferredPort: 45210);
        api.Start();

        // 端口回退：同首选端口再起一个 → 必须落到相邻端口
        using var kernel2 = new RealDownloadKernel(new KernelOptions());
        using var api2 = new LocalControlApi(kernel2, preferredPort: 45210);
        api2.Start();
        That(api2.Port != api.Port && api2.Port is > 45210 and <= 45220, "首选端口被占应回退到相邻端口");

        using var http = new HttpClient();
        var task = kernel.Add(new NewDownloadRequest(server.Base + "api/t.zip", TempDir(), "", 2, TaskPriority.Normal, StartNow: true));
        await WaitAsync(() => task.Status == DownloadStatus.Downloading, "前置：任务应进入下载", 30_000);

        // pause → 暂停；重复 pause → 409
        var pause = await http.PostAsync($"{api.BaseUrl}tasks/{task.Id}/pause", content: null);
        Equal(HttpStatusCode.OK, pause.StatusCode, "pause 应 200");
        await WaitAsync(() => task.Status == DownloadStatus.Paused, "pause 后应暂停", 10_000);
        var pauseAgain = await http.PostAsync($"{api.BaseUrl}tasks/{task.Id}/pause", content: null);
        Equal(HttpStatusCode.Conflict, pauseAgain.StatusCode, "已暂停再 pause 应 409");

        // resume → 恢复；重复 resume → 409
        var resume = await http.PostAsync($"{api.BaseUrl}tasks/{task.Id}/resume", content: null);
        Equal(HttpStatusCode.OK, resume.StatusCode, "resume 应 200");
        await WaitAsync(() => task.Status is DownloadStatus.Queued or DownloadStatus.Downloading, "resume 后应恢复", 10_000);
        var resumeAgain = await http.PostAsync($"{api.BaseUrl}tasks/{task.Id}/resume", content: null);
        Equal(HttpStatusCode.Conflict, resumeAgain.StatusCode, "运行中再 resume 应 409");

        // 未知任务的操作 → 404
        var missingPause = await http.PostAsync($"{api.BaseUrl}tasks/99999/pause", content: null);
        Equal(HttpStatusCode.NotFound, missingPause.StatusCode, "未知任务 pause 应 404");
    }

    // ── 25. 浏览器接管入口（P0-2：nettrans:// 解析 + 握手 token） ─────────
    // 协议注册本体在 OS/UI 侧（文档化，不碰 WinUI）；内核保证解析与 token 校验。

    private static Task NettransParseUnit()
    {
        var sha = new string('a', 64);
        var ok = NettransLink.Parse($"nettrans://add?url=https%3A%2F%2Fcdn.example%2Fa%20b.zip&sha256={sha}&token=tok123", out var error);
        That(ok is not null, $"合法链接应解析：{error}");
        Equal("https://cdn.example/a b.zip", ok!.Url, "url 应解码还原");
        Equal(sha, ok.Sha256, "sha256 应保留");
        Equal("tok123", ok.Token, "token 应提取");
        That(NettransLink.Parse("NETTRANS://ADD?url=https://x/y.zip&token=t", out _) is not null, "scheme/host 大小写不敏感");

        That(NettransLink.Parse("https://not-nettrans/add?url=x&token=t", out error) is null, "错误 scheme 拒绝");
        That(NettransLink.Parse("nettrans://open?url=x&token=t", out error) is null, "未知动作拒绝");
        That(NettransLink.Parse("nettrans://add?sha256=x&token=t", out error) is null && error!.Contains("url"), "缺 url 拒绝");
        That(NettransLink.Parse("nettrans://add?url=ftp://f/x.zip&token=t", out _) is not null, "ftp 直链允许");
        That(NettransLink.Parse("nettrans://add?url=x.zip&token=t", out error) is null, "相对 url 拒绝");
        That(NettransLink.Parse("nettrans://add?url=https://x/y.zip", out error) is null && error!.Contains("token"), "缺 token 拒绝");
        That(NettransLink.Parse($"nettrans://add?url=https://x/y.zip&sha256=ZZ&token=t", out error) is null && error!.Contains("sha256"), "非法 sha256 拒绝");
        That(NettransLink.Parse($"nettrans://add?url=https://x/y.zip&sha256={new string('g', 63)}&token=t", out error) is null && error!.Contains("sha256"), "63 位 sha256 拒绝");

        return Task.CompletedTask;
    }

    private static async Task ControlApiNettransAsync(TestServer server)
    {
        server.Map("net/f.zip", server.RangeFile(Payload(64 * 1024)));
        using var kernel = new RealDownloadKernel(new KernelOptions());
        using var api = new LocalControlApi(kernel, preferredPort: 45310);
        api.Start();
        using var http = new HttpClient();

        // 合法链接（token 匹配）→ 201，参数完整入队
        var link = $"nettrans://add?url={Uri.EscapeDataString(server.Base + "net/f.zip")}" +
                   $"&sha256={new string('B', 64)}&token={api.Token}&to={Uri.EscapeDataString(TempDir())}";
        var body = JsonSerializer.Serialize(new { link });
        var post = await http.PostAsync(api.BaseUrl + "links",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Equal(HttpStatusCode.Created, post.StatusCode, "合法链接应 201");
        var id = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        var single = await http.GetAsync($"{api.BaseUrl}tasks/{id}");
        var task = JsonDocument.Parse(await single.Content.ReadAsStringAsync()).RootElement;
        Equal(server.Base + "net/f.zip", task.GetProperty("url").GetString(), "入队 URL 应与链接一致");
        Equal(new string('b', 64), task.GetProperty("expectedHash").GetString(), "sha256 应大写归一为小写入 ExpectedHash");

        // 无效 token → 403（防恶意页面伪造 nettrans:// 调用）
        var bad = link.Replace($"token={api.Token}", "token=deadbeef");
        var forbidden = await http.PostAsync(api.BaseUrl + "links",
            new StringContent(JsonSerializer.Serialize(new { link = bad }), Encoding.UTF8, "application/json"));
        Equal(HttpStatusCode.Forbidden, forbidden.StatusCode, "无效 token 应 403");

        // 非 nettrans 链接 → 400
        var wrong = await http.PostAsync(api.BaseUrl + "links",
            new StringContent(JsonSerializer.Serialize(new { link = "https://example.com/x" }), Encoding.UTF8, "application/json"));
        Equal(HttpStatusCode.BadRequest, wrong.StatusCode, "非 nettrans 链接应 400");

        // 缺保存目录 → 400（内核不假设默认目录，UI 侧可预填）
        var noDir = $"nettrans://add?url=https://x/y.zip&token={api.Token}";
        var missing = await http.PostAsync(api.BaseUrl + "links",
            new StringContent(JsonSerializer.Serialize(new { link = noDir }), Encoding.UTF8, "application/json"));
        Equal(HttpStatusCode.BadRequest, missing.StatusCode, "缺 to（保存目录）应 400");
    }
}
