# Handoff: FlashGet Mini — 双窗口下载器（iOS 风格桌面小窗）

## Overview
一个便携式下载工具的桌面小窗界面。两个独立窗口：**主窗**（任务列表 + 工具栏）和**详情窗**（单任务检查器）。
两窗等宽等高（536 × 680 px），可分别拖动；把一个窗的边缘拖到另一个窗 18px 内会**磁吸贴合**（Winamp 式），
贴合后详情窗随主窗一起移动，直到被拉开。窗上方另有一个"灵动岛"式全局速度指示器。

## About the Design Files
本包内的 HTML / JSX / CSS 是**设计稿**，用 HTML 实现的高保真原型，用来说明外观与交互，
**不是可直接复制进产品的生产代码**。任务是在目标代码库既有环境里（React / Vue / SwiftUI / WinUI / Electron 等）
按其既有模式与组件库**重新实现**这些设计；若尚无既有环境，请选择最合适的框架再实现。

## Fidelity
**High-fidelity（高保真）**。颜色、字号、间距、圆角、动效时长均为最终值，应按像素还原，
但控件应换成目标代码库自己的组件与主题机制。

## Screens / Views

### 1. 主窗（`.frame.win2`）
- **Purpose**：查看和管理下载任务。
- **Size**：536 × 680 px，圆角 16px，绝对定位在桌面层（`.desk`）上。
  阴影：`0 0 0 .5px rgba(0,0,0,.18), 0 26px 60px -14px rgba(0,0,0,.5), 0 6px 18px rgba(0,0,0,.16)`。
  贴合时贴合侧两个圆角归零（`.bond-r/.bond-l/.bond-t/.bond-b`）。
- **Layout**（纵向 flex，自上而下）：
  1. **标题栏 `.nav`** — 高 44px，padding 0 14px，背景 `rgba(249,249,249,.82)` + `backdrop-filter: saturate(180%) blur(20px)`，
     底部 .5px 分隔线。左：三个 11px 圆点（#ff5f57 / #febc2e / #28c840）+ 分类筛选 chip（有筛选时）。
     中：标题「下载 · N 项 · X MB/s」（15px / 600 / -.2px，副信息 `--label2`）。
     右：搜索、显示与排序、新建三个 17–19px 图标按钮（色 `--blue`）。整条可拖动窗口（cursor: grab）。
  2. **分段控件 `.seg`** — 全部 / 进行中 N / 已完成 N，白色滑块 + 2px 内缩，切换 tab。
  3. **列表 `.scroll`** — flex:1，padding 0 16px 4px，背景 `--group`。单一平铺列表（不再按状态分组，状态由顶部分段承担）。
     **折叠**：默认最多 5 行，第 6 行是「展开更多 N 项」按钮（`.morerow`，13.5px / 500 / `--blue`，左侧 22px 圆形箭头，
     展开态箭头旋转 180° 并变为「收起」）。启动状态刚好不出现竖向滚动条。
  4. **工具栏 `.bar`** — 高 49px，7 个 22px 图标：新建 / 全部开始暂停 / 删除 / 打开文件夹 / 视频嗅探 / 详情窗开关 / 设置。
- **任务行 `.row`**（默认态）：padding 9px 12px，gap 11px。
  36×36 圆角 9px 彩色图标块（按类型着色：#ff9500 光盘 / #af52de 视频 / #5856d6 压缩包 / #8e8e93 BT / #ff2d55 音频 / #0a84ff 文档 / #34c759 已完成）；
  中部文件名 14.5px / 500 / -.15px（可跟「优先」「新版本」标签，`.tag2` 10.5px / 600 / 圆角 9px）；
  副行 12.5px `--label2`（速度 · 剩余时间 / 已暂停 / 排队中 / 错误原因，错误为 `--red`）；
  3px 进度条（`--blue`，暂停为 `--gray`）。右侧百分比或「完成」徽章 + 12px 箭头。
  hover 背景 `rgba(0,0,0,.025)`，选中 `rgba(0,122,255,.09)`。
  **悬浮操作 `.swipe`**：hover 时从右侧滑入（transform，.18s），暂停/继续（`--gray`）与删除（`--red`）各 64px 宽、17px 图标。
