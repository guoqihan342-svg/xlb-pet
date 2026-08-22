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

运行时不直接逐张读取 `pic/` 或全部人物 PNG。正式发布嵌入 Brotli 压缩的 Pbgra32 分页和清单，并按当前动作按需加载；`Assets/luban-pillow-layer.png` 是明确保留的轻量 WPF Resource。旧许愿星、蝴蝶及其 overlay 资源均已退役，不得重新嵌入。

## 2. 原图与动作映射

| 原图或参考 | 动画状态 | 正式资源入口 |
| --- | --- | --- |
| `pic/小鲁班1.jpg` | 原始打哈欠参考（仅保留） | `v1.0.62` 起不再生成、打包或播放 `Assets/luban-yawn-*` |
| `pic/小鲁班2.png` | 趴枕头打呼噜待机 | `Assets/luban-idle.png` |
| `pic/小鲁班3.png` | 委屈哭泣 | `Assets/luban-cry-frame-01.png` 起 |
| `pic/小鲁班4.png` | 原始参考图 | 跑步已移除，不进入运行时图集 |
| `pic/小鲁班5.png` | 欢呼卖萌 | `Assets/luban-cute-frame-01.png` 起 |
| `pic/小鲁班6.png` | 眨眼点赞 | `Assets/luban-like-frame-01.png` 起 |
| `pic/小鲁班7.png` | 吃饼干 | `Assets/luban-eat-frame-01.png` 起 |
| `pic/小鲁班8.png` | 作者原始挥手素材（仅保留） | `v1.0.60` 起不再生成、打包或播放 `Assets/luban-wave-*` |
| `pic/小鲁班9.png` | 右键 Todo 专用托腮思考 | `Assets/luban-think-frame-01.png` 起；Todo 状态所有权不进入普通点击或随机动作 |
| 经甄选的透明打工锚点与四根手指触键姿势 | 坐在电脑前打工 | `luban-work-enter-*`、`luban-work-loop-*`、`luban-work-serious-loop-*`、`luban-work-serious-exit-*`；`pic/小鲁班9.png` 仅作上游角色参考 |
| 高清边缘姿势 | 左/右/下探头 | `luban-edge-left-smooth-*`、`luban-edge-bottom-smooth-*`；右侧镜像左侧 |
| 大头小鲁班与熊猫参考 | 熊猫坐骑巡游 | `luban-roam-boarding-*`、`luban-roam-flight-*`；`roam-wave` 可选 |
| `tools/generated_sources/roam-rocket-luban-cloud-key-v2-alpha.png` | 加长萌火箭巡游 | `luban-roam-rocket-boarding-*`、`luban-roam-rocket-flight-*`，必须成对出现且各 64 帧 |

`pic/` 是用户原始素材边界。任何生成、安装、QA 或清理脚本都不得覆盖或删除其中的文件。本文中已退役的打哈欠、普通挥手动作与熊猫巡游可选的 `roam-wave` 是互相独立的资源；退役动作不得因其他可选构建而重新进入运行时。

## 3. 尺寸和画质契约

- 图集显示帧为 `399×509` Pbgra32，对应 `190×242 DIP` 逻辑基准。
- 许愿星、蝴蝶和失败的全人物星星方案均不得新增或复用运行时动作入口；对应 PNG、overlay、ActionName、对白和图集页都应保持为零。
- 桌宠在 150% DPI、140% 用户缩放下仍使用高密度源像素，不能退回低清放大图。
- 所有姿势共用统一逻辑边界、帽子中心和接触基线，避免换帧时忽大忽小。
- 相邻姿势直接显示清晰单帧；大变化使用专用桥接姿势，不使用整图交叉淡化制造双层轮廓或光纹。
- 人物、双手、喇叭、声效线或熊猫坐骑等全尺寸内容必须作为完整预乘 Alpha 轮廓处理，不能用模糊整图交叉淡化制造双影。
- 边缘探头先保留完整头、肩和双手，再按 Windows 边界裁切；不要把已经截断的手交给补帧器。

## 4. 当前图集快照与动态契约

