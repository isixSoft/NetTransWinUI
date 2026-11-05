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
}
