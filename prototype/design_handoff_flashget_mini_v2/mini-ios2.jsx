// Mediation Mini v2 — iOS idiom, full-weight download manager.
// The seven-button discipline stays; depth lives in the side-docked inspector,
// the context menu, and the queue — not in more chrome.
const { useState, useEffect, useRef } = React;

const I = {
  plus: "M12 5v14M5 12h14",
  trash: "M4 7h16M10 11v6M14 11v6M6 7l1 12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-12M9 7V5a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2",
  folder: "M3 8a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8z",
  search: "M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16zM21 21l-4.3-4.3",
  info: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 11v5M12 7.6v.6",
  gear: "M12 15.2a3.2 3.2 0 1 0 0-6.4 3.2 3.2 0 0 0 0 6.4zM19.4 13.6a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.7 1.7 0 0 0-1.87-.34 1.7 1.7 0 0 0-1.03 1.56V20a2 2 0 1 1-4 0v-.1A1.7 1.7 0 0 0 8.9 18.3a1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.7 1.7 0 0 0 .34-1.87 1.7 1.7 0 0 0-1.56-1.03H2a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.56-1.14 1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.7 1.7 0 0 0 1.87.34H8.9A1.7 1.7 0 0 0 9.94 4.1V4a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1.03 1.56 1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.7 1.7 0 0 0-.34 1.87v.04a1.7 1.7 0 0 0 1.56 1.03H22a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.56 1.03z",
  chev: "M9 6l6 6-6 6",
  film: "M4 5h16v14H4zM4 9h16M4 15h16M8 5v14M16 5v14",
  doc: "M7 3h7l5 5v13H7zM14 3v5h5",
  zip: "M7 3h10v18H7zM11 5h2v2h-2zM13 7h-2v2h2zM11 9h2v2h-2zM13 11h-2v2h2z",
  disc: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 14.5a2.5 2.5 0 1 0 0-5 2.5 2.5 0 0 0 0 5z",
  music: "M9 18V5l10-2v13M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM19 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0z",
  open: "M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5",
  copy: "M9 9h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H9a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1zM5 15H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h9a1 1 0 0 1 1 1v1",
  redo: "M20 11a8 8 0 1 0-2.3 6M20 5v6h-6",
  rename: "M4 20h16M6 16l10.5-10.5a2.1 2.1 0 0 0-3-3L3 13v3h3z",
  up: "M12 19V5M5 12l7-7 7 7",
  down: "M12 5v14M19 12l-7 7-7-7",
  clock: "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18zM12 7v5l3.2 2",
  shield: "M12 3l7 3v6c0 4.4-3 7.6-7 9-4-1.4-7-4.6-7-9V6z",
  check: "M20 6L9 17l-5-5",
  x: "M6 6l12 12M18 6L6 18",
  gauge: "M12 20a8 8 0 1 0-8-8M12 12l4.5-4.5",
};
const Ic = ({ d, w = 1.7, size }) => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={w} strokeLinecap="round" strokeLinejoin="round"
    style={size ? { width: size, height: size } : undefined}><path d={d} /></svg>
);
const PlayIc = () => <svg viewBox="0 0 24 24" fill="currentColor"><path d="M7 4.5l13 7.5-13 7.5z" /></svg>;
const PauseIc = () => <svg viewBox="0 0 24 24" fill="currentColor"><path d="M7 4h4v16H7zM13 4h4v16h-4z" /></svg>;

const CATS = [
  { id: "all", label: "全部" }, { id: "soft", label: "软件" }, { id: "video", label: "视频" },
  { id: "doc", label: "文档" }, { id: "music", label: "音乐" }, { id: "bt", label: "BT" },
];

const mkBlocks = (pct, n = 96) => Array.from({ length: n }, (_, i) => i / n < pct / 100 ? 1 : (Math.random() < .05 && i / n < pct / 100 + .12 ? 2 : 0));
const mkConns = (n, kbs) => Array.from({ length: n }, () => ({ v: kbs > 0 ? (kbs / n) * (0.55 + Math.random() * 0.9) : 0 }));

const T = (id, name, kind, tint, cat, size, got, kbs, state, conns, host, url, extra = {}) => ({
  id, name, kind, tint, cat, size, got, kbs, state, conns, host, url,
  priority: extra.priority || "normal", limit: extra.limit || "不限", retries: extra.retries || 0,
  added: extra.added || "今天 09:41", checksum: extra.checksum || null, err: extra.err || null,
  peers: extra.peers, seeds: extra.seeds, ratio: extra.ratio, up: extra.up || 0, newer: extra.newer || null,
  log: extra.log || [["09:41", "任务已创建"], ["09:41", `已连接 ${host}`], ["09:41", "服务器支持断点续传"], ["09:42", `已建立 ${conns} 个连接`]],
});