截至 `2026-08-10`，`v1.0.66` 在 `v1.0.65` 的侧边紧凑抓边基础上补全下手后方的短弯前臂；左侧 48 帧仍是唯一源序列，右侧运行时精确水平镜像，Bottom 资源保持不变。该调整不改变人物动作集合或图集逻辑帧总数，最终数量仍须由同次重建清单复核。

截至 `2026-08-10`，`v1.0.65` 删除 `star-wish` ActionName、对白、overlay 和 `Assets/luban-wish-star.png`。普通点击与空闲动作人物资源只包含 `cry / cute / like / eat`，Todo 继续独占完整 `think` smooth。许愿星本来不占人物分页，因此 Brotli v4 人物图集仍为 41 页、1240 个源帧和 1240 个分页帧；最终值仍须由同次重建清单复核。

截至 `2026-08-09`，`v1.0.63` 曾以轻量 `star-wish` 替换普通 `butterfly`；该段仅保留历史。人物当时复用现有 `cute` 的 56 张 smooth 帧并增加一张独立星星 Resource，失败的 144 帧全人物 `star-cuddle` 不打包。

截至 `2026-08-09`，`v1.0.62` 删除 `yawn` 的 84 张 smooth 与 48 张 loop、未播放的 48 张 `loop-cute`，以及不可达的 `cute-smooth-057..090`。蝴蝶普通动作复用 Todo 已验证的 56 张 `think` smooth 人物帧，独立 `96×96` 蝴蝶不进入图集；Todo 仍拥有原有完整入场、最终姿势和反向退场。目标 Brotli 图集为 41 页、1240 个逻辑帧，正式发布仍须以同次重建的清单、程序集嵌入资源和源集指纹动态复核。

截至 `2026-08-09`，`v1.0.61` 的运行时打工契约仍为 `48 / 96 / 96 / 24`，共 264 个逻辑帧；48 帧 `work-tap` 和普通挥手的 5 页、146 帧继续保持退役。普通点击与空闲随机动作只包含 `yawn / cry / cute / like / eat`；Todo 继续使用完整 `think` smooth 入场和稳定托腮姿势，但仅供普通思考动作使用的 48 帧 `loop-think` 及其 1 个分页不再打包。目标 Brotli 图集由 `v1.0.60` 的 48 页、1502 帧收敛为 47 页、1454 帧；左右探头资源仍使用 `v1.0.57` 恢复结果。页数与帧数必须从最终清单动态校验，不能只由动作增减手算。

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

这些数字只用于保留历史，不是固定公共接口。boarding、flight、四组独立普通人物动作、Todo 专用 `think` smooth 和可选 `roam-wave` 的实际帧数、逻辑帧数与分页数必须从磁盘资源和 `Assets/luban-sprite-pages.json` 动态读取；`star-wish`、`action-star-wish`、`star-cuddle` 和额外 loop 分页必须保持为零。构建或测试不得依赖旧 README 中的固定总帧数。

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

### 5.2 退役物件动画边界

`star-wish`、`butterfly` 与失败的全人物 `star-cuddle` 均已退役。正式提交和发布包不得包含对应 ActionName、中文对白、WPF overlay、`luban-wish-star.png`、`luban-butterfly.png`、`luban-star-cuddle-*` 人物帧、生成源或图集页。`cute` 的前 56 张高清 smooth 只服务保留的欢呼卖萌动作，不再被物件动画别名复用。

### 5.3 生成密集帧

委屈、欢呼卖萌、点赞、吃饼干四种独立人物动作与 Todo 专用 `think` smooth 补帧需要外部 RIFE 或等价的连续补帧工具。`star-wish`、`butterfly`、`star-cuddle`、`yawn`、普通 `think` 循环、未播放的 `cute` 循环和普通挥手均已退役，不得被 `--loops`、`--actions` 或单动作入口重新带回运行时；`cute-smooth` 只保留运行时可达的前 56 帧。提醒序列使用确定性刚体生成。

```powershell
$env:XLB_RIFE_ROOT = 'C:\path\to\rife-ncnn-vulkan-20221029-windows'
python .\tools\generate_dense_motion_assets.py `
  --wake --actions --loops --edge-peek --reminder
