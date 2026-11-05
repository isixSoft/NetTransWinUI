using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NetTrans.Kernel;
using NetTrans.Kernel.Net;
using NetTrans.Kernel.Protocol;
using NetTrans.Kernel.Protocol.Drivers;
using NetTrans.Kernel.Transfer;
namespace NetworkTests;

internal sealed partial class Program
{
    // ── 6. DASH 其余形态 ──────────────────────────────────────────────────

    private static async Task DashSegmentTimelineAsync(TestServer server)
    {
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static" mediaPresentationDuration="PT7S">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v1" bandwidth="800000" codecs="avc1.64001f">
                    <SegmentTemplate media="$RepresentationID$_$Bandwidth$_$Number$_$Time$.m4s"
                                     initialization="$RepresentationID$/init.mp4" timescale="1" startNumber="1">
                      <SegmentTimeline>
                        <S t="0" d="2" r="1"/>
                        <S d="3"/>
                      </SegmentTimeline>
                    </SegmentTemplate>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("dash/timeline.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));

        var probe = await Registry.ProbeAsync(server.Base + "dash/timeline.mpd", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "SegmentTimeline 应产出 SegmentFiles 计划");
        var b = server.Base;
        // t=0 d=2 r=1 → 2 段（number 1,2 / time 0,2）；S d=3 续接 → 1 段（number 3 / time 4）
        Equal(
            string.Join("\n", new[]
            {
                $"{b}dash/v1/init.mp4",
                $"{b}dash/v1_800000_1_0.m4s",
                $"{b}dash/v1_800000_2_2.m4s",
                $"{b}dash/v1_800000_3_4.m4s",
            }),
            string.Join("\n", plan.Segments), "SegmentTimeline 应按 t/d/r 展开并解析模板变量");
        Equal("timeline.mp4", probe.Name, "DASH 输出文件名");
        That(plan.TotalSize is null, "SegmentFiles 计划无总大小");
    }

    private static async Task DashSegmentListAsync(TestServer server)
    {
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v2" bandwidth="1200000">
                    <SegmentList duration="4">
                      <Initialization sourceURL="list-init.mp4"/>
                      <SegmentURL media="seg-1.m4s"/>
                      <SegmentURL media="seg-2.m4s"/>
                    </SegmentList>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("dash/list.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));

        var probe = await Registry.ProbeAsync(server.Base + "dash/list.mpd", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "SegmentList 应产出 SegmentFiles 计划");
        var b = server.Base;
        Equal(
            string.Join("\n", new[] { $"{b}dash/list-init.mp4", $"{b}dash/seg-1.m4s", $"{b}dash/seg-2.m4s" }),
            string.Join("\n", plan.Segments), "SegmentList 应含 Initialization + 全部 SegmentURL 并解析为绝对 URL");
        Equal("list.mp4", probe.Name, "DASH 输出文件名");
    }

    private static async Task DashSegmentBaseAsync(TestServer server)
    {
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="video">
                  <Representation id="v3" bandwidth="900000">
                    <BaseURL>video/single.m4v</BaseURL>
                    <SegmentBase indexRange="0-999">
                      <Initialization range="0-999"/>
                    </SegmentBase>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("dash/base.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));

        var probe = await Registry.ProbeAsync(server.Base + "dash/base.mpd", null, null, default);
        var plan = AssertPlan<TransferPlan.ByteRanges>(probe, "SegmentBase 应回退为 ByteRanges 计划");
        Equal($"{server.Base}dash/video/single.m4v", plan.Url, "ByteRanges 应落在 BaseURL 解析结果上");
        That(plan.AcceptRanges, "SegmentBase 回退计划应允许字节范围");
        Equal(-1, plan.Size, "SegmentBase 回退计划大小未知");
        That(plan.Mirrors.Count == 0, "不应有镜像");
        That(probe.AcceptRanges, "probe 应允许字节范围");
        Equal("base.mp4", probe.Name, "DASH 输出文件名");
    }

    // ── 7. HLS 解析边界 ───────────────────────────────────────────────────

    private static async Task HlsEdgesAsync(TestServer server)
    {
        var master = string.Join('\n',
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=640x360",
            server.Base + "hls/v/low.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,RESOLUTION=1920x1080",
            server.Base + "hls/v/high.m3u8");
        const string low = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            ../seg0.ts
            #EXTINF:10,
            seg1.ts?token=abc
            #EXTINF:10,
            http://absolute.example.org/abs.ts
            #EXT-X-ENDLIST
            """;
        const string high = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            high-only.ts
            #EXT-X-ENDLIST
            """;
        server.Map("hls/master.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(master), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/low.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(low), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/high.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(high), "application/vnd.apple.mpegurl"));

        var probe = await Registry.ProbeAsync(server.Base + "hls/master.m3u8", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "HLS 应产出 SegmentFiles 计划");
        var b = server.Base;
        Equal(
            string.Join("\n", new[] { $"{b}hls/seg0.ts", $"{b}hls/v/seg1.ts?token=abc", "http://absolute.example.org/abs.ts" }),
            string.Join("\n", plan.Segments),
            "分段应解析：../ 相对路径、带查询串、绝对 URL 原样保留");
        That(!plan.Segments.Any(s => s.Contains("high-only")), "码率相同时应取第一个变体（low.m3u8）");
        Equal("master.ts", probe.Name, "HLS 输出文件名 .ts");
    }

    private static async Task HlsRelativeVariantAsync(TestServer server)
    {
        // RFC8216：变体 URI 相对于 master 播放列表解析，相对路径是常态写法
        var master = string.Join('\n',
            "#EXTM3U",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000",
            "v/rel-low.m3u8",
            "#EXT-X-STREAM-INF:BANDWIDTH=2000000",
            "v/rel-high.m3u8");
        const string high = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            rel-seg.ts
            #EXT-X-ENDLIST
            """;
        server.Map("hls/rel-master.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(master), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/rel-low.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(high), "application/vnd.apple.mpegurl"));
        server.Map("hls/v/rel-high.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(high), "application/vnd.apple.mpegurl"));

        var probe = await Registry.ProbeAsync(server.Base + "hls/rel-master.m3u8", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "相对变体 URL 的 master 应可探测");
        Equal($"{server.Base}hls/v/rel-seg.ts", plan.Segments.Single(), "应取最高码率变体 rel-high.m3u8 并解析分段");
    }

    // ── 28. HLS/DASH 字幕轨（P1-5：外置 .vtt/.srt 优先）──────────────────

    private static async Task HlsSubtitlesAsync(TestServer server)
    {
        var master = string.Join('\n',
            "#EXTM3U",
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"中文\",LANGUAGE=\"zh\",DEFAULT=YES,AUTOSELECT=YES,URI=\"subs/zh.vtt\"",
            "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",URI=\"subs/en.srt\"",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"Audio\",LANGUAGE=\"en\",URI=\"audio/en.m3u8\"",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,SUBTITLES=\"subs\"",
            "v/video.m3u8");
        const string video = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            seg0.ts
            #EXT-X-ENDLIST
            """;
        var vtt = "#WEBVTT\n\n00:00:01.000 --> 00:00:02.000\n你好，世界\n";
        const string srt = "1\n00:00:01,000 --> 00:00:02,000\nHello\n";
        server.Map("hls/sub/master.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(master), "application/vnd.apple.mpegurl"));
        server.Map("hls/sub/v/video.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(video), "application/vnd.apple.mpegurl"));
        server.Map("hls/sub/subs/zh.vtt", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(vtt), "text/vtt"));
        server.Map("hls/sub/subs/en.srt", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(srt), "application/x-subrip"));

        var probe = await Registry.ProbeAsync(server.Base + "hls/sub/master.m3u8", null, null, default);
        var plan = AssertPlan<TransferPlan.SegmentFiles>(probe, "HLS 视频计划不应因字幕存在而改变");
        Equal($"{server.Base}hls/sub/v/seg0.ts", plan.Segments.Single(), "视频分段应正常解析");

        var tracks = probe.Subtitles ?? throw new Exception("probe.Subtitles 应列出 EXT-X-MEDIA 字幕轨");
        Equal(2, tracks.Count, "TYPE=SUBTITLES 应有两条，TYPE=AUDIO 不算字幕轨");
        Equal("中文", tracks[0].Name, "字幕轨 NAME 属性");
        Equal("zh", tracks[0].Language, "字幕轨 LANGUAGE 属性");
        That(tracks[0].IsDefault, "DEFAULT=YES 应映射为默认轨");
        Equal($"{server.Base}hls/sub/subs/zh.vtt", tracks[0].Url, "字幕 URI 应相对 master 解析为绝对 URL");
        Equal("English", tracks[1].Name, "第二条字幕轨 NAME");
        Equal("en", tracks[1].Language, "第二条字幕轨 LANGUAGE");
        Equal($"{server.Base}hls/sub/subs/en.srt", tracks[1].Url, "第二条字幕轨 URL");
        That(!tracks[1].IsDefault, "无 DEFAULT 的字幕轨不是默认轨");

        // 外置 .vtt 可下载：作为普通 HTTP 任务完整取回
        var dir = TempDir();
        using var kernel = new RealDownloadKernel(new KernelOptions { ConnectionsPerTask = 1, MaxSimultaneous = 1 });
        var completed = NewSignal();
        kernel.TaskCompleted += _ => completed.TrySetResult();
        var task = kernel.Add(new NewDownloadRequest(tracks[0].Url, dir, "", 1, TaskPriority.Normal, StartNow: true));
        await WaitSignalAsync(completed.Task, "字幕 .vtt 应可作为普通任务下载完成", 60_000);
        Equal(DownloadStatus.Completed, task.Status, "字幕任务应为 Completed");
        Equal(vtt, File.ReadAllText(TargetPath(dir, task)), "字幕文件内容应一致");
    }

    private static async Task HlsNoSubtitlesAsync(TestServer server)
    {
        const string video = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            plain.ts
            #EXT-X-ENDLIST
            """;
        const string nouriVideo = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:10,
            nouri.ts
            #EXT-X-ENDLIST
            """;
        var master = string.Join('\n', "#EXTM3U", "#EXT-X-STREAM-INF:BANDWIDTH=1000000", "v/plain.m3u8");
        // SUBTITLES 行缺 URI：不可用，不得产出轨道也不得影响视频
        var noUri = string.Join('\n', "#EXTM3U", "#EXT-X-MEDIA:TYPE=SUBTITLES,NAME=\"Ghost\",LANGUAGE=\"en\"", "#EXT-X-STREAM-INF:BANDWIDTH=1000000", "v/nouri.m3u8");
        server.Map("hls/nosub/master.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(master), "application/vnd.apple.mpegurl"));
        server.Map("hls/nosub/v/plain.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(video), "application/vnd.apple.mpegurl"));
        server.Map("hls/nouri/master.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(noUri), "application/vnd.apple.mpegurl"));
        server.Map("hls/nouri/v/nouri.m3u8", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(nouriVideo), "application/vnd.apple.mpegurl"));

        var probe = await Registry.ProbeAsync(server.Base + "hls/nosub/master.m3u8", null, null, default);
        AssertPlan<TransferPlan.SegmentFiles>(probe, "无字幕 master 应照常产出 SegmentFiles");
        That(probe.Subtitles is null or { Count: 0 }, "无 EXT-X-MEDIA 时不应有字幕轨");
        Equal("master.ts", probe.Name, "无字幕 HLS 输出名不变");

        var probe2 = await Registry.ProbeAsync(server.Base + "hls/nouri/master.m3u8", null, null, default);
        AssertPlan<TransferPlan.SegmentFiles>(probe2, "缺 URI 的 SUBTITLES 行不应影响视频计划");
        That(probe2.Subtitles is null or { Count: 0 }, "缺 URI 的 SUBTITLES 行不应产出字幕轨");
        Equal("master.ts", probe2.Name, "输出名不变");
    }

    private static async Task DashSubtitlesAsync(TestServer server)
    {
        const string mpd = """
            <?xml version="1.0" encoding="utf-8"?>
            <MPD xmlns="urn:mpeg:dash:schema:mpd:2011" type="static">
              <Period>
                <AdaptationSet contentType="text" lang="zh">
                  <Representation id="subs-zh" bandwidth="256000">
                    <BaseURL>subs/zh.vtt</BaseURL>
                  </Representation>
                </AdaptationSet>
                <AdaptationSet contentType="video">
                  <Representation id="v1" bandwidth="800000">
                    <BaseURL>video/single.m4v</BaseURL>
                    <SegmentBase indexRange="0-999">
                      <Initialization range="0-999"/>
                    </SegmentBase>
                  </Representation>
                </AdaptationSet>
              </Period>
            </MPD>
            """;
        server.Map("dash/subs.mpd", ctx => TestServer.Reply(ctx.Response, 200, Encoding.UTF8.GetBytes(mpd), "application/dash+xml"));

        var probe = await Registry.ProbeAsync(server.Base + "dash/subs.mpd", null, null, default);
        var plan = AssertPlan<TransferPlan.ByteRanges>(probe, "视频计划不应因文本轨存在而改变");
        Equal($"{server.Base}dash/video/single.m4v", plan.Url, "视频仍应落在 video AdaptationSet 上");
        var tracks = probe.Subtitles ?? throw new Exception("DASH text AdaptationSet 应列为字幕轨");
        Equal(1, tracks.Count, "文本轨数量");
        Equal("subs-zh", tracks[0].Name, "DASH 字幕轨名取 Representation id");
        Equal("zh", tracks[0].Language, "DASH 字幕轨语言取 AdaptationSet lang");
        Equal($"{server.Base}dash/subs/zh.vtt", tracks[0].Url, "DASH 字幕 BaseURL 应解析为绝对 URL");
    }

    // ── 8. SOCKS5 ─────────────────────────────────────────────────────────

    private static async Task Socks5RoundtripAsync()
    {
        var payload = Encoding.UTF8.GetBytes("HELLO-FROM-SOCKS5-REMOTE-0123456789ABCDEFG");
        using var socks = new Socks5TestServer(payload);

        using var stream = await Socks5Connector.ConnectAsync(
            new Uri($"socks5://127.0.0.1:{socks.Port}"), "proxy-target.example.org", 8443, default);
        That(stream.CanRead && stream.CanWrite, "返回的流应可读可写");

        var received = new byte[payload.Length];
        await ReadExactlyAsync(stream, received);
        Equal(Encoding.UTF8.GetString(payload), Encoding.UTF8.GetString(received), "远端推送的数据应原样到达");

        var reply = new byte[payload.Length];
        Encoding.UTF8.GetBytes("CLIENT-READY").CopyTo(reply, 0);
        await stream.WriteAsync(reply);

        var finished = await Task.WhenAny(socks.HandshakeAndEcho, Task.Delay(5_000));
        That(finished == socks.HandshakeAndEcho, $"SOCKS5 服务端握手/回读应完成：{socks.HandshakeAndEcho.Status}");
        That(socks.GreetingOk, "greeting 应为 05 01 00");
        Equal("proxy-target.example.org", socks.Host, "CONNECT 应携带目标域名（ATYP=3）");
        Equal(8443, socks.DestinationPort ?? -1, "CONNECT 应携带目标端口（大端两字节）");
        Equal(Encoding.UTF8.GetString(reply), Encoding.UTF8.GetString(socks.FromClient ?? Array.Empty<byte>()),
            "客户端写入的数据应原样到达远端");
    }

}
