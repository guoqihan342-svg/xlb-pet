# 动画与图集管线

本文记录运行时动画架构、素材边界、生成顺序和必须保持的质量不变量。普通代码修改不需要重建素材；只有更换动作、补帧或图集格式时才使用本管线。

## 1. 运行时概览

```text
pic/ 用户原图（只读参考）
        │
        ▼
tools/generated_sources/ 已甄选创作源
        │ install / dense generation / roam build
        ▼
Assets/ 透明 PNG 运行素材
        │ source QA
        ▼
build_sprite_atlas.py
        │
        ├─ Assets/luban-sprite-pages.json
        └─ Assets/sprite-pages/*.pbgra.br
                │ atlas QA
                ▼
       WPF 运行时按页解码与显示
```

运行时不直接逐张读取 `pic/` 或全部 PNG。正式发布嵌入 Brotli 压缩的 Pbgra32 分页和清单，并按当前动作按需加载。

## 2. 原图与动作映射

| 原图或参考 | 动画状态 | 正式资源入口 |
| --- | --- | --- |
| `pic/小鲁班1.jpg` | 犯困/打哈欠 | `Assets/luban-yawn-frame-01.png` 起 |
| `pic/小鲁班2.png` | 趴枕头打呼噜待机 | `Assets/luban-idle.png` |
| `pic/小鲁班3.png` | 委屈哭泣 | `Assets/luban-cry-frame-01.png` 起 |
| `pic/小鲁班4.png` | 原始参考图 | 跑步已移除，不进入运行时图集 |
| `pic/小鲁班5.png` | 欢呼卖萌 | `Assets/luban-cute-frame-01.png` 起 |
| `pic/小鲁班6.png` | 眨眼点赞 | `Assets/luban-like-frame-01.png` 起 |
| `pic/小鲁班7.png` | 吃饼干 | `Assets/luban-eat-frame-01.png` 起 |
| `pic/小鲁班8.png` | 挥手 | `Assets/luban-wave-frame-01.png` 起 |
| `pic/小鲁班9.png` | 托腮思考 | `Assets/luban-think-frame-01.png` 起 |
| 经甄选的透明打工锚点与四根手指触键姿势 | 坐在电脑前打工 | `luban-work-enter-*`、`luban-work-loop-*`、`luban-work-tap-*`、`luban-work-serious-loop-*`、`luban-work-serious-exit-*`；`pic/小鲁班9.png` 仅作上游角色参考 |
| 高清边缘姿势 | 左/右/下探头 | `luban-edge-left-smooth-*`、`luban-edge-bottom-smooth-*`；右侧镜像左侧 |
| 大头小鲁班与熊猫参考 | 熊猫坐骑巡游 | `luban-roam-boarding-*`、`luban-roam-flight-*`；`roam-wave` 可选 |

`pic/` 是用户原始素材边界。任何生成、安装、QA 或清理脚本都不得覆盖或删除其中的文件。

## 3. 尺寸和画质契约

- 图集显示帧为 `399×509` Pbgra32，对应 `190×242 DIP` 逻辑基准。
- 桌宠在 150% DPI、140% 用户缩放下仍使用高密度源像素，不能退回低清放大图。
- 所有姿势共用统一逻辑边界、帽子中心和接触基线，避免换帧时忽大忽小。
- 相邻姿势直接显示清晰单帧；大变化使用专用桥接姿势，不使用整图交叉淡化制造双层轮廓或光纹。
- 人物、双手、喇叭、声效线或熊猫坐骑等必须作为完整预乘 Alpha 轮廓处理，不能把独立矢量贴层悬空叠加。
- 边缘探头先保留完整头、肩和双手，再按 Windows 边界裁切；不要把已经截断的手交给补帧器。

## 4. 当前图集快照与动态契约

截至 `2026-08-05`，`v1.0.54` 的打工源帧契约为 `48 / 96 / 48 / 96 / 24`，共 312 个逻辑帧。整个 Brotli 图集的总帧数和分页数必须在最终重建后从清单读取，不能由动作增量手算。

