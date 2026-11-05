// Mediation Mini v2 — additional dialogs and menus.
// Loaded before mini-ios2.jsx; exports onto window.
const { useState: uS, useEffect: uE } = React;

const Ic2 = ({ d, w = 1.7, size }) => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={w} strokeLinecap="round" strokeLinejoin="round"
    style={size ? { width: size, height: size } : undefined}><path d={d} /></svg>
);
const P = {
  chev: "M9 6l6 6-6 6", check: "M20 6L9 17l-5-5", plus: "M12 5v14M5 12h14",
  layers: "M12 3l9 5-9 5-9-5 9-5zM3 13l9 5 9-5M3 17l9 5 9-5",
  magnet: "M6 4v7a6 6 0 0 0 12 0V4h-4v7a2 2 0 0 1-4 0V4z",
  film: "M4 5h16v14H4zM4 9h16M4 15h16M8 5v14M16 5v14",
  sortA: "M4 6h10M4 12h7M4 18h4M17 5v14M17 19l3-3M17 19l-3-3",
  grid: "M4 6h16M4 12h16M4 18h16",
  rows: "M4 5h16v6H4zM4 13h16v6H4z",
  eye: "M2 12s3.6-6.5 10-6.5S22 12 22 12s-3.6 6.5-10 6.5S2 12 2 12zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z",
  power: "M12 3v9M6.5 6.6a8 8 0 1 0 11 0",
  win: "M4 5h16v14H4zM4 9h16",
  pin: "M12 21v-6M8 4h8l-1 7 3 3H6l3-3z",
};
const Sw2 = ({ on, onClick }) => <button className="sw" role="switch" aria-checked={on} onClick={onClick}><i></i></button>;