const SEED = [
  T(1, "ubuntu-24.04.2-desktop.iso", "disc", "#ff9500", "soft", 5940, 3742, 1180, "run", 8, "releases.ubuntu.com", "https://releases.ubuntu.com/24.04/ubuntu-24.04.2-desktop-amd64.iso", { checksum: "SHA-256 待校验", priority: "high", newer: { ver: "ubuntu-24.04.3-desktop.iso", size: 6042, date: "2 天前" } }),
  T(3, "4k-timelapse-reel.mp4", "film", "#af52de", "video", 1180, 486, 742, "run", 6, "cdn.video-host.net", "https://cdn.video-host.net/stream/8841/4k-timelapse-reel.mp4"),
  T(5, "source-sans-pack.zip", "zip", "#5856d6", "doc", 86, 21, 214, "run", 4, "fonts.mirror.dev", "https://fonts.mirror.dev/packs/source-sans-3-complete.zip"),
  T(4, "arch-linux-2026.08.torrent", "disc", "#8e8e93", "bt", 3210, 1104, 0, "paused", 0, "12 个节点", "magnet:?xt=urn:btih:9f2c1a…", { peers: 12, seeds: 4, ratio: 0.42, up: 86 }),
  T(7, "logic-samples-vol3.flac", "music", "#ff2d55", "music", 740, 96, 0, "error", 0, "cdn.audio-lib.net", "https://cdn.audio-lib.net/vol3/logic-samples-vol3.flac", { retries: 2, err: "连接被服务器重置", log: [["09:38", "任务已创建"], ["09:39", "已连接 cdn.audio-lib.net"], ["09:44", "连接被服务器重置"], ["09:44", "第 2 次重试失败"]] }),
  T(8, "design-system-handoff.pdf", "doc", "#0a84ff", "doc", 62, 0, 0, "queued", 0, "docs.internal", "https://docs.internal/handoff/design-system.pdf", { log: [["09:45", "已加入队列，等待空闲通道"]] }),
  T(2, "blender-4.2-portable.7z", "zip", "#34c759", "soft", 412, 412, 0, "done", 0, "mirror.blender.org", "https://mirror.blender.org/release/blender-4.2.7z", { checksum: "SHA-256 已校验", newer: { ver: "blender-4.2.1-portable.7z", size: 418, date: "今天 08:12" } }),
  T(6, "quarterly-report-q2.pdf", "doc", "#ff3b30", "doc", 14, 14, 0, "done", 0, "docs.internal", "https://docs.internal/reports/2026-q2.pdf"),
];

const mb = (v) => v >= 1024 ? (v / 1024).toFixed(2) + " GB" : Math.round(v) + " MB";
const spd = (k) => k <= 0 ? "" : k >= 1024 ? (k / 1024).toFixed(1) + " MB/s" : Math.round(k) + " KB/s";
const eta = (t) => { if (t <= 0 || !isFinite(t) || t > 5940) return "计算中"; const m = Math.floor(t / 60), s = Math.round(t % 60); return m ? `剩余 ${m} 分 ${s} 秒` : `剩余 ${s} 秒`; };
const STATE_CN = { run: "下载中", paused: "已暂停", done: "已完成", error: "出错", queued: "排队中" };

function Segmented({ value, onChange, items }) {
  const i = items.findIndex((x) => x.id === value);
  return (
    <div className="seg">
      <div className="seg__thumb" style={{ left: `calc(${(i * 100) / items.length}% + 2px)`, width: `calc(${100 / items.length}% - 4px)` }}></div>
      {items.map((x) => <button key={x.id} aria-selected={x.id === value} onClick={() => onChange(x.id)}>{x.label}</button>)}
    </div>
  );
}
const Switch = ({ on, onClick }) => <button className="sw" role="switch" aria-checked={on} onClick={onClick}><i></i></button>;

function Row({ t, sel, onSel, onToggle, onRemove, onCtx }) {
  const pct = Math.min(100, (t.got / t.size) * 100);
  const done = t.state === "done";
  const sub = done ? `${mb(t.size)}${t.checksum ? " · " + t.checksum : ""}`
    : t.state === "error" ? t.err + ` · 已重试 ${t.retries} 次`
    : t.state === "queued" ? "排队中，等待空闲通道"
    : t.state === "paused" ? `已暂停 · ${mb(t.got)} / ${mb(t.size)}`
    : `${spd(t.kbs)} · ${eta((t.size - t.got) * 1024 / (t.kbs || 1))}`;
  return (
    <div className="row" data-sel={sel} data-state={t.state} onClick={onSel}
      onContextMenu={(e) => { e.preventDefault(); onSel(e); onCtx(e); }}>
      <div className="tile" style={{ background: t.tint }}><Ic d={I[t.kind]} w={1.9} /></div>
      <div className="rmid">
        <div className="rname">{t.name}{t.priority === "high" && <span className="tag2" style={{ marginLeft: 6 }}>优先</span>}{t.newer && <span className="tag2 new" style={{ marginLeft: 6 }}>新版本</span>}</div>
        <div className={"rsub" + (t.state === "error" ? " rerr" : "")}>{sub}</div>
        {!done && t.state !== "queued" && <div className="prog"><i style={{ width: pct + "%" }}></i></div>}
      </div>
      <div className="rt">
        {done ? <span className="badge">完成</span>
          : t.state === "error" ? <span className="rpct rerr">失败</span>
          : t.state === "queued" ? <span className="rpct">—</span>
          : <span className="rpct">{pct.toFixed(0)}%</span>}
        <span className="chev"><Ic d={I.chev} w={2.4} size={13} /></span>
      </div>
      <div className="swipe">
        {!done && (
          <button className="sw-pause" onClick={(e) => { e.stopPropagation(); onToggle(); }}>
            {t.state === "run" ? <PauseIc /> : <PlayIc />}{t.state === "run" ? "暂停" : t.state === "error" ? "重试" : "继续"}
          </button>
        )}
        <button className="sw-del" onClick={(e) => { e.stopPropagation(); onRemove(); }}><Ic d={I.trash} />删除</button>
      </div>
    </div>
  );
}