`v1.0.52` 的历史清单快照为：

| 字段 | 当前值 |
| --- | --- |
| `version` | `4` |
| `compression` | `brotli` |
| `displayWidth × displayHeight` | `399 × 509` |
| `sourceFrameCount` | `1600` |
| `pageFrameCount` | `1600` |
| 分页数 | `53` |
| `maxDecodedPageBytes` | `25,165,824`（24 MiB） |

这些数字只用于保留上一版历史，不是固定公共接口。boarding、flight、动作和可选 wave 的实际帧数、逻辑帧数与分页数必须从磁盘资源和 `Assets/luban-sprite-pages.json` 动态读取。构建或测试不得依赖旧 README 中的固定总帧数。

查询当前清单：

```powershell
$manifest = Get-Content .\Assets\luban-sprite-pages.json -Raw | ConvertFrom-Json
[pscustomobject]@{
    Version      = $manifest.version
    Compression  = $manifest.compression
    SourceFrames = $manifest.sourceFrameCount
    PageFrames   = $manifest.pageFrameCount
    Pages        = @($manifest.pages.PSObject.Properties).Count
    Display      = "$($manifest.displayWidth)x$($manifest.displayHeight)"
}
```

## 5. 生成顺序

### 5.1 安装已甄选创作源

```powershell
# 基础动作、静态枕头层和手动边缘姿势
python .\tools\install_generated_motion_assets.py `
  --v6-motion `
  --source-directory .\tools\generated_sources `
  --assets-directory .\Assets

# 重新安装边缘探头创作源
python .\tools\install_generated_motion_assets.py `
  --edge-peek `
  --source-directory .\tools\generated_sources `
  --assets-directory .\Assets

# 安装提醒核心姿势和桥接候选
python .\tools\install_generated_motion_assets.py `
  --reminder `
  --source-directory .\tools\generated_sources `
  --assets-directory .\Assets
```

### 5.2 生成密集帧

普通动作补帧需要外部 RIFE；提醒序列使用确定性刚体生成。

```powershell
$env:XLB_RIFE_ROOT = 'C:\path\to\rife-ncnn-vulkan-20221029-windows'
python .\tools\generate_dense_motion_assets.py `
  --wake --actions --loops --edge-peek --reminder
```

### 5.3 构建熊猫坐骑素材

```powershell
python .\tools\build_roam_flight_assets.py
```

boarding 与 flight 从 `001` 连续编号并保持姿势唯一；`roam-wave` 是可选补充，不能用固定秒数强行切断 flight 主循环。人物和帽子保持正立，运行资源不得重新混入 `run`、`crawl` 或 `wriggle` 旧动作。

### 5.4 修复侧边支撑手臂

重新安装或生成左侧探头帧后，在构建图集前运行：

```powershell
python .\tools\fix_edge_side_arm_reveal.py
```

该脚本只在 48 帧 `luban-edge-left-smooth-*` 的下方支撑手臂遮罩内做最多 7 像素的平滑显露，遮罩外像素必须零改动。右侧运行时镜像复用左侧，不产生第二套源帧。脚本使用输入/输出 SHA-256 防止累积重写：已修复素材再次运行是幂等的，来源不明的素材会失败关闭。QA 报告写入 `tools/generated_sources/edge-side-arm-reveal-qa.json`。

### 5.5 生成打工素材

打工素材由 `tools/build_work_animation.py` 从已甄选的透明锚点确定性生成：

```powershell
python .\tools\build_work_animation.py
```

当前构建直接读取 `luban-work-home-row-v5-alpha.png` 中性锚点、`luban-work-armless-underlay-v6-alpha.png` 无臂参考、`luban-work-left-prelift-v6-alpha.png` 与 `luban-work-viewer-right-prelift-v6c-alpha.png` 两侧预抬参考、`luban-work-tap-hand-v1-alpha.png`、`luban-work-serious-v2-alpha.png` 和 `Assets/luban-idle.png`，并在 QA JSON 中记录输入 SHA-256。图像生成的 v6 参考只用于确认人体结构和画风；正式运行帧固定沿用获批中性图的脸、电脑和比例，从原图提取完整双臂，并用确定性的局部补洞恢复手臂移开后露出的窄小区域。`pic/小鲁班9.png` 只作上游角色参考，不会被覆盖。

