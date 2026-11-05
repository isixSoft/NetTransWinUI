// Mediation Mini v2 — shell with Winamp-style magnetic docking.
// Two independent frames: the task window and the inspector. Drag either by
// its title bar; bring an edge within 18px of the other and it snaps flush and
// then travels with the main window until you pull it away.
const { useState, useEffect, useRef, useCallback } = React;

const MW = 536, MH = 680, SNAP = 18, ISL = 68;
const FOLD = 5;
const DIM = { right: { w: MW, h: MH }, left: { w: MW, h: MH }, bottom: { w: MW, h: MH }, top: { w: MW, h: MH } };

function App2() {
  const deskRef = useRef(null);
  const [m, setM] = useState({ x: 60, y: 120 });
  const [s, setS] = useState({ x: 596, y: 120 });
  const [dock, setDock] = useState("right");     // right | left | bottom | top | null
  const [detail, setDetail] = useState(true);
  const [hint, setHint] = useState(null);
  const [drag, setDrag] = useState(null);

  const [tasks, setTasks] = useState(SEED);
  const [sel, setSel] = useState([1]);
  const [tab, setTab] = useState("all");
  const [cat, setCat] = useState("all");
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
  const [edge, setEdge] = useState(false);
  const [boss, setBoss] = useState(false);
  const [islandOn, setIslandOn] = useState(true);
  const [drop, setDrop] = useState(false);
  const [banner, setBanner] = useState(null);
  const [search, setSearch] = useState(false);
  const [snapFx, setSnapFx] = useState(false);
  const [easing, setEasing] = useState(false);
  const [open5, setOpen5] = useState({});
  const say = (t) => { setToast(t); setTimeout(() => setToast(null), 1700); };

  const fitPair = useCallback((mm, ss, dw, dh) => {
    const el = deskRef.current;
    if (!el) return [mm, ss];
    const W = el.clientWidth, H = el.clientHeight;
    const x0 = Math.min(mm.x, ss.x), x1 = Math.max(mm.x + MW, ss.x + dw);
    const y0 = Math.min(mm.y, ss.y), y1 = Math.max(mm.y + MH, ss.y + dh);
    let dx = 0, dy = 0;
    if (x0 < 14) dx = 14 - x0; else if (x1 > W - 14) dx = (W - 14) - x1;
    if (y0 < ISL) dy = ISL - y0; else if (y1 > H - 10) dy = (H - 10) - y1;
    return [{ x: mm.x + dx, y: mm.y + dy }, { x: ss.x + dx, y: ss.y + dy }];
  }, []);

  const settle = (d, mm, sp) => {
    const dd = DIM[d];
    const [nm, ns] = fitPair(mm, sp, dd.w, dd.h);
    setEasing(true); setM(nm); setS(ns); setDock(d);
    setSnapFx(true);
    setTimeout(() => { setEasing(false); setSnapFx(false); }, 320);
  };

  const detach = () => {
    setEasing(true); setDock(null);
    setS((p) => ({ x: p.x + 34, y: p.y + 26 }));
    setTimeout(() => setEasing(false), 280);
  };
  const attach = (d = "right") => settle(d, m, dockPosRef.current(d, m));

  /* centre the pair on first paint */
  useEffect(() => {
    const el = deskRef.current; if (!el) return;
    const W = el.clientWidth, H = el.clientHeight;
    const x = Math.max(14, (W - MW - DIM.right.w) / 2), y = ISL;
    const [nm, ns] = fitPair({ x, y }, { x: x + MW, y }, DIM.right.w, DIM.right.h);
    setM(nm); setS(ns);
  }, []);

  /* dock positions relative to the main window */
  const dockPos = useCallback((d, mm) => ({
    right: { x: mm.x + MW, y: mm.y },
    left: { x: mm.x - DIM.left.w, y: mm.y },
    bottom: { x: mm.x, y: mm.y + MH },
    top: { x: mm.x, y: mm.y - DIM.top.h },
  }[d]), []);
  const dockPosRef = useRef(dockPos);
  dockPosRef.current = dockPos;

  const nearest = useCallback((sp, mm) => {
    let best = null;
    for (const d of ["right", "left", "bottom", "top"]) {
      const p = dockPos(d, mm);
      const dx = Math.abs(sp.x - p.x), dy = Math.abs(sp.y - p.y);
      if (dx <= SNAP && dy <= SNAP && (!best || dx + dy < best.dist)) best = { d, p, dist: dx + dy };
    }
    return best;
  }, [dockPos]);

  /* drag */
  const start = (which) => (e) => {
    if (e.button !== 0 || e.target.closest("button")) return;
    e.preventDefault();
    document.body.classList.add("dragging");
    const base = which === "m" ? m : s;
    setDrag({ which, ox: e.clientX - base.x, oy: e.clientY - base.y });
  };

  useEffect(() => {
    if (!drag) return;
    const el = deskRef.current;
    const move = (e) => {
      const W = el.clientWidth, H = el.clientHeight;
      let x = e.clientX - drag.ox, y = e.clientY - drag.oy;
      if (drag.which === "m") {
        x = Math.min(Math.max(-MW + 80, x), W - 80);
        y = Math.min(Math.max(ISL, y), H - 60);
        const dx = x - m.x, dy = y - m.y;
        setM({ x, y });
        if (dock && detail) setS((p) => ({ x: p.x + dx, y: p.y + dy }));
        setHint(null);
      } else {
        x = Math.min(Math.max(-DIM.right.w - 40, x), W + 40);
        y = Math.min(Math.max(-DIM.bottom.h - 40, y), H + 40);
        setS({ x, y });
        const n = nearest({ x, y }, m);
        setHint(n ? n.d : null);
      }
    };
    const up = () => {
      document.body.classList.remove("dragging");
      if (drag.which === "s") {
        const n = nearest(s, m);
        if (n) settle(n.d, m, n.p);
        else {
          const dd = DIM.right;
          const [nm, ns] = fitPair(m, s, dd.w, dd.h);
          setDock(null); setEasing(true); setM(nm); setS(ns);
          setTimeout(() => setEasing(false), 280);
        }
        setHint(null);
      }
      setDrag(null);
    };
    window.addEventListener("mousemove", move);
    window.addEventListener("mouseup", up);
    return () => { window.removeEventListener("mousemove", move); window.removeEventListener("mouseup", up); };
  }, [drag, m, s, dock, detail, nearest]);

  /* simulation */
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
  const moveQ = (id, dir) => setTasks((ts) => {
    const rest = ts.filter((t) => t.id !== id), one = ts.find((t) => t.id === id);
    return dir < 0 ? [one, ...rest] : [...rest, one];
  });

  const cur = tasks.find((t) => t.id === sel[sel.length - 1]);
  const scoped = tasks.filter((t) => cat === "all" || t.cat === cat);
  const active = scoped.filter((t) => t.state !== "done");
  const done = scoped.filter((t) => t.state === "done");
  const SORT = {
    added: (a, b) => a.id - b.id, name: (a, b) => a.name.localeCompare(b.name),
    size: (a, b) => a.size - b.size, progress: (a, b) => a.got / a.size - b.got / b.size, speed: (a, b) => a.kbs - b.kbs,
  };
  const shown = (tab === "active" ? active : tab === "done" ? done : scoped)
    .filter((t) => !q || t.name.toLowerCase().includes(q.toLowerCase()))
    .slice().sort((a, b) => (sortDir === "asc" ? 1 : -1) * SORT[sortKey](a, b));
  const groups = [[null, shown]];

  const pick = (id, e) => {
    if (e && (e.metaKey || e.ctrlKey)) setSel((v) => v.includes(id) ? v.filter((x) => x !== id) : [...v, id]);
    else setSel([id]);
  };
  const openCtx = (e) => {
    const box = e.currentTarget.closest(".frame").getBoundingClientRect();
    setCtx({ x: Math.min(e.clientX - box.left, box.width - 244), y: Math.min(e.clientY - box.top, box.height - 380) });
  };

  const TOOLS = [
    { id: "new", icon: <Ic d={I.plus} w={2} />, t: "新建下载", on: () => setMenu(menu === "add" ? null : "add") },
    { id: "start", icon: running ? <PauseIc /> : <PlayIc />, t: running ? "全部暂停" : "全部开始", on: allToggle },
    { id: "remove", icon: <Ic d={I.trash} />, t: sel.length > 1 ? `删除 ${sel.length} 项` : "删除", on: () => sel.length && remove(sel), off: !sel.length },
    { id: "folder", icon: <Ic d={I.folder} />, t: "打开文件夹", on: () => cur && say(`已在文件夹中显示“${cur.name}”`), off: !cur },
    { id: "sniff", icon: <Ic d={I.film} />, t: "视频嗅探", on: () => setSheet("sniff") },
    { id: "info", icon: <Ic d={I.info} />, t: detail ? "关闭详情窗口" : "打开详情窗口", press: detail, on: () => setDetail((d) => !d) },
    { id: "prefs", icon: <Ic d={I.gear} />, t: "设置", on: () => setSheet("prefs") },
  ];

  /* snap guide line */
  const guide = () => {
    if (!hint) return null;
    const st = { position: "absolute", zIndex: 9 };
    if (hint === "right") return <div className="snapline" style={{ ...st, left: m.x + MW - 1.5, top: m.y + 8, width: 3, height: MH - 16 }}></div>;
    if (hint === "left") return <div className="snapline" style={{ ...st, left: m.x - 1.5, top: m.y + 8, width: 3, height: MH - 16 }}></div>;    if (hint === "bottom") return <div className="snapline" style={{ ...st, left: m.x + 8, top: m.y + MH - 1.5, width: MW - 16, height: 3 }}></div>;
    return <div className="snapline" style={{ ...st, left: m.x + 8, top: m.y - 1.5, width: MW - 16, height: 3 }}></div>;
  };

  const dims = DIM[dock || "right"];
  const wide = false;
  const bonded = detail && dock;
  const mainBond = bonded ? { right: "bond-r", left: "bond-l", bottom: "bond-b", top: "bond-t" }[dock] : "";
  const sideBond = bonded ? { right: "bond-l", left: "bond-r", bottom: "bond-t", top: "bond-b" }[dock] : "";

  return (
    <div className="desk" ref={deskRef} data-boss={boss}>
      {boss && <div className="bosshint"><b>已进入后台</b>下载继续进行。再按 Ctrl + Alt + H 恢复</div>}
      {guide()}

      {islandOn && (
        <div className="isl-slot" style={{ left: m.x + MW / 2, top: m.y - 66 }}>
          <Island total={total} hist={hist} up={up} />
        </div>
      )}

      <div className={`frame win2 ${mainBond} ${easing ? "easing" : ""} ${edge ? "edgehide" : ""} ${drag && drag.which === "m" ? "drag" : ""}`}
        style={{ left: m.x, top: m.y }}
        onDragOver={(e) => { e.preventDefault(); setDrop(true); }}
        onDragLeave={() => setDrop(false)}
        onDrop={(e) => { e.preventDefault(); setDrop(false); say("已添加 1 个链接"); }}>
        <div className="nav" onMouseDown={start("m")}>
          <div className="navleft">
            <div className="dots"><span className="dot r"></span><span className="dot y"></span><span className="dot g"></span></div>
            {cat !== "all" && (
              <button className="filterchip sm" title="清除分类筛选" onMouseDown={(e) => e.stopPropagation()} onClick={() => setCat("all")}>
                {CATS.find((c) => c.id === cat).label}<Ic d={I.x} w={2.6} size={10} />
              </button>
            )}
          </div>
          {search
            ? <input className="navsearch" autoFocus placeholder="搜索任务" value={q}
                onMouseDown={(e) => e.stopPropagation()} onChange={(e) => setQ(e.target.value)}
                onBlur={() => { if (!q) setSearch(false); }} />
            : <div className="nav__t">下载 <em>· {active.length} 项 · {total >= 1024 ? (total / 1024).toFixed(1) + " MB/s" : Math.round(total) + " KB/s"}{sel.length > 1 ? ` · 已选 ${sel.length}` : ""}</em></div>}
          <div className="navright">
            <button className="nav__b" title="搜索" onMouseDown={(e) => e.stopPropagation()}
              onClick={() => { setSearch((v) => !v); if (search) setQ(""); }}><Ic d={I.search} w={2.1} size={17} /></button>
            <button className={"nav__b" + (cat !== "all" ? " act" : "")} title="显示与排序" onMouseDown={(e) => e.stopPropagation()}
              onClick={() => setMenu(menu === "view" ? null : "view")}><Ic d={I.gauge} w={2} size={18} /></button>
            <button className="nav__b" title="新建" onMouseDown={(e) => e.stopPropagation()}
              onClick={() => setMenu(menu === "add" ? null : "add")}><Ic d={I.plus} w={2.1} size={19} /></button>
          </div>
          {menu === "add" && <AddMenu onClose={() => setMenu(null)} onPick={(k) => setSheet(k)} />}
        </div>

        <Segmented value={tab} onChange={setTab} items={[
          { id: "all", label: "全部" }, { id: "active", label: `进行中 ${active.length}` }, { id: "done", label: `已完成 ${done.length}` },
        ]} />

        <div className="scroll">
          {groups.map(([label, list], gi) => list.length > 0 && (() => {
            const key = label || "one", open = !!open5[key], vis = open ? list : list.slice(0, FOLD), hid = list.length - vis.length;
            return (
            <div key={gi} style={gi > 0 ? { marginTop: 18 } : undefined}>
              {label && <div className="ghdr">{label}{list.length > FOLD && <span className="ghdr__n">{open ? list.length : `${FOLD} / ${list.length}`}</span>}</div>}
              <div className={"card" + (dense ? " dense" : "")}>
                {vis.map((t) => (
                  <Row key={t.id} t={t} sel={sel.includes(t.id)} onSel={(e) => pick(t.id, e)}
                    onToggle={() => toggle(t.id)} onRemove={() => remove([t.id])} onCtx={openCtx} />
                ))}
                {(hid > 0 || open) && list.length > FOLD && (
                  <button className="morerow" onClick={() => setOpen5((v) => ({ ...v, [key]: !open }))}>
                    <span className="morerow__c" data-open={open}><Ic d={I.down} w={2.2} size={15} /></span>
                    {open ? "收起" : `展开更多 ${hid} 项`}
                  </button>
                )}
              </div>
            </div>
            );
          })())}
          {shown.length === 0 && <p className="fnote" style={{ textAlign: "center", padding: "34px 0" }}>没有符合条件的任务</p>}
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
          onDetail={() => setDetail(true)} onMove={(d) => cur && moveQ(cur.id, d)} />}
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

      {detail && (
        <div className={`frame sidewin ${sideBond} ${easing ? "easing" : ""} ${snapFx ? "snapped" : ""} ${drag && drag.which === "s" ? "drag" : ""}`}
          style={{ left: s.x, top: s.y, width: dims.w, height: dims.h }}>
          <div className="nav" onMouseDown={start("s")}>
            <div className="navleft">
              <span className="grip"><i></i><i></i><i></i></span>
              {dock
                ? <button className="attach" title="点击分离" onMouseDown={(e) => e.stopPropagation()} onClick={detach}>
                    <Ic d={I.copy} w={2} />已吸附
                  </button>
                : <button className="attach" style={{ color: "var(--label2)", background: "var(--fill)" }}
                    title="点击吸附到主窗口" onMouseDown={(e) => e.stopPropagation()} onClick={() => attach("right")}>
                    已分离
                  </button>}
            </div>
            <div className="nav__t">详细信息</div>
            <button className="nav__b" onMouseDown={(e) => e.stopPropagation()} onClick={() => setDetail(false)}>
              <Ic d={I.x} w={2.3} size={17} />
            </button>
          </div>
          <Inspector t={cur} onPatch={(p) => cur && patch(cur.id, p)}
            onRedl={(t) => { patch(t.id, { state: "run", got: 0, kbs: 420 + Math.random() * 600, newer: null }); say("已按新版本重新下载"); }} />
        </div>
      )}

      {menu === "view" && <ViewMenu x={m.x + MW - 242} y={m.y + 46} onClose={() => setMenu(null)}
        dense={dense} setDense={setDense} sortKey={sortKey} setSortKey={setSortKey}
        sortDir={sortDir} setSortDir={setSortDir} cat={cat} setCat={setCat} cats={CATS} />}

      <div className="tray" onMouseDown={(e) => e.stopPropagation()} onClick={() => setMenu(menu === "tray" ? null : "tray")}>
        <span className="tray__i"><Ic d={I.plus} w={2.6} /></span>
        {total >= 1024 ? (total / 1024).toFixed(1) + " MB/s" : Math.round(total) + " KB/s"}
        {menu === "tray" && <TrayMenu onClose={() => setMenu(null)} docked={edge} running={running} onAct={(k) => {
          if (k === "dock") { setEdge((v) => !v); if (!edge) { const W = deskRef.current.clientWidth; setM((p) => ({ ...p, x: W - MW })); } }
          else if (k === "island") setIslandOn((v) => !v);
          else if (k === "toggle") allToggle();
          else if (k === "prefs") setSheet("prefs");
          else if (k === "boss") setBoss(true);
          else if (k === "show") { setEdge(false); setIslandOn(true); }
          else if (k === "quit") say("已退出（演示）");
        }} />}
      </div>
    </div>
  );
}

ReactDOM.createRoot(document.getElementById("root")).render(<App2 />);