- **小图模式 `.card.dense`**：行 padding 5px 12px，图标块 22×22（图标 13px），文件名 13px，副行隐藏，进度条 2px，
  分隔线左缩至 43px，悬浮按钮 46px 宽、13px 图标。

### 2. 详情窗（`.frame.sidewin`）
- **Purpose**：单个任务的完整检查器。
- **Size**：536 × 680 px，与主窗完全等宽等高，四个吸附方向尺寸一致。
- **Layout**：标题栏（可拖动，左侧 grip + 文件名，右侧关闭）→ 分段控件 → 可滚动内容区 `.side__body`（padding 0 12px 14px）。
- **四个 tab**：
  - **概览** — 98px 环形进度（`stroke-width 7`，颜色随状态：`--blue` 下载中 / `--green` 完成 / `--red` 出错 / `--gray` 暂停，
    `stroke-dashoffset` 过渡 .6s linear）+ 中心百分比（27px / 700）与速度；下方 `已接收 / 总量 · 剩余时间`；
    信息表（状态、来源、连接数、速度、校验或节点/种子、断点续传、添加时间、保存位置）；
    传输设置组（优先级三段按钮 高/普通/低、速度上限下拉）；最后是完整 URL（12.5px `--label2`，允许换行）。
  - **分块** — 96 格网格（16 列，gap 2.5px，aspect-ratio 1，圆角 1.5px；已完成 `--blue`，活动块 `--green` + 1.1s 呼吸动画）
    + 会话统计（柱状图 + 平均速度 / 峰值 / 重试次数）。
  - **连接** — 每个连接一行：编号、4px 速率条、右侧速率数值；BT 任务追加 节点 / 种子 / 分享率 / 上传。
  - **日志** — 时间戳 + 事件文本（12px，行高 1.35）。
- **新版本提示 `.newver`**（本次新增）：当任务对应的**服务器端文件有更新版本**时，内容区顶部显示蓝底提示条
  （`rgba(0,122,255,.09)`，圆角 12px，padding 10px 11px）：26px 蓝色圆形上箭头图标 +
  标题「服务器上有更新版本」（13.5px / 600）+ 副行「版本名 · 体积 · 发布于 时间」（12px `--label2`）+
  右侧「下载新版」实心蓝按钮（12.5px / 600，圆角 9px）。点击后该任务以新版本重新开始下载，提示条消失，
  主列表对应行的「新版本」标签同时移除。有新版本的任务在主列表行名后带蓝色 `.tag2.new`「新版本」标签。

### 3. 灵动岛（`.isl-slot` / `Island`）
主窗上方 66px 处居中的黑色胶囊：环形总进度 + 总速度 + 26 根 sparkline 速度柱。可在设置里关闭。

### 4. 表单浮层（`.sheet`）
新建下载 / 批量添加 / 种子文件选择 / 视频嗅探 / 设置，均为窗口内 iOS 半屏 sheet：
顶部 grab 条 + 取消/标题/完成，内容为 iOS 分组表单（`.card` + `.frow`，行高 ≥44px，
开关 51×31px `--green`，下拉 `--fill` 背景）。入场动画 `translateY(100%) → 0`，.34s `cubic-bezier(.32,.72,0,1)`。

### 5. 右键菜单（`.ctx`）
244px 宽，圆角 12px，白底 + 模糊，条目 高亮 hover；包含打开文件、在文件夹中显示、暂停/继续、重新下载、
详细信息、上移/下移队列、复制链接、删除。

## Interactions & Behavior
- **拖动**：标题栏 mousedown 起拖，窗体绝对定位跟随鼠标；拖动中 `body.dragging` 禁用选中。
- **磁吸**：拖动详情窗时，与主窗任一边距离 ≤18px 显示 3px 蓝色吸附引导线（`.snapline`，带 12px 蓝色外发光）；
  松手后 `.24s cubic-bezier(.32,.72,0,1)` 缓动贴合，并播放 `snapIn` 缩放动画（.982 → 1.006 → 1）。
  贴合后主窗移动时详情窗同步位移；拉开距离即解除贴合。
