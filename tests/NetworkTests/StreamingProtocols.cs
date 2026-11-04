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