function Sheet2({ title, left, right, onClose, children }) {
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

/* 批量下载 — 站点筛选 + 后缀筛选 + 结果勾选 */
function BatchSheet({ onClose, onAdd }) {
  const [stage, setStage] = uS("form");
  const [sameSite, setSameSite] = uS(true);
  const [picks, setPicks] = uS({ 0: true, 1: true, 3: true });
  const found = [
    ["chapter-01-intro.pdf", "4.2 MB"], ["chapter-02-tokens.pdf", "6.8 MB"],
    ["chapter-03-motion.pdf", "11.4 MB"], ["assets-bundle.zip", "128 MB"],
    ["cover-artwork.png", "2.1 MB"], ["errata-notes.pdf", "0.4 MB"],
  ];
  const n = Object.values(picks).filter(Boolean).length;
  return (
    <Sheet2 title="批量下载" onClose={onClose}
      right={<button className="nav__b w" disabled={stage !== "list"} style={stage !== "list" ? { color: "var(--label3)" } : undefined}
        onClick={() => { onAdd(n); onClose(); }}>添加 {stage === "list" ? n : ""}</button>}>
      {stage === "form" ? (
        <>
          <div className="card"><div className="frow"><input type="text" className="l" defaultValue="https://docs.internal/handbook/" autoFocus /></div></div>
          <p className="fnote">抓取该页面上的所有可下载资源。</p>
          <div className="card" style={{ marginTop: 14 }}>
            <div className="frow"><span className="flabel">抓取深度</span>
              <select defaultValue="1 层"><option>仅本页</option><option>1 层</option><option>2 层</option><option>3 层</option></select>
            </div>
            <div className="frow"><span className="flabel">仅限本站</span><Sw2 on={sameSite} onClick={() => setSameSite(!sameSite)} /></div>
            <div className="frow"><span className="flabel">后缀筛选</span><input type="text" defaultValue="pdf; zip; png" /></div>
            <div className="frow"><span className="flabel">最小文件</span>
              <select defaultValue="不限"><option>不限</option><option>100 KB</option><option>1 MB</option><option>10 MB</option></select>
            </div>
          </div>
          <button className="big" onClick={() => { setStage("scan"); setTimeout(() => setStage("list"), 1000); }}
            disabled={stage === "scan"}>{stage === "scan" ? "抓取中…" : "开始抓取"}</button>
        </>
      ) : stage === "scan" ? (
        <p className="fnote" style={{ textAlign: "center", padding: "48px 0" }}>正在抓取页面资源…</p>
      ) : (
        <>
          <div className="ghdr">找到 {found.length} 个资源 · 已选 {n}</div>
          <div className="card">
            {found.map(([name, size], i) => (
              <div className="frow" key={i} style={{ cursor: "pointer" }} onClick={() => setPicks({ ...picks, [i]: !picks[i] })}>
                <span className="flabel" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{name}</span>
                <span className="fval">{size}</span>
                <span style={{ color: picks[i] ? "var(--blue)" : "var(--label3)", display: "grid", placeItems: "center", width: 18 }}><Ic2 d={P.check} w={2.6} size={16} /></span>
              </div>
            ))}
          </div>
          <button className="big plain" onClick={() => setPicks(Object.fromEntries(found.map((_, i) => [i, true])))}>全选</button>
        </>
      )}
    </Sheet2>
  );
}

/* 种子内文件选择 */
function TorrentSheet({ onClose, onAdd }) {
  const files = [
    ["archlinux-2026.08-x86_64.iso", "3.14 GB", true],
    ["archlinux-bootstrap.tar.zst", "182 MB", true],
    ["sha256sums.txt", "1 KB", true],
    ["sha256sums.txt.sig", "1 KB", false],
    ["magnet-mirrors.txt", "2 KB", false],
  ];
  const [on, setOn] = uS(Object.fromEntries(files.map((f, i) => [i, f[2]])));
  const n = Object.values(on).filter(Boolean).length;
  return (
    <Sheet2 title="种子内容" onClose={onClose}
      right={<button className="nav__b w" onClick={() => { onAdd(n); onClose(); }}>下载 {n}</button>}>
      <div className="card">
        <div className="frow"><span className="flabel">名称</span><span className="fval">archlinux-2026.08</span></div>
        <div className="frow"><span className="flabel">总大小</span><span className="fval">3.32 GB</span></div>
        <div className="frow"><span className="flabel">节点 / 种子</span><span className="fval">12 / 4</span></div>
      </div>
      <div className="ghdr" style={{ marginTop: 16 }}>选择要下载的文件</div>
      <div className="card">
        {files.map(([name, size], i) => (
          <div className="frow" key={i} style={{ cursor: "pointer" }} onClick={() => setOn({ ...on, [i]: !on[i] })}>
            <span className="flabel" style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{name}</span>
            <span className="fval">{size}</span>
            <span style={{ color: on[i] ? "var(--blue)" : "var(--label3)", display: "grid", placeItems: "center", width: 18 }}><Ic2 d={P.check} w={2.6} size={16} /></span>
          </div>
        ))}
      </div>
      <p className="fnote">未勾选的文件不会占用磁盘空间。</p>
    </Sheet2>
  );
}

/* 通用弹出菜单（新建来源、排序与显示、托盘、悬浮窗） */
function Popover({ x, y, left, right, bottom, onClose, children, width }) {
  uE(() => {
    const h = () => onClose();
    window.addEventListener("mousedown", h); window.addEventListener("keydown", h);
    return () => { window.removeEventListener("mousedown", h); window.removeEventListener("keydown", h); };
  }, [onClose]);
  const st = { width: width || 232 };
  if (x !== undefined) st.left = x;
  if (left !== undefined) st.left = left;
  if (y !== undefined) st.top = y;
  if (right !== undefined) st.right = right;
  if (bottom !== undefined) st.bottom = bottom;
  return <div className="ctx" style={st} onMouseDown={(e) => e.stopPropagation()}>{children}</div>;
}
const MI = ({ label, icon, on, close, cls, mark }) => (
  <button className={cls} onClick={() => { on && on(); close && close(); }}>
    {label}{mark ? <Ic2 d={P.check} w={2.6} /> : icon}
  </button>
);

function AddMenu({ onClose, onPick }) {
  return (
    <Popover right={12} y={40} onClose={onClose} width={222}>
      <MI label="新建下载…" icon={<Ic2 d={P.plus} w={2} />} on={() => onPick("add")} close={onClose} />
      <MI label="批量下载…" icon={<Ic2 d={P.layers} />} on={() => onPick("batch")} close={onClose} />
      <MI label="打开种子 / 磁力链…" icon={<Ic2 d={P.magnet} />} on={() => onPick("torrent")} close={onClose} />
      <MI label="视频嗅探…" icon={<Ic2 d={P.film} />} on={() => onPick("sniff")} close={onClose} />
    </Popover>
  );
}

function ViewMenu({ onClose, dense, setDense, sortKey, setSortKey, sortDir, setSortDir, cat, setCat, cats, x, y }) {
  const keys = [["added", "加入时间"], ["name", "名称"], ["size", "大小"], ["progress", "进度"], ["speed", "速度"]];
  const arrow = sortDir === "asc" ? "M12 5v14M6 11l6-6 6 6" : "M12 19V5M6 13l6 6 6-6";
  const [page, setPage] = uS("view");
  return (
    <Popover x={x} y={y} onClose={onClose} width={226}>
      {page === "view" ? (
        <>
          <MI label="大图" icon={<Ic2 d={P.rows} />} mark={!dense} on={() => setDense(false)} close={onClose} />
          <MI label="小图" icon={<Ic2 d={P.grid} />} mark={dense} on={() => setDense(true)} close={onClose} />
          <hr />
          {keys.map(([k, l]) => (
            <button key={k} onClick={() => { if (sortKey === k) setSortDir(sortDir === "asc" ? "desc" : "asc"); else setSortKey(k); }}>
              {l}{sortKey === k && <Ic2 d={arrow} w={2.2} />}
            </button>
          ))}
          <hr />
          <button onClick={() => setPage("cat")}>分类筛选<Ic2 d={P.chev} w={2.2} /></button>
        </>
      ) : (
        <>
          <button onClick={() => setPage("view")}><span style={{ color: "var(--blue)" }}>‹ 返回</span></button>
          <hr />
          {cats.map((c) => <MI key={c.id} label={c.label} mark={cat === c.id} on={() => setCat(c.id)} close={onClose} />)}
        </>
      )}
    </Popover>
  );
}

function TrayMenu({ onClose, onAct, docked, running }) {
  return (
    <Popover left={0} y={38} onClose={onClose} width={226}>
      <MI label="显示主窗口" icon={<Ic2 d={P.win} />} on={() => onAct("show")} close={onClose} />
      <MI label={docked ? "取消贴边" : "贴边隐藏"} icon={<Ic2 d={P.pin} />} on={() => onAct("dock")} close={onClose} />
      <MI label="隐藏悬浮窗" icon={<Ic2 d={P.eye} />} on={() => onAct("island")} close={onClose} />
      <hr />
      <MI label={running ? "全部暂停" : "全部开始"} on={() => onAct("toggle")} close={onClose} />
      <MI label="速度限制…" on={() => onAct("prefs")} close={onClose} />
      <hr />
      <MI label="老板键：全部隐藏" icon={<Ic2 d={P.power} />} on={() => onAct("boss")} close={onClose} />
      <hr />
      <MI label="退出" cls="del" on={() => onAct("quit")} close={onClose} />
    </Popover>
  );
}

Object.assign(window, { BatchSheet, TorrentSheet, AddMenu, ViewMenu, TrayMenu, Popover, MI, Ic2, P });