- **列表折叠**：每组 5 条上限，展开/收起为即时状态切换，箭头 .2s 旋转。
- **hover 操作条**：`.swipe` transform 过渡 .18s。
- **选择**：单击选中（驱动详情窗内容），Cmd/Ctrl 单击多选，工具栏删除按选中集合批量删除。
- **贴边隐藏**：开启后窗口未 hover 时 `translateX(calc(100% - 36px))`，.34s 缓动。
- **老板键**：Ctrl + Alt + H 全部窗口 opacity → 0 并显示提示条。
- **拖放**：拖链接到主窗显示「松开以添加下载」蒙层。
- **完成通知**：窗内底部 banner（图标 + 文件名 + 体积 + 「打开」）。
- **Toast**：`.toast` 1.7s 自动消失。

## State Management
```
tasks[]        任务数组（id, name, kind, tint, cat, size, got, kbs, state, conns, host, url,
               priority, limit, retries, added, checksum, err, peers/seeds/ratio/up, log[], newer)
sel[]          选中任务 id（末位为详情窗当前任务）
tab            all | active | done
cat            分类筛选
q / search     搜索词 / 搜索框展开
sortKey/sortDir added | name | size | progress | speed，asc | desc
open5{}        每个分组的展开状态（折叠模式）
detail         详情窗是否显示
dock           right | left | bottom | top | null（磁吸方向）
m / s          主窗 / 详情窗坐标
hint/drag/easing/snapFx  拖动与吸附过渡态
dense          小图模式
islandOn / edge / boss   灵动岛 / 贴边隐藏 / 老板键
sheet / menu / ctx / toast / banner / drop  浮层与瞬时 UI
```
进度用 setInterval 模拟推进（每个 run 任务按 kbs 增加 got，完成后置 done 并触发 banner）。
`newer` 字段代表服务器端存在更新版本，实际实现应来自服务端版本查询接口。

## Design Tokens
```
--blue #007aff   --green #34c759   --red #ff3b30   --orange #ff9500   --gray #8e8e93
--label #000     --label2 rgba(60,60,67,.6)   --label3 rgba(60,60,67,.3)
--sep rgba(60,60,67,.18)   --fill rgba(120,120,128,.12)   --fill2 rgba(120,120,128,.2)
--group #f2f2f7  --card #fff   --r 10px（卡片圆角；窗口 16px）
字体：-apple-system / SF Pro Text / SF Pro Display，回退 Inter Tight、system-ui
字号：10.5 / 11.5 / 12 / 12.5 / 13.5 / 14.5 / 15 / 17 / 27 / 44 px
字重：400 / 500 / 600 / 700；字距 -.15 至 -1.6px（越大越紧）
间距：4 / 6 / 8 / 9 / 11 / 12 / 14 / 16 / 18 px
窗口尺寸：536 × 680；标题栏 44；工具栏 49；吸附阈值 18；灵动岛偏移 66
动效：.12s 悬浮反馈；.18s 滑出；.22s 淡入；.24s 吸附/开关；.34s sheet / 贴边；
      缓动统一 cubic-bezier(.32,.72,0,1)
数字一律 font-variant-numeric: tabular-nums
```

## Assets
无位图资源。所有图标为内联 24×24 stroke SVG path（定义在 `mini-ios2.jsx` 的 `I` 对象里），
stroke-width 1.7–2.6，round cap/join。图标可换成目标代码库自己的图标库中语义相同的图标。

## Files
- `Mediation Mini iOS v2.html` — 入口（引入下面三个脚本 + 样式）
- `mini-ios2.css` — 全部样式与 token
- `mini-ios2.jsx` — 图标集、种子数据、Row、Segmented、Switch、Inspector（四 tab 详情窗）、各 Sheet、右键菜单、Island
- `mini-ios2-more.jsx` — 附加浮层（批量添加、种子选择、显示与排序菜单等）
- `mini-ios2-app.jsx` — App2：双窗口、磁吸、列表折叠、状态与模拟进度

运行方式：直接在浏览器打开 HTML（React + Babel 由 CDN 加载，无构建步骤）。