/* ── side-docked inspector: 概览 / 分块 / 连接 / 日志 ─── */
function Inspector({ t, onPatch, onRedl }) {
  const [view, setView] = useState("info");
  if (!t) return <aside className="side"><p className="fnote" style={{ padding: "28px 16px", textAlign: "center" }}>选择一个任务查看详情。</p></aside>;
  const pct = Math.min(100, (t.got / t.size) * 100);
  const R = 34, C = 2 * Math.PI * R;
  const stroke = t.state === "error" ? "var(--red)" : t.state === "done" ? "var(--green)" : t.state === "run" ? "var(--blue)" : "var(--gray)";
  const blocks = mkBlocks(pct, 96);
  const conns = mkConns(t.conns || 0, t.kbs);
  const row = (k, v, cls) => <div className="frow" key={k}><span className="flabel">{k}</span><span className={"fval" + (cls ? " " + cls : "")}>{v}</span></div>;

  const NewVer = () => t.newer ? (
    <div className="newver">
      <span className="newver__i"><Ic d={I.up} w={2.4} size={15} /></span>
      <div className="newver__m">
        <b>服务器上有更新版本</b>
        <span>{t.newer.ver} · {mb(t.newer.size)} · 发布于 {t.newer.date}</span>
      </div>
      <button onClick={() => onRedl && onRedl(t)}>下载新版</button>
    </div>
  ) : null;

  return (
    <aside className="side">
      <Segmented value={view} onChange={setView} items={[
        { id: "info", label: "概览" }, { id: "blk", label: "分块" }, { id: "conn", label: "连接" }, { id: "log", label: "日志" },
      ]} />
      <div className="side__body">
        <NewVer />

        {view === "info" && <>
          <div className="ring">
            <svg viewBox="0 0 80 80">
              <circle cx="40" cy="40" r={R} fill="none" stroke="rgba(120,120,128,.18)" strokeWidth="7" />
              <circle cx="40" cy="40" r={R} fill="none" strokeWidth="7" strokeLinecap="round" stroke={stroke}
                strokeDasharray={C} strokeDashoffset={C * (1 - pct / 100)} style={{ transition: "stroke-dashoffset .6s linear" }} />
            </svg>
            <div className="ring__c">
              <div className="ring__p">{pct.toFixed(0)}<span style={{ fontSize: 13 }}>%</span></div>
              <div className="ring__s">{spd(t.kbs) || STATE_CN[t.state]}</div>
            </div>
          </div>
          <div className="ringcap">{mb(t.got)} / {mb(t.size)} · {t.state === "done" ? "已完成" : eta((t.size - t.got) * 1024 / (t.kbs || 1))}</div>
          <div className="card">
            {row("状态", STATE_CN[t.state], t.state === "error" ? "rerr" : "")}
            {row("来源", t.host)}
            {row("连接数", t.conns || "—")}
            {row("速度", spd(t.kbs) || "—")}
            {t.peers !== undefined ? row("节点 / 种子", t.peers + " / " + t.seeds) : row("校验", t.checksum || "未启用")}
            {row("断点续传", "支持")}
            {row("添加时间", t.added)}
            {row("保存位置", "D:\\Downloads")}
          </div>
          <div className="ghdr" style={{ marginTop: 16 }}>传输</div>
          <div className="card">
            <div className="frow"><span className="flabel">优先级</span>
              <div className="pri">
                {[["high", "高"], ["normal", "普通"], ["low", "低"]].map(([k, l]) => (
                  <button key={k} aria-pressed={t.priority === k} onClick={() => onPatch({ priority: k })}>{l}</button>
                ))}
              </div>
            </div>
            <div className="frow"><span className="flabel">速度上限</span>
              <select value={t.limit} onChange={(e) => onPatch({ limit: e.target.value })}>
                <option>不限</option><option>1 MB/s</option><option>512 KB/s</option><option>128 KB/s</option>
              </select>
            </div>
          </div>
          <p className="fnote" style={{ wordBreak: "break-all" }}>{t.url}</p>
        </>}

        {view === "blk" && <>
          <div className="ghdr">分块 · 96</div>
          <div className="card"><div className="blocks">{blocks.map((b, i) => <span key={i} className={"blk" + (b === 1 ? " on" : b === 2 ? " act" : "")}></span>)}</div></div>
          <div className="ghdr" style={{ marginTop: 16 }}>本次会话</div>
          <div className="card">
            <div className="sched">{Array.from({ length: 40 }, (_, i) => <i key={i} className={i > 33 ? "on" : ""} style={{ height: (12 + Math.random() * 22) + "px" }}></i>)}</div>
            {row("平均速度", spd(t.kbs * .82) || "—")}
            {row("峰值", spd(t.kbs * 1.24) || "—")}
            {row("重试", t.retries + " 次")}
          </div>
        </>}

        {view === "conn" && <>
          <div className="ghdr">连接 · {conns.length || "0"}</div>
          <div className="card">
            {conns.length === 0 && <p className="fnote" style={{ padding: "16px 12px", textAlign: "center" }}>当前没有活动连接。</p>}
            {conns.map((c, i) => (
              <div className="conn" key={i}>
                <span className="conn__n">#{i + 1}</span>
                <span className="conn__b"><i style={{ width: Math.min(100, (c.v / (t.kbs / conns.length || 1)) * 62) + "%" }}></i></span>
                <span className="conn__v">{spd(c.v) || "空闲"}</span>
              </div>
            ))}
          </div>
          {t.peers !== undefined && <>
            <div className="ghdr" style={{ marginTop: 16 }}>BT</div>
            <div className="card">
              {row("节点", t.peers)}{row("种子", t.seeds)}{row("分享率", t.ratio)}{row("上传", spd(t.up) || "—")}
            </div>
          </>}
        </>}

        {view === "log" && <>
          <div className="ghdr">日志</div>
          <div className="card"><div className="log">
            {t.log.map(([ts, msg], i) => <div className="logrow" key={i}><time>{ts}</time><span>{msg}</span></div>)}
          </div></div>
        </>}
      </div>
    </aside>
  );
}