v6 参考图通过三次定向图像编辑得到：一张要求从肩缝开始移除双臂并补全衣服与键盘，一张要求观察者左侧手臂以肘、腕、手指连贯预抬，另一张借助局部标记要求观察者右侧张开手掌贴近键盘且不能握拳。生成结果先保留纯 `#00FF00` 背景版本，再用标准 chroma-key 脚本转换为透明 PNG；只有通过人物身份、五官、电脑、画布和肢体连接检查的三组候选进入正式输入。

当前确定性输出为：

| 序列 | 帧数 | 用途 |
| --- | ---: | --- |
| `work-enter` | 48 | 萌云团遮挡式入场；退出倒放同一序列 |
| `work-loop` | 96 | 81 张独特位图；6 次强触键加 2 次轻微换位，1 倍速每圈 1.6 秒 |
| `work-tap` | 48 | 单击敲头反应；首尾都对齐普通循环的精确中性接缝，播放后回到 1 倍速 |
| `work-serious-loop` | 96 | 81 张独特位图；4 次强触键加 4 次轻微换位，使用 8 个精确中性接缝并以 2 倍速完整保持至少 4 秒 |
| `work-serious-exit` | 24 | 在精确中性姿势上把认真眉平滑还原为普通表情 |

脚本把每侧肩、上臂、肘、袖口、手腕、掌根和四指作为一个连通的 v6 关节层进行预乘 Alpha 变形，并对敲头叠层单独合成；旧手臂位置只在必要的窄小区域确定性补洞。普通循环使用 6 次强触键和 2 次轻微换位，目标指尖最终栅格实测位移必须落在 `5.5–9.5 px`，且肩 `<` 肘 `<` 腕 `<` 指尖；专用眉形补丁之外的脸、电脑和双臂允许区域外逐像素零漂移。普通与认真循环各有 8 个精确中性接缝、至少 70 张独特位图，最长连续相同画面不得超过 3 帧。认真眉形每侧始终只能有一个连通形状，`work-enter` 不允许把待机与电脑场景整图淡化。以下接缝必须像素级相等：

- `work-enter-048 == work-loop` 中性接缝。
- `work-tap-001 == work-tap-048 == work-loop` 中性接缝。
- 普通与认真循环在声明的 8 个中性帧内分别等于各自中性位图。
- 双击到达最近普通中性接缝后，运行时倒放抽取 `work-serious-exit` 的 8 个姿势，形成约 `133 ms` 的认真眉过渡；首尾分别等于普通与认真中性位图。
- `work-serious-loop` 中性接缝 `== work-serious-exit-001`。
- `work-serious-exit-024 == work-loop-001`。

质量报告写入 `tools/generated_sources/luban-work-animation-qa.json`，至少检查连续编号、96 帧循环至少 70 张独特位图、8 个中性接缝、最长 3 帧连续相同画面、普通与认真强触键事件、肩到指尖的逐级位移、30/59/60/120/144Hz 采样覆盖、v6 完整手臂蒙版边界、透明 RGB、Alpha IoU、静态锁区零漂移、单眉连通性和上述状态接缝。

### 5.6 源素材 QA

```powershell
python .\tools\qa_dense_motion_assets.py --require-edge-peek --contacts
```

QA 至少覆盖：

- PNG 尺寸和透明通道。
- 帧编号连续且关键序列不存在重复帧。
- 相邻轮廓、帽子中心、人物缩放和接触基线连续。
- 熊猫、铃铛、竹筒保持同一完整轮廓。
- 边缘探头接触点、单调探出、真实探出深度和支撑手臂遮罩 QA。
- 打工的自然双手运动、认真循环/退出帧数、脸区稳定与像素级接缝。
- 原始关键姿势与最终安装帧逐像素一致。