```

### 5.4 构建熊猫坐骑素材

```powershell
python .\tools\build_roam_flight_assets.py
```

boarding 与 flight 从 `001` 连续编号并保持姿势唯一；`roam-wave` 是可选补充，不能用固定秒数强行切断 flight 主循环。人物和帽子保持正立，运行资源不得重新混入 `run`、`crawl` 或 `wriggle` 旧动作。

### 5.5 构建加长萌火箭素材

```powershell
python .\tools\build_roam_rocket_assets.py
```

生成器只读取已甄选的透明关键图，确定性输出 `64` 张 boarding 和 `64` 张 flight。火箭本体上限为约 `300 × 335 px` 并做 `1.18×` 横向拉长；三朵云均为 `64 × 64 px`，相邻轨道固定保留 `1 px` 透明间隙，按 `4` 帧爆发周期以 `0 / 1 / 1 / 2 px` 水平位移后回卷。第一朵相对火箭最大包围必须至少露出 `12 px`，最右侧必须保留一整列透明像素；最终合成后还要逐朵核对未被火箭遮挡的可见面积。全部 flight 四边必须保留透明像素，`idle == boarding-001`、`boarding-064 == flight-001` 必须逐字节相等；不得用整帧交叉淡化制造双角色鬼影。

图集把 `roam-rocket-boarding` 与 `roam-rocket-flight` 视为成对能力：两者同时存在才可构建，缺少任一组立即失败。QA 同时检查三云可见面积、互不重叠、首尾爆发循环与 boarding / flight 接缝。

### 5.6 生成侧边紧凑抓边手臂

`v1.0.66` 要求“紧凑”不能退化为只有手掌和极短袖口：下手后的紫色前臂必须保持连续、短小、上弯的完整轮廓，从腕后自然延伸并收进脸下，同时避免 `v1.0.64` 的横向长管、扫描线条纹和近水平平切底边。右侧只允许运行时镜像左侧，Bottom 的文件字节和解码像素哈希必须保持不变。

源图 QA 之外还必须按最终运行尺寸验收：以 `190×242 DIP` 人物显示区域，在 `0.75` 与 `1.40` 用户缩放下分别观察左、右浅探与深探。四种组合中都应看见下手后的连续前臂，且深探不会在窗口下半部显得被裁掉；不能只凭 `450×550` 源 PNG、放大 ROI 或接触像素数量判定通过。

`v1.0.65` 废弃 `v1.0.57` 的逐扫描线补色和 7 像素局部拉伸。安装四张左侧关键姿势、生成 48 张补间帧时，管线会在源图 `450×550` 的下方袖口 ROI 内重建一条短小、上扬的 C 形前臂；头、脸、帽子、耳机和两只手的原始像素保持不变。构建图集前可再次运行同一幂等检查：

```powershell
python .\tools\fix_edge_side_arm_reveal.py --smooth-only
```

脚本会清除旧拉伸留下的 x=0 重复横纹、袖口下半环和断连低 Alpha 碎点，再用原有紫色袖子像素形成与手腕连通的圆润轮廓；不得重新绘制人物或加入整图淡化。48 帧必须全部唯一，循环相邻 ROI Alpha IoU 不低于 `0.94`、面积变化不超过 `4%`、腕心和轮廓端点步进不超过 `2` 个源像素、量化后的近水平下缘连续长度不超过 `6` 个源像素。右侧运行时严格水平镜像同一左侧序列，不产生第二套 PNG；Bottom 的文件名、字节和解码像素哈希必须保持不变。报告默认写入忽略的 `.codex_tmp/edge-compact-grip-qa.json`，不得提交；图集继续保留 `DestinationX=-2` 的透明 gutter。

### 5.7 生成打工素材

打工素材由 `tools/build_work_animation.py` 从已甄选的透明锚点确定性生成：

```powershell
python .\tools\build_work_animation.py
```

当前构建只读取 `luban-work-home-row-v5-alpha.png`、`luban-work-keyboard-underlay-v5-alpha.png`、四张 `luban-work-*-down-v5-alpha.png` 触键参考、`luban-work-serious-v2-alpha.png` 和 `Assets/luban-idle.png`，并在 QA JSON 中记录输入 SHA-256。键盘底图只填补手部移开后留下的局部孔洞；左右手与袖口分别从中性姿势构建语义蒙版，再由手腕、掌根、MCP/PIP 和指尖控制点分层变形。生成器不再读取敲头手掌，也不再生成、验收或写出历史 `work-tap` 序列；`pic/小鲁班9.png` 只作上游角色参考，不会被覆盖。

当前确定性输出为：

| 序列 | 帧数 | 用途 |
| --- | ---: | --- |
| `work-enter` | 48 | 萌云团遮挡式入场；退出倒放同一序列 |
| `work-loop` | 96 | 65 张独特位图；8 次不等间隔四指落键，1 倍速每圈 1.6 秒 |
| `work-serious-loop` | 96 | 与普通循环共享手部相位和 9 个精确中性接缝；认真表情以 2 倍速完整保持至少 4 秒 |
| `work-serious-exit` | 24 | 在精确中性姿势上把认真眉形和专注嘴形平滑还原为普通表情 |

脚本只在 v5 语义手/袖口蒙版内做预乘 Alpha 关节变形，旧手位置只用键盘底图局部补洞。8 次触键中心采用不等间隔，四根目标手指的最终栅格实测位移必须落在 `5.528–6.049 px`；认真表情补丁之外的脸、双眼、头、躯干、电脑和非目标键盘区域逐像素零漂移。普通与认真循环各有 9 个精确中性接缝、65 张独特位图，最长连续中性停顿不得超过 5 帧。认真眉形每侧始终只能有一个连通形状，专注嘴形也必须在局部补丁内变化；`work-enter` 不允许把待机与电脑场景整图淡化。以下接缝必须像素级相等：

- `work-enter-048 == work-loop` 中性接缝。
- 普通与认真循环在声明的 9 个中性帧内分别等于各自中性位图。
- 双击到达最近普通中性接缝后，运行时倒放抽取 `work-serious-exit` 的 8 个姿势，形成约 `133 ms` 的认真表情过渡；首尾分别等于普通与认真中性位图。
- `work-serious-loop` 中性接缝 `== work-serious-exit-001`。
- `work-serious-exit-024 == work-loop-001`。

质量报告写入 `tools/generated_sources/luban-work-animation-qa.json`，只包含 `48/96/96/24` 四阶段共 264 帧，至少检查连续编号、96 帧循环的 65 张独特位图、9 个中性接缝、最长 5 帧中性停顿、8 次不等间隔四指触键、目标指尖实际位移、v5 手/袖口蒙版边界、透明 RGB、Alpha IoU、静态锁区零漂移、单眉连通性、嘴形变化、双眼逐像素锁定和上述状态接缝。表情联系表同时展示运行时实际抽取的 8 帧认真进入与 8 帧认真退出，并覆盖眉眼和嘴形区域。

### 5.7 源素材 QA

```powershell
python .\tools\qa_dense_motion_assets.py --require-edge-peek --contacts
```

QA 至少覆盖：

- PNG 尺寸和透明通道；退役的 `Assets/luban-wish-star.png`、蝴蝶和全人物星星候选必须不存在，也不得被项目资源或程序集重新嵌入。
- 帧编号连续且关键序列不存在重复帧。
- 普通点击与空闲随机动作的人物清单只包含 `cry / cute / like / eat`；点击随机选择必须排除上次成功动作，空闲洗牌袋必须独立。Todo 专用 56 帧 `think` smooth 必须保留；`star-wish`、`butterfly`、`star-cuddle`、`yawn`、`loop-cute`、`cute-smooth-057..090`、`loop-think` 和普通 `luban-wave-*` 运行时资源都必须保持为零。
- 相邻轮廓、帽子中心、人物缩放和接触基线连续。
- 熊猫、铃铛、竹筒保持同一完整轮廓。
- 边缘探头接触点、单调探出、真实探出深度和支撑手臂遮罩 QA。
- 打工的自然双手运动、认真循环/退出帧数、脸区稳定与像素级接缝。
- 原始关键姿势与最终安装帧逐像素一致。

### 5.8 构建最终图集

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

### 5.9 最终图集 QA

```powershell
python .\tools\qa_sprite_atlas_motion.py --contacts
```

该步骤应解码最终 Pbgra32 分页，核对清单、Brotli payload、像素哈希、帧映射和动作连续性。

## 6. 时间轴不变量

- 人物动作、熊猫巡游和边缘探头使用同一个 `CompositionTarget.Rendering + Stopwatch` 绝对时间轴；59/60/120/144 Hz 在同一绝对时间必须得到同一视觉状态。不得启动第二个 `Rendering` 订阅、`DispatcherTimer` 或逐帧创建位图。呼噜泡泡由 WPF `DoubleAnimation/AnimationClock` 驱动，稳定待机时不为它单独保留托管 `Rendering` 回调。
- 动作播放速度由 `MainWindow.xaml.cs` 中 `AnimationPlaybackSpeed` 代码常量配置，当前默认 `1.25`；不提供持久化速度滑块。
- 打工普通循环的 96 个逻辑帧以 1 倍速播放，每圈 1.6 秒。单击严格保持当前 clip、相位、速度和认真期限；双击立即保持当前相位切到 2 倍速，到最近的 9 个精确中性接缝之一后播放约 133 ms 认真表情过渡，再进入 96 帧 `work-serious-loop` 并从此完整计时至少 4 秒。认真期限到达后仍等待最近精确接缝，再播放 24 帧认真退出，不能在任意手指相位硬切。
- 窗口位置按显示器刷新率更新，姿势帧率和移动刷新率互相独立。
- 逻辑坐标保持高精度，只在写入 `Left/Top` 时对齐物理像素。
- UI 延迟超过 250ms 时不补移动距离，也不快速补播积压姿势；直接定位到当前绝对时间对应状态。
- 手动边缘探头每轮严格约 10 秒，开心姿势停留约 650ms；长静止段应暂停逐帧托管回调。

## 7. 内存不变量

- 运行时只同步解码并永久固定首个待机页；起身续页和其他页面全部按动作需要预取。
- 普通动作 resident LRU 软预算为 `52 MiB`。
- 火箭或熊猫巡游预载 / 播放期间允许 `92 MiB` 预算；需求必须分别从当前清单与受限容量桶复用规则动态验证，只保护当轮车辆，不把两套热集相加。
- 动作结束保留 `20 秒`热缓存，稳定空闲后 resident 页和空闲缓冲池共同收敛到 `12 MiB`；只保留完整待机页，不固定第一张起身续页。
- 边缘探头进入静止保持段后也允许执行上述收缩；运动帧期间继续禁止回收，完整边缘序列页始终受保护。
- 单页解码不得超过清单声明的 24 MiB；像素缓冲池总上限为 `92 MiB`，按 `1 MiB`容量桶复用，并仅允许复用大 `1 MiB` 的最近桶。
- 后台 Rent 前先淘汰之后必然被预算驱逐的非保护 LRU 页；当前、pending、desired、唯一固定的待机页和巡游页必须保留。
- 自然 Gen2 只更新观察代际，不得假定 LOH 已压缩并清空淘汰债务；淘汰债务达到 `8 MiB` 后，才可在稳定空闲门禁内请求显式 LOH 压缩，完成后再扣除债务。
- 取消、过期预取、LRU 淘汰和退出必须归还缓冲；迟到的后台结果不得在退出后重新常驻。
- 渲染回调不得读盘、解压、写文件或为每帧创建新位图。

## 8. 变更素材前的检查

```powershell
git status --short
git ls-files Assets pic tools/generated_sources
```

不要覆盖用户原图，不要把 `_scratch/`、`_qa/`、`.codex_tmp/`、派生预览 PNG 或外部 RIFE 工具混入正式提交。最终发布前必须同时通过源 PNG QA、图集构建、最终图集 QA 和相关 UI 契约。