/* ── sheets ────────────────────────────────────────────── */
function Sheet({ title, left, right, onClose, children }) {
  return (
    <div className="scrim" onMouseDown={onClose}>
      <div className="sheet" onMouseDown={(e) => e.stopPropagation()}>
        <div className="grab"></div>
        <div className="shead">
          <button className="nav__b" onClick={onClose}>{left === undefined ? "取消" : left}</button>
          <h2>{title}</h2>
          {right || <button className="nav__b w" onClick={onClose}>完成</button>}
        </div>
        <div className="sbody">{children}</div>
      </div>
    </div>
  );
}

function AddSheet({ onClose }) {
  const [auto, setAuto] = useState(true);
  const [sched, setSched] = useState(false);
  return (
    <Sheet title="新建下载" right={<button className="nav__b w" onClick={onClose}>添加</button>} onClose={onClose}>
      <div className="card"><div className="frow"><input type="text" className="l" defaultValue="https://" autoFocus /></div></div>
      <p className="fnote">已从剪贴板检测到链接。支持 HTTP / FTP / 磁力链 / 种子文件。</p>
      <div className="card" style={{ marginTop: 14 }}>
        <div className="frow"><span className="flabel">保存到</span><span className="fval">下载</span><span className="chev"><Ic d={I.chev} w={2.4} size={13} /></span></div>
        <div className="frow"><span className="flabel">分类</span>
          <select defaultValue="软件"><option>软件</option><option>视频</option><option>文档</option><option>音乐</option></select>
        </div>
        <div className="frow"><span className="flabel">连接数</span>
          <select defaultValue="8"><option>1</option><option>4</option><option>8</option><option>16</option></select>
        </div>
        <div className="frow"><span className="flabel">优先级</span>
          <select defaultValue="普通"><option>高</option><option>普通</option><option>低</option></select>
        </div>
      </div>
      <div className="card" style={{ marginTop: 14 }}>
        <div className="frow"><span className="flabel">立即开始</span><Switch on={auto} onClick={() => setAuto(!auto)} /></div>
        <div className="frow"><span className="flabel">定时开始</span><Switch on={sched} onClick={() => setSched(!sched)} /></div>
        {sched && <div className="frow"><span className="flabel">开始时间</span><span className="fval">今晚 23:00</span><span className="chev"><Ic d={I.chev} w={2.4} size={13} /></span></div>}
      </div>
    </Sheet>
  );
}