### 5.7 构建最终图集

```powershell
python .\tools\build_sprite_atlas.py
```

默认不输出派生分页预览 PNG。只有人工验图时临时启用：

```powershell
$env:XLB_ATLAS_WRITE_PREVIEWS = '1'
python .\tools\build_sprite_atlas.py
Remove-Item .\Assets\sprite-pages\*.png
Remove-Item Env:XLB_ATLAS_WRITE_PREVIEWS
```

### 5.8 最终图集 QA

```powershell
python .\tools\qa_sprite_atlas_motion.py --contacts
```

该步骤应解码最终 Pbgra32 分页，核对清单、Brotli payload、像素哈希、帧映射和动作连续性。

## 6. 时间轴不变量

- 人物动作、熊猫巡游和边缘探头使用 `CompositionTarget.Rendering + Stopwatch` 绝对时间轴。呼噜泡泡改由 WPF `DoubleAnimation/AnimationClock` 驱动，稳定待机时不为它单独保留托管 `Rendering` 回调。
- 动作播放速度由 `MainWindow.xaml.cs` 中 `AnimationPlaybackSpeed` 代码常量配置，当前默认 `1.25`；不提供持久化速度滑块。
- 打工普通循环的 96 个逻辑帧以 1 倍速播放，每圈 1.6 秒。单击在最近精确中性接缝播放 `work-tap`，结束后仍回普通 1 倍速；双击立即保持当前相位切到 2 倍速，到最近的 8 个精确中性接缝之一后播放约 133 ms 认真眉过渡，再进入 96 帧 `work-serious-loop` 并从此完整计时至少 4 秒。认真期限到达后仍等待最近精确接缝，再播放 24 帧认真退出，不能在任意手指相位硬切。
- 窗口位置按显示器刷新率更新，姿势帧率和移动刷新率互相独立。
- 逻辑坐标保持高精度，只在写入 `Left/Top` 时对齐物理像素。
- UI 延迟超过 250ms 时不补移动距离，也不快速补播积压姿势；直接定位到当前绝对时间对应状态。
- 手动边缘探头每轮严格约 10 秒，开心姿势停留约 650ms；长静止段应暂停逐帧托管回调。

## 7. 内存不变量

- 运行时只同步解码首个待机页，并按动作需要预取相邻页。
- 普通动作 resident LRU 软预算为 `64 MiB`。
- 熊猫巡游预载或播放期间允许 `104 MiB` 预算；当前清单全部 boarding、flight、固定待机页和相邻桶最坏余量为 `101 MiB`。
- 动作结束保留 `20 秒`热缓存，稳定空闲后 resident 页和空闲缓冲池共同收敛到 `24 MiB`；两页待机/起身热集保持完整。
- 边缘探头进入静止保持段后也允许执行上述收缩；运动帧期间继续禁止回收，完整边缘序列页始终受保护。
- 单页解码不得超过清单声明的 24 MiB；像素缓冲池总上限为 `104 MiB`，按 `1 MiB`容量桶复用，并仅允许复用大 `1 MiB` 的最近桶。
- 后台 Rent 前先淘汰之后必然被预算驱逐的非保护 LRU 页；当前、pending、desired、固定待机和巡游页必须保留。
- 自然 Gen2 只更新观察代际，不得假定 LOH 已压缩并清空淘汰债务；债务由空闲显式压缩完成后扣除。
- 取消、过期预取、LRU 淘汰和退出必须归还缓冲；迟到的后台结果不得在退出后重新常驻。
- 渲染回调不得读盘、解压、写文件或为每帧创建新位图。

## 8. 变更素材前的检查

```powershell
git status --short
git ls-files Assets pic tools/generated_sources
```

不要覆盖用户原图，不要把 `_scratch/`、`_qa/`、`.codex_tmp/`、派生预览 PNG 或外部 RIFE 工具混入正式提交。最终发布前必须同时通过源 PNG QA、图集构建、最终图集 QA 和相关 UI 契约。