function SniffSheet({ onClose, onAdd }) {
  const [url, setUrl] = useState("https://video-host.net/watch/8841");
  const [state, setState] = useState("idle");
  const [picks, setPicks] = useState({ 0: true });
  const found = [{ q: "2160p", size: "1.2 GB", fmt: "MP4" }, { q: "1080p", size: "412 MB", fmt: "MP4" }, { q: "720p", size: "186 MB", fmt: "MP4" }, { q: "音轨", size: "38 MB", fmt: "M4A" }];
  return (
    <Sheet title="视频嗅探" onClose={onClose}
      right={<button className="nav__b w" disabled={state !== "found"} style={state !== "found" ? { color: "var(--label3)" } : undefined}
        onClick={() => { onAdd(); onClose(); }}>下载</button>}>
      <div className="card"><div className="frow"><input type="text" className="l" value={url} onChange={(e) => { setUrl(e.target.value); setState("idle"); }} placeholder="粘贴网页地址" /></div></div>
      <p className="fnote">便携版不注入浏览器，改为粘贴网页地址后探测。</p>
      {state !== "found"
        ? <button className="big" onClick={() => { setState("probing"); setTimeout(() => setState("found"), 900); }}
            disabled={state === "probing"} style={state === "probing" ? { opacity: .5 } : undefined}>
            {state === "probing" ? "探测中…" : "探测视频"}
          </button>
        : <>
            <div className="ghdr" style={{ marginTop: 16 }}>找到 {found.length} 个源</div>
            <div className="card">
              {found.map((f, i) => (
                <div className="frow" key={i} style={{ cursor: "pointer" }} onClick={() => setPicks({ ...picks, [i]: !picks[i] })}>
                  <span className="flabel">{f.q} <span style={{ color: "var(--label2)", fontSize: 13 }}>{f.fmt}</span></span>
                  <span className="fval">{f.size}</span>
                  <span style={{ color: picks[i] ? "var(--blue)" : "var(--label3)", display: "grid", placeItems: "center", width: 18 }}><Ic d={I.check} w={2.6} size={16} /></span>
                </div>
              ))}
            </div>
          </>}
    </Sheet>
  );
}

function SettingsSheet({ onClose, cap, setCap }) {
  const [s, setS] = useState({ clip: true, scan: true, edge: true, notify: true, verify: true, quiet: false, resume: true });
  const [after, setAfter] = useState("无操作");
  const tog = (k) => setS({ ...s, [k]: !s[k] });
  return (
    <Sheet title="设置" left="" onClose={onClose}>
      <div className="ghdr">下载</div>
      <div className="card">
        <div className="frow"><span className="flabel">默认位置</span><span className="fval">D:\Downloads</span><span className="chev"><Ic d={I.chev} w={2.4} size={13} /></span></div>
        <div className="frow"><span className="flabel">按分类分文件夹</span><Switch on={s.resume} onClick={() => tog("resume")} /></div>
        <div className="frow"><span className="flabel">同时下载</span><select defaultValue="3"><option>1</option><option>3</option><option>5</option><option>8</option></select></div>
        <div className="frow"><span className="flabel">全局限速</span>
          <select value={cap} onChange={(e) => setCap(e.target.value)}><option>不限</option><option>4 MB/s</option><option>2 MB/s</option><option>1 MB/s</option><option>256 KB/s</option></select>
        </div>
        <div className="frow"><span className="flabel">夜间不限速</span><Switch on={s.quiet} onClick={() => tog("quiet")} /></div>
      </div>

      <div className="ghdr" style={{ marginTop: 18 }}>队列与计划</div>
      <div className="card">
        <div className="frow"><span className="flabel">计划时段</span><span className="fval">23:00 – 07:00</span><span className="chev"><Ic d={I.chev} w={2.4} size={13} /></span></div>
        <div className="frow"><span className="flabel">失败自动重试</span><select defaultValue="3 次"><option>不重试</option><option>3 次</option><option>10 次</option></select></div>
        <div className="frow"><span className="flabel">全部完成后</span>
          <select value={after} onChange={(e) => setAfter(e.target.value)}><option>无操作</option><option>退出程序</option><option>休眠</option><option>关机</option></select>
        </div>
      </div>

      <div className="ghdr" style={{ marginTop: 18 }}>行为</div>
      <div className="card">
        <div className="frow"><span className="flabel">监视剪贴板</span><Switch on={s.clip} onClick={() => tog("clip")} /></div>
        <div className="frow"><span className="flabel">完成后通知</span><Switch on={s.notify} onClick={() => tog("notify")} /></div>
        <div className="frow"><span className="flabel">完成后校验 SHA-256</span><Switch on={s.verify} onClick={() => tog("verify")} /></div>
        <div className="frow"><span className="flabel">完成后病毒扫描</span><Switch on={s.scan} onClick={() => tog("scan")} /></div>
        <div className="frow"><span className="flabel">贴边隐藏</span><Switch on={s.edge} onClick={() => tog("edge")} /></div>
        <div className="frow"><span className="flabel">老板键</span><span className="fval">Ctrl + Alt + H</span><span className="chev"><Ic d={I.chev} w={2.4} size={13} /></span></div>
      </div>
      <p className="fnote">按下老板键后主窗口、悬浮窗、托盘图标全部消失，下载在后台继续。</p>
      <p className="fnote">便携模式：设置写入程序同目录的 mini.ini，不写注册表、不关联协议、不驻留开机项。并发数优化需手动执行，会改动系统设置。</p>
      <button className="big plain">执行并发数优化…</button>
    </Sheet>
  );
}

function ContextMenu({ x, y, t, onClose, onToggle, onRemove, onDetail, onMove }) {
  useEffect(() => {
    const h = () => onClose();
    window.addEventListener("mousedown", h); window.addEventListener("keydown", h);
    return () => { window.removeEventListener("mousedown", h); window.removeEventListener("keydown", h); };
  }, [onClose]);
  if (!t) return null;
  const done = t.state === "done";
  const item = (label, icon, fn, cls) => <button className={cls} onClick={() => { fn && fn(); onClose(); }}>{label}{icon}</button>;
  return (
    <div className="ctx" style={{ left: x, top: y }} onMouseDown={(e) => e.stopPropagation()}>
      {done && item("打开文件", <Ic d={I.open} />)}
      {item("在文件夹中显示", <Ic d={I.folder} />)}
      <hr />
      {!done && item(t.state === "run" ? "暂停" : t.state === "error" ? "重试" : "继续", t.state === "run" ? <PauseIc /> : <PlayIc />, onToggle)}
      {item("重新下载", <Ic d={I.redo} />)}
      {item("详细信息", <Ic d={I.info} />, onDetail)}
      <hr />
      {!done && item("移到队首", <Ic d={I.up} />, () => onMove(-1))}
      {!done && item("移到队尾", <Ic d={I.down} />, () => onMove(1))}
      {item("校验 SHA-256", <Ic d={I.shield} />)}
      <hr />
      {item("拷贝链接", <Ic d={I.copy} />)}
      {item("重命名", <Ic d={I.rename} />)}
      <hr />
      {item("删除", <Ic d={I.trash} />, onRemove, "del")}
    </div>
  );
}

function Island({ total, hist, up }) {
  const max = Math.max(700, ...hist);
  const pct = 0.63, R = 8, C = 2 * Math.PI * R;
  return (
    <div className="island">
      <div className="isl__c">
        <div className="isl__top">
          <svg className="isl__ring" viewBox="0 0 22 22">
            <circle cx="11" cy="11" r={R} fill="none" stroke="rgba(255,255,255,.25)" strokeWidth="3" />
            <circle cx="11" cy="11" r={R} fill="none" stroke="#0a84ff" strokeWidth="3" strokeLinecap="round"
              strokeDasharray={C} strokeDashoffset={C * (1 - pct)} transform="rotate(-90 11 11)" />
          </svg>
          <div className="isl__v">{total >= 1024 ? (total / 1024).toFixed(1) : Math.round(total)}<em>{total >= 1024 ? "MB/s" : "KB/s"}</em></div>
          <div className="isl__sub" style={{ marginLeft: "auto" }}>↑ {up} KB/s · 平均 980 KB/s</div>
        </div>
        <div className="spark">
          {hist.map((v, i) => <i key={i} style={{ height: Math.max(2, (v / max) * 26) + "px", opacity: .3 + (i / hist.length) * .7 }}></i>)}
        </div>
      </div>
    </div>
  );
}

function App() {
  const [tasks, setTasks] = useState(SEED);
  const [sel, setSel] = useState([1]);
  const [tab, setTab] = useState("all");
  const [cat, setCat] = useState("all");
  const [detail, setDetail] = useState(true);
  const [sheet, setSheet] = useState(null);
  const [ctx, setCtx] = useState(null);
  const [cap, setCap] = useState("不限");
  const [q, setQ] = useState("");
  const [hist, setHist] = useState(() => Array.from({ length: 26 }, () => 900 + Math.random() * 900));
  const [toast, setToast] = useState(null);
  const [dense, setDense] = useState(false);
  const [sortKey, setSortKey] = useState("added");
  const [sortDir, setSortDir] = useState("asc");
  const [menu, setMenu] = useState(null);
  const [docked, setDocked] = useState(false);
  const [boss, setBoss] = useState(false);
  const [islandOn, setIslandOn] = useState(true);
  const [drop, setDrop] = useState(false);
  const [banner, setBanner] = useState(null);
  const say = (m) => { setToast(m); setTimeout(() => setToast(null), 1700); };

  useEffect(() => {
    const iv = setInterval(() => {
      setTasks((ts) => ts.map((t) => {
        if (t.state !== "run") return t;
        const kbs = Math.max(60, t.kbs * (0.93 + Math.random() * 0.15));
        const got = Math.min(t.size, t.got + kbs / 1024);
        if (got >= t.size) {
          setBanner({ name: t.name, size: t.size });
          setTimeout(() => setBanner(null), 3600);
          return { ...t, got: t.size, kbs: 0, state: "done", conns: 0, checksum: "SHA-256 已校验" };
        }
        return { ...t, got, kbs };
      }));
    }, 900);
    return () => clearInterval(iv);
  }, []);

  useEffect(() => {
    const k = (e) => {
      if (e.ctrlKey && e.altKey && (e.key === "h" || e.key === "H")) { e.preventDefault(); setBoss((b) => !b); }
      else if (e.key === "Escape" && boss) setBoss(false);
    };
    window.addEventListener("keydown", k);
    return () => window.removeEventListener("keydown", k);
  }, [boss]);

  const total = tasks.reduce((a, t) => a + t.kbs, 0);
  const up = tasks.reduce((a, t) => a + (t.up || 0), 0);
  useEffect(() => { setHist((h) => [...h.slice(1), total]); }, [Math.round(total / 40)]);

  const running = tasks.some((t) => t.state === "run");
  const patch = (id, p) => setTasks((ts) => ts.map((t) => t.id === id ? { ...t, ...p } : t));
  const toggle = (id) => setTasks((ts) => ts.map((t) => t.id !== id ? t
    : t.state === "run" ? { ...t, state: "paused", kbs: 0 } : { ...t, state: "run", kbs: 300 + Math.random() * 700, conns: t.conns || 8, err: null }));
  const remove = (ids) => { setTasks((ts) => ts.filter((t) => !ids.includes(t.id))); setSel([]); };
  const allToggle = () => setTasks((ts) => ts.map((t) => t.state === "done" ? t
    : { ...t, state: running ? "paused" : "run", kbs: running ? 0 : 300 + Math.random() * 800 }));
  const move = (id, dir) => setTasks((ts) => {
    const rest = ts.filter((t) => t.id !== id), one = ts.find((t) => t.id === id);
    return dir < 0 ? [one, ...rest] : [...rest, one];
  });

  const cur = tasks.find((t) => t.id === sel[sel.length - 1]);
  const active = tasks.filter((t) => t.state !== "done");
  const done = tasks.filter((t) => t.state === "done");
  const SORT = {
    added: (a, b) => a.id - b.id,
    name: (a, b) => a.name.localeCompare(b.name),
    size: (a, b) => a.size - b.size,
    progress: (a, b) => a.got / a.size - b.got / b.size,
    speed: (a, b) => a.kbs - b.kbs,
  };
  const shown = (tab === "active" ? active : tab === "done" ? done : tasks)
    .filter((t) => cat === "all" || t.cat === cat)
    .filter((t) => !q || t.name.toLowerCase().includes(q.toLowerCase()))
    .slice().sort((a, b) => (sortDir === "asc" ? 1 : -1) * SORT[sortKey](a, b));
  const groups = tab === "all"
    ? [["进行中", shown.filter((t) => t.state !== "done")], ["已完成", shown.filter((t) => t.state === "done")]]
    : [[null, shown]];

  const pick = (id, e) => {
    if (e && (e.metaKey || e.ctrlKey)) setSel((s) => s.includes(id) ? s.filter((x) => x !== id) : [...s, id]);
    else setSel([id]);
  };
  const openCtx = (e) => {
    const box = e.currentTarget.closest(".win").getBoundingClientRect();
    setCtx({ x: Math.min(e.clientX - box.left, box.width - 244), y: Math.min(e.clientY - box.top, box.height - 380) });
  };

  const TOOLS = [
    { id: "new", icon: <Ic d={I.plus} w={2} />, t: "新建下载", on: () => setSheet("add") },
    { id: "start", icon: running ? <PauseIc /> : <PlayIc />, t: running ? "全部暂停" : "全部开始", on: allToggle },
    { id: "remove", icon: <Ic d={I.trash} />, t: sel.length > 1 ? `删除 ${sel.length} 项` : "删除", on: () => sel.length && remove(sel), off: !sel.length },
    { id: "folder", icon: <Ic d={I.folder} />, t: "打开文件夹", on: () => cur && say(`已在文件夹中显示“${cur.name}”`), off: !cur },
    { id: "sniff", icon: <Ic d={I.film} />, t: "视频嗅探", on: () => setSheet("sniff") },
    { id: "info", icon: <Ic d={I.info} />, t: detail ? "收起详情" : "展开详情", on: () => setDetail((d) => !d), press: detail },
    { id: "prefs", icon: <Ic d={I.gear} />, t: "设置", on: () => setSheet("prefs") },
  ];

  return (
    <div className="desk">
      {boss && <div className="bosshint"><b>已进入后台</b>下载继续进行。再按 Ctrl + Alt + H 恢复</div>}
      <div className="stage" data-docked={docked} data-boss={boss}>
        {islandOn && <div className="isl-slot"><Island total={total} hist={hist} up={up} /></div>}
        <div className="win" data-detail={detail}
          onDragOver={(e) => { e.preventDefault(); setDrop(true); }}
          onDragLeave={() => setDrop(false)}
          onDrop={(e) => { e.preventDefault(); setDrop(false); say("已添加 1 个链接"); }}>
          <div className="nav">
            <div className="dots"><span className="dot r"></span><span className="dot y"></span><span className="dot g"></span></div>
            <div className="nav__t">Mediation Mini</div>
            <button className="nav__b" onMouseDown={(e) => e.stopPropagation()}
              onClick={() => setMenu(menu === "add" ? null : "add")}><Ic d={I.plus} w={2.1} size={20} /></button>
            {menu === "add" && <AddMenu onClose={() => setMenu(null)} onPick={(k) => setSheet(k)} />}
          </div>

          <div className="wbody">
            <div className="wmain">
              <div className="head">
                <h1>下载</h1>
                <p>
                  {active.length} 项进行中 · 合计 {total >= 1024 ? (total / 1024).toFixed(1) + " MB/s" : Math.round(total) + " KB/s"}
                  {cap !== "不限" ? ` · 限速 ${cap}` : ""}
                  {sel.length > 1 ? ` · 已选 ${sel.length} 项` : ""}
                </p>
                <div className="headrow">
                  <div className="search">
                    <Ic d={I.search} w={2} size={15} />
                    <input placeholder="搜索任务、来源或类型" value={q} onChange={(e) => setQ(e.target.value)} />
                  </div>
                  <button className="iconbtn" title="显示与排序" onMouseDown={(e) => e.stopPropagation()}
                    onClick={() => setMenu(menu === "view" ? null : "view")}><Ic d={I.gauge} w={1.9} /></button>
                </div>
                {menu === "view" && <ViewMenu onClose={() => setMenu(null)} dense={dense} setDense={setDense}
                  sortKey={sortKey} setSortKey={setSortKey} sortDir={sortDir} setSortDir={setSortDir} />}
              </div>

              <Segmented value={tab} onChange={setTab} items={[
                { id: "all", label: "全部" }, { id: "active", label: `进行中 ${active.length}` }, { id: "done", label: `已完成 ${done.length}` },
              ]} />

              <div className="chips">
                {CATS.map((c) => (
                  <button key={c.id} className="chip" aria-pressed={cat === c.id} onClick={() => setCat(c.id)}>{c.label}</button>
                ))}
              </div>

              <div className="scroll">
                {groups.map(([label, list], gi) => list.length > 0 && (
                  <div key={gi} style={gi > 0 ? { marginTop: 18 } : undefined}>
                    {label && <div className="ghdr">{label}</div>}
                    <div className={"card" + (dense ? " dense" : "")}>
                      {list.map((t) => (
                        <Row key={t.id} t={t} sel={sel.includes(t.id)} onSel={(e) => pick(t.id, e)}
                          onToggle={() => toggle(t.id)} onRemove={() => remove([t.id])} onCtx={openCtx} />
                      ))}
                    </div>
                  </div>
                ))}
                {shown.length === 0 && <p className="fnote" style={{ textAlign: "center", padding: "34px 0" }}>没有符合条件的任务</p>}
              </div>
            </div>

            {detail && <Inspector t={cur} onClose={() => setDetail(false)} onPatch={(p) => cur && patch(cur.id, p)} />}
          </div>

          <div className="bar">
            {TOOLS.map((b) => (
              <button key={b.id} className="tb" title={b.t} disabled={b.off} aria-pressed={b.press} onClick={b.on}>{b.icon}</button>
            ))}
          </div>

          {sheet === "add" && <AddSheet onClose={() => setSheet(null)} />}
          {sheet === "batch" && <BatchSheet onClose={() => setSheet(null)} onAdd={(n) => say(`已添加 ${n} 个任务`)} />}
          {sheet === "torrent" && <TorrentSheet onClose={() => setSheet(null)} onAdd={(n) => say(`已选择 ${n} 个文件`)} />}
          {sheet === "prefs" && <SettingsSheet onClose={() => setSheet(null)} cap={cap} setCap={setCap} />}
          {sheet === "sniff" && <SniffSheet onClose={() => setSheet(null)} onAdd={() => say("已加入下载队列")} />}
          {ctx && <ContextMenu x={ctx.x} y={ctx.y} t={cur} onClose={() => setCtx(null)}
            onToggle={() => cur && toggle(cur.id)} onRemove={() => cur && remove([cur.id])}
            onDetail={() => setDetail(true)} onMove={(d) => cur && move(cur.id, d)} />}
          {toast && <div className="toast">{toast}</div>}
          {drop && <div className="drop">松开以添加下载</div>}
          {banner && (
            <div className="banner">
              <div className="banner__i"><Ic d={I.check} w={2.6} /></div>
              <div style={{ minWidth: 0, flex: 1 }}>
                <div className="banner__t">下载完成</div>
                <div className="banner__s">{banner.name} · {mb(banner.size)}</div>
              </div>
              <button className="nav__b" onClick={() => setBanner(null)}>打开</button>
            </div>
          )}
        </div>
      </div>

      <div className="tray" onMouseDown={(e) => e.stopPropagation()} onClick={() => setMenu(menu === "tray" ? null : "tray")}>
        <span className="tray__i"><Ic d={I.plus} w={2.6} /></span>
        {total >= 1024 ? (total / 1024).toFixed(1) + " MB/s" : Math.round(total) + " KB/s"}
        {menu === "tray" && <TrayMenu onClose={() => setMenu(null)} docked={docked} running={running} onAct={(k) => {
          if (k === "dock") setDocked((d) => !d);
          else if (k === "island") setIslandOn((v) => !v);
          else if (k === "toggle") allToggle();
          else if (k === "prefs") setSheet("prefs");
          else if (k === "boss") setBoss(true);
          else if (k === "show") { setDocked(false); setIslandOn(true); }
          else if (k === "quit") say("已退出（演示）");
        }} />}
      </div>
    </div>
  );
}

Object.assign(window, {
  Row, Inspector, Island, Segmented, Switch, Sheet, AddSheet, SniffSheet, SettingsSheet,
  ContextMenu, SEED, mb, spd, eta, STATE_CN, I, Ic, PlayIc, PauseIc, CATS,
});
