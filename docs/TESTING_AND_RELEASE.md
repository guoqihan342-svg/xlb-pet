# v1.0.77 测试与发布

本文给出可重复的本地测试入口、发布命令和人工验收边界。仓库当前没有 CI；任何“已通过”都必须来自本机命令或实机检查，不能写成云端自动验证。

## 1. 测试前准备

```powershell
git status --short
dotnet --info
dotnet restore .\DesktopPet.csproj
```

- 先确认工作树中哪些修改属于当前任务，保留用户已有的素材和代码变化。
- 自动化测试使用临时数据目录，但实机冒烟会读取正式 `%LocalAppData%\LubanDesktopPet`；测试前建议先正常退出并备份。
- 当前生产版不写文件日志，不能把“没有 log 文件”误判成启动失败。

## 2. 快速验证

### Release 构建

```powershell
dotnet build .\DesktopPet.csproj -c Release
dotnet build .\tests\UiStateChecks\UiStateChecks.csproj -c Release
```

### 存储契约

```powershell
dotnet run --project .\tests\TodoStoreChecks\TodoStoreChecks.csproj -c Release
```

覆盖待办、设置、定时任务、循环规则、免打扰、损坏 JSON 和占用/无权限保护。

### 完整 WPF 契约

```powershell
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj -c Release
```

完整套件包含动画、缓存、内存、绝对时间轴、多屏、DPI、边缘吸附、巡游、待办、输入法、开机自启、托盘和提醒堆叠。发布前不能忽略失败项。
其中桌宠尺寸手势会在新的 `--pet-size-only` 子进程和全新 `MainWindow` 中执行，防止前序提醒测试的 WPF 布局状态串入；子进程退出码仍属于完整套件结果，失败不会被忽略。

## 3. 定向 UI 入口

所有入口都使用同一项目：

```powershell
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj `
  -c Release --no-build -- --todo-layout-only
```

将示例末尾的 `--todo-layout-only` 替换为下表中的其他参数，即可运行对应定向检查。

| 参数 | 主要范围 |
| --- | --- |
| `--todo-layout-only` | 待办布局、主题、输入法、修改、拖拽、`360 × 280 DIP` 长文预览、负坐标/窄屏工作区夹紧、右键菜单 transient 生命周期、悬停像素和滚动 |
| `--todo-cut-only` | 三个窗口的 Copy / Cut 路由、`Ctrl+X` / `Shift+Delete` / 右键、剪贴板占用 fail-closed、全局 generation、外部 sequence、绝对 `300 ms` 截止、隐藏取消及 ASCII 数字粘贴 |
| `--todo-only` | Owned Window、箭头、多屏、隐藏面板半尺寸/离屏恢复、待办完成自动移底、取消完成保留位置、拖拽排序、定时选项卡、免打扰展开后的列表真实收缩与末项滚动，以及尾部提示在固定面板和最小宽度修改窗中的完整排版 |
| `--todo-arrow-only` | 气泡箭头在多屏/DPI/换边时指向人物 |
| `--scheduled-editor-only` | 定时任务新增与修改组件 |
| `--reminder-only` | 提醒堆叠、分页、关闭语义和免打扰运行时 |
| `--startup-only` | 当前用户开机自启、原生托盘、Dispatcher 合并打开、前台所有权、外点关闭、通知区焦点归还、显式锚点和物理坐标/DIP 往返契约 |
| `--alt-tab-only` | 六个正式 WPF 窗口的真实 HWND 扩展样式；必须含 `WS_EX_TOOLWINDOW`、不含 `WS_EX_APPWINDOW`/`WS_EX_NOACTIVATE` 且不进入 Alt+Tab 候选集合，同时保持输入与焦点能力 |
| `--edge-dock-only` | `12 DIP` 磁吸、快速越界、顶部可拖达但不吸附、左/右/下 `rest-first` 吸附、48 帧侧边支撑手臂像素接触、完整短弯前臂，以及待办/定时面板自动关闭与迟到回调防重开 |
| `--pet-drag-preview` | 显示可由 Computer Use 识别的真实 WPF 拖动窗口；标题实时给出人物可见顶边、工作区顶边和物理像素误差 |
| `--roam-source-only` | 绕屏状态机、路线、朝向、原地退场、延迟打开待办、负坐标副屏和资源契约 |
| `--roam-interaction-only` | 真实 WPF 状态下的巡游交互，以及混合 DPI 拖拽的 32 位光标、不可变抓点、generation 失效、Loaded→ContextIdle 稳定校正、顶部 clamp 工作区切换、松手最多 3 次重试和失败关闭契约 |
| `--deadline-only` | 1 分钟原地动作、10 分钟巡游和 20 秒忙碌重试截止 |
| `--pet-size-only` | 尺寸滑块手势、连续缩放和待办布局 |
| `--reaction-random-only` | 许愿星资源彻底退役、四个保留动作、用户点击随机且不连续重复、失败不提交历史，以及自动洗牌袋独立 |
| `--clip-clock-only` | 单缓冲预乘 Alpha、冷页时钟、四种普通动作的绝对时间轴、精确完整水平镜像 6 种尺寸及真实窗口矩阵的逐字节旧路径等价，以及非目标矩阵严格回退 |
| `--work-mode-only` | `48/96/96/24` 序列、普通 1.6 秒、65 张独特循环位图、9 个精确中性接缝、normal / serious 各 23 处相邻同描述符候选的强谓词像素复用、逻辑发布与 deferred clock 不变、pre-Stop blend 状态防误复用、单击严格无操作、双击认真表情、太阳/月亮 420 ms 绝对时钟双向切换、待机拖动时太阳跟随但不可命中、边缘仍隐藏，以及打工拖动命中左/右/下时跳过普通退出与 idle 的热页/冷页原子交接 |
| `--resident-cache-only` | 仅待机页常驻、普通/active reminder/normal 工作/serious 工作/巡游/稳定空闲 `52/36/57/73/92/12 MiB` 预算、四种完整 reaction clip `99/83/109/97 MiB` 短时预算与提醒重叠 `+12 MiB`、`8 MiB` LOH 门槛；提醒完整 enter/hold/reverse-exit 热集、normal/serious/reaction 热集及全部既有淘汰、预取、迟到结果和退出清理契约 |
| `--atlas-hash-only` | 图集页上限、Brotli payload 和像素哈希失败关闭 |
| `--memory-profile` | 本地内存剖面输出；不是普通 pass/fail 快速测试 |

### 人工预览入口

以下入口会打开可交互测试窗口，应由测试者手动关闭：

| 参数 | 预览内容 |
| --- | --- |
| `--todo-preview` | 多行待办、长文本、完成项、悬停、滚动条和真实勾选后自动移底 |
| `--picker-preview` | 日期与时分秒选择器 |
| `--delete-style-preview` | 蓝色待办删除和橘色定时删除确认窗 |
| `--pet-size-preview` | 桌宠大小滑块与人物连续缩放 |
| `--roam-preview` | 按 `Space` 准备一轮真实登乘中段姿势；用于观察 Win32 真实光标判定、左键完整逆播退场、拖动接管和右键退场后打开待办，预览不会自动重启 |
| `--reminder-close-preview` | 提醒关闭和分页交互 |
| `--work-preview` | 左上角萌太阳/月亮 420 ms 双向切换、v5 语义手/袖口关节运动、8 次不等间隔四指落键、单击无额外动作、双击认真表情，以及打工拖动命中左/右/下后直接交接真实探头，全程不播普通退出、枕头或待机中间帧 |

预览窗口只用于人工观察，不替代自动断言。

## 4. 动画素材 QA

仅在修改动画或图集时运行：

```powershell
# 修改侧边关键姿势时，恢复关键帧并用 RIFE 重建 48 帧，随后执行完整短弯支撑前臂后处理
python .\tools\install_generated_motion_assets.py --edge-peek
python .\tools\generate_dense_motion_assets.py --edge-peek
python .\tools\fix_edge_side_arm_reveal.py

# 生成打工帧并校验自然双手运动、认真循环和接缝
python .\tools\build_work_animation.py

# 源 PNG 连续性、Alpha、接触点和轮廓
python .\tools\qa_dense_motion_assets.py --require-edge-peek --contacts

# 确定性重建 Brotli v4 分页
python .\tools\build_sprite_atlas.py

# 解码最终分页并核对清单、像素和连续性
python .\tools\qa_sprite_atlas_motion.py --contacts
```

许愿星、旧蝴蝶和失败的全人物 `star-cuddle` 均已退役。应确认 `Assets/luban-wish-star.png`、`Assets/luban-butterfly.png`、对应 WPF overlay、项目 Resource、ActionName、中文对白、人物图集页和运行时代码全部不存在。

详细生成顺序和不变量见 [动画与图集管线](ANIMATION_PIPELINE.md)。`v1.0.65` 必须断言普通点击与自动袋的人物动作严格只含 `cry / cute / like / eat`；用户点击使用独立随机源并排除上次成功动作，启动失败不提交历史；空闲活动继续以独立袋洗牌四动作和一次待机。Todo 的完整 56 帧 `think` smooth 入场和稳定托腮姿势必须继续存在。最终图集、程序集和发布 EXE 不得包含 star-wish、butterfly ActionName、旧中文对白、失败的 `star-cuddle` 人物帧或图集页，也不得包含 `yawn`、`loop-cute`、`cute-smooth-057..090`、`loop-think` 页面、48 帧普通思考循环或普通 `luban-wave-*` 运行时资源；`pic/小鲁班1.jpg` 与 `pic/小鲁班8.png` 必须继续保留。最终清单必须保持 41 页、1240 个源帧和 1240 个分页帧。

`v1.0.66` 的侧边验收不能只看源 PNG、Alpha 接触数或放大 ROI：必须用最终图集和真实 WPF 显示路径，在 `190×242 DIP` 人物显示区域下，把用户缩放分别设为 `0.75` 与 `1.40`，逐一观察左、右浅探和深探。下手后必须保留完整、连续、短小且上弯的前臂并自然收进脸下；不得只剩手和袖口，不得出现 `v1.0.64` 的横向紫色长管、条纹或平切下缘。右侧必须是左侧精确水平镜像，Bottom 文件字节与解码像素哈希必须保持不变。

## 5. 发布前清单校验

```powershell
$manifest = Get-Content .\Assets\luban-sprite-pages.json -Raw | ConvertFrom-Json
if ($manifest.version -ne 4 -or $manifest.compression -ne 'brotli') {
    throw '最终图集不是 Brotli v4，禁止发布'
}

[pscustomobject]@{
    Version      = $manifest.version
    Compression  = $manifest.compression
    SourceFrames = $manifest.sourceFrameCount
    PageFrames   = $manifest.pageFrameCount
    Pages        = @($manifest.pages.PSObject.Properties).Count
    Display      = "$($manifest.displayWidth)x$($manifest.displayHeight)"
}
```

不要从 README 或旧测试复制其他版本的固定帧数。清单、实际分页、嵌入资源和源集指纹必须来自同一次构建；`v1.0.65` 删除的许愿星原本不占人物分页，因此本次正式清单仍应为 41 页、1240 个源帧和 1240 个分页帧。

## 6. 发布单文件 EXE

先确认目标输出目录中的 EXE 没有正在运行，否则 Windows 会锁定该文件：

```powershell
Get-Process LubanDesktopPet -ErrorAction SilentlyContinue |
  Select-Object Id, Path, Responding
```

框架依赖单文件发布：

```powershell
dotnet publish .\DesktopPet.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -p:DebugType=None `
  -o .\dist
```

如需在不覆盖正在测试的常规输出文件时保留版本化副本：

```powershell
$version = ([xml](Get-Content .\DesktopPet.csproj)).Project.PropertyGroup.Version
dotnet publish .\DesktopPet.csproj -c Release -r win-x64 `
  --self-contained false -p:PublishSingleFile=true -p:DebugType=None `
  -o ".\dist\v$version"
```

发布结果依赖目标机安装 x64 `.NET 8 Desktop Runtime`，不是自包含运行时包。

## 7. 大小、哈希和签名

```powershell
$exe = Get-Item .\dist\LubanDesktopPet.exe
$version = $exe.VersionInfo.FileVersion
$hash = Get-FileHash $exe.FullName -Algorithm SHA256
$signature = Get-AuthenticodeSignature $exe.FullName

[pscustomobject]@{
    Path      = $exe.FullName
    Version   = $version
    Bytes     = $exe.Length
    SHA256    = $hash.Hash
    Signature = $signature.Status
}

if ($exe.Length -ge 100MB) {
    Write-Warning '已达到 GitHub 普通 Git 对象 100 MiB 硬限制，只能上传 Release 附件'
} elseif ($exe.Length -ge 50MB) {
    Write-Warning 'GitHub 会对超过 50 MiB 的普通 Git 对象给出警告'
}
```

规则：

- `dist/**/*.exe` 永远不提交到普通 Git 历史。
- 达到或超过 `100 MiB`（`104,857,600` 字节）时必须使用 GitHub Release 附件，不能尝试绕过限制；规则来源见 [GitHub 大文件说明](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-large-files-on-github)。
- 当前程序没有商业代码签名，`NotSigned` 是预期状态，不等于 SHA-256 失败。
- Release 页面应同时记录版本、字节数、SHA-256、运行时依赖和未签名提示。

## 8. 实机冒烟

发布文件至少检查：

- EXE 能启动，进程响应，通知区域图标出现，第二次启动不会产生第二个实例。
- 四种点击动作都能从待机连续起身并返回；连续有效点击随机选择且不能重复上一个成功动作，自动随机袋只含这四种动作与一次待机并保持状态独立。许愿星、旧蝴蝶对白与资源、失败的 144 帧星星方案、打哈欠、普通“让我认真想一想……”和“嗨～我在这里！”挥手不得再出现；右键打开任务面板仍须完整进入并保持 Todo 专用托腮思考姿势；稳定待机只有一个半透明泡泡。
- 四种 reaction 的完整 wake / action / loop / reverse 路径不得出现冷页 pending；自然完成以及 edge、Todo、Reminder 或分页失败接管后必须立即恢复新状态的普通 `52 MiB` resident 目标，稳定空闲 `20 秒`后再回唯一 idle 页。锁屏期间不得丢弃尚未完成的 reaction 热集；点击接管绕屏预载时不得改变原熊猫巡游到期节奏。
- 打工从 Entering、普通/认真打字、认真进出或 Exiting 拖到左、右、下边缘时，热页和冷页 descriptor 序列都只能是“当前工作帧 → 目标 edge-rest”；不得播放普通 `work-exit`，也不得出现 idle 页、枕头、待机帧、尺寸包络或锚点跳变。目标页失败才回退 idle，顶部和未命中拖放保持工作状态。
- 主桌宠、任务、提醒、确认和两个编辑窗口的真实 HWND 均含 `WS_EX_TOOLWINDOW`、清除 `WS_EX_APPWINDOW`、不设置 `WS_EX_NOACTIVATE` 且 `ShowInTaskbar=false`；人工按 `Alt+Tab` 时不得出现透明框或任何辅助窗口，输入法、焦点和编辑仍须正常。
- 完全待机时可点击人物视觉左上角的萌太阳进入电脑场景；按住待机人物拖动时，太阳必须与人物保持相同的 HWND 屏幕位移和相对位置，但在释放前禁用点击与命中，普通位置释放后恢复可点，左、右、下边缘仍隐藏。进入打工后用 420 ms 绝对时间轴原位交叉切换为萌月亮；退出请求必须在一个刷新周期内开始反向，中途反转从当前混合状态连续折返，总透明度保持 `1.00–1.05`，59/60/120/144 Hz 同一绝对时间结果一致，250 ms UI 阻塞后直接定位且不补播。人物镜像时图标仍保持屏幕左上和正向，不得出现文字胶囊。普通 96 帧循环必须在 1.6 秒内显示 8 次不等间隔四指落键；工作中单击严格保持活动帧、相位、倍速与认真期限，双击立即连续切到 2 倍速并完整保持至少 4 秒认真状态；点击月亮后平滑回到稳定待机。
- serious 工作中右键退出时，中性缝之前必须保持 serious-exit 页 resident，不得让 work-exit 抢占唯一预取槽；serious-exit 首帧必须立即显示且不得出现 pending 冷页停顿，随后应在该 clip 的完整播放窗口内预热 work-exit。normal loop 和 work-enter 中途退出仍必须保留原有早预热行为。
- 左、右、下普通边缘探头时太阳必须隐藏、禁用且不可命中；三边从末尾休息姿势进入。侧边下手必须保持边缘接触，紫袖为短而圆的上扬弧线，不能出现横向长管、平切底边、重手、黑边或光纹；右侧必须是左侧精确镜像，底边素材保持不变，顶部仍不吸附。任务面板打开时拖入任一受支持边缘，待办/定时页面必须立即关闭且不得被迟到回调重新打开。
- 侧边素材必须在最终 `190×242 DIP` 真实 WPF 窗口中复核，而不是只看 `450×550` 源图：分别以 75%（`0.75`）和 140%（`1.40`）桌宠大小检查左、右浅探与深探。每种组合都要看见下手后的完整短弯前臂，深探时下半部分不得显得被裁掉；右侧镜像和 Bottom 不变需同时确认。
- 打工期间不启动自动动作、呼噜或绕屏；拖动过程应继续当前动画与绝对相位。松手命中左、右或下外边缘时，必须锁定目标边缘并完全跳过普通 `work-exit`、枕头和 idle：热页在同一渲染提交中从当前工作描述符原子切到 edge-rest，冷页冻结当前工作描述符直至 edge-rest resident；目标页失败才允许安全回到 idle。顶部及双屏内部接缝不触发边缘交接，右键和定时提醒接管时不得留下迟到请求。
- 右键打开和收起面板、快速拖动大小滑块时不闪帧、不抖动、不改变人物比例。隐藏任务面板被模拟成半尺寸并移到屏外后，系统恢复仍须保持隐藏；再次右键打开必须按人物所在屏幕的当前 DPI 原子恢复 `292×414 DIP` 完整尺寸，并完全落在该屏工作区内。
- 待办新增、`Ctrl+C / Ctrl+X / Ctrl+V`、F2 修改、拖拽排序和长文本全文窗正常；勾选中间项后该项移至末尾、其他项顺序不变，取消完成不自动上移，重启后顺序保持；普通/完成项悬停都应显示浅蓝填充与完整蓝色圆角框，相邻行不得出现上下横线。
- 定时任务日期/秒级时间、循环、免打扰、修改、每页 5 条提醒和确认语义正常；展开循环的免打扰行后，任务列表应按新增行的真实高度收缩，滚动范围保持，最后一项可完整滚入可见区域；“可跨夜”在固定任务面板和最小宽度修改窗中均完整显示，无裁剪或省略。
- 左、右、下边缘探头正常；在 75%、100%、125%、140% 大小下，人物可见像素可在一个物理像素内拖到工作区顶沿，但顶部中央不吸附、不探头。
- 默认熊猫巡游可以被点击、拖动、右键和提醒抢占；静止左键以 Win32 `GetCursorPos` 的真实屏幕光标及按下时 DPI 判定，原生采样短暂失败时保持末次可信点，不受退场时窗口局部坐标漂移影响，完整逆播回到待机且不追加卖萌动作；真实物理位移超过阈值时拖动仍立即接管。全部绕屏页就绪后必须只丢弃 free decode arrays，resident 正播和逆播帧保持不动；受控 active roam resident 应从 baseline `82.00 MiB` 降至 candidate `79.53 MiB`，另测 idle resident 为 `11.00 → 10.86 MiB`。普通动作 `20 秒`缓存宽限、`52 / 92 / 12 / 8 MiB` 预算、图集、像素、帧率、时序和全部素材帧不变。右键只打开一次默认待办页，退出时无回跳、翻转或闪帧，竖边方向旋转正确。
- 负坐标副屏、100%/125%/150% DPI、任务栏避让和显示器热插拔正常。
- 开机自启启用/关闭与旧路径修复正常，最终恢复测试前的真实注册表状态。
- 通知区域右键菜单必须出现在光标附近，多屏和不同 DPI 下不得跑到屏幕左上角；“退出小鲁班”文字完整左对齐，点击桌面或其他应用必须自动关闭，连续右键不得闪出重复/错乱 Popup；正常退出保存数据并移除图标，运行前后都不生成 `log/` 文件夹。

## 9. Git 和 Release 交付

发布前：

```powershell
git diff --check
git status --short --ignored
```

暂存范围不得包含：

- `.codex_tmp/`、`tmp/`、`bin/`、`obj/`。
- `dist/` 下任意层级 EXE。
- 运行时安装包。
- 未甄选的绿幕、候选帧或无关 QA 图片。
- 被覆盖或删除的 `pic/` 用户原图。

建议交付顺序：

1. 同步 `DesktopPet.csproj` 版本和 `CHANGELOG.md`。
2. 运行与改动范围匹配的定向测试、完整存储测试和发布前全量 UI 测试。
3. 重新构建并记录最终 EXE 哈希。
4. 提交和推送源码、清单与正式素材，不提交 EXE。
5. 创建与项目版本一致的 Git 标签和 GitHub Release。
6. 将 EXE 作为 Release 附件上传，并在干净目录重新下载核对 SHA-256。

## v1.0.77 发布验证

以下源码差异、Release 构建、定向回归、完整 WPF 契约与受控 reminder A/B 于 `2026-08-14` 完成；真实提交、标签、EXE 和 GitHub Release 证据待发布后回填。

| 项目 | 当前实测或待验证结果 |
| --- | --- |
| 剪贴板 | 三窗口 Copy / Cut、占用 fail-closed、最新跨窗口请求胜出、外部 sequence 淘汰、绝对 `300 ms` 截止、Hide/Closed 取消与 ASCII 数字粘贴均由 `--todo-cut-only` 覆盖；普通正文 Paste 不绑定、不改写 |
| 多屏与 DPI | `GetCursorPos` 保留 32 位虚拟桌面坐标；不可变抓点、generation 失效、`Loaded → ContextIdle`、物理工作区 clamp 与最多 3 次松手重试由 `--roam-interaction-only` 覆盖。当前机器没有可用于最终验收的混合 DPI 双屏，因此发布说明只把自动化和单屏 WPF 回归列为已验证，混合 DPI 实机矩阵仍需人工冒烟 |
| 长文预览 | 首选 `360 × 280 DIP`，正文 `64–218 DIP`；负坐标工作区、`300 × 200 DIP` 窄区缩放、四边 `16 DIP` inset、左优先/右回退、关闭释放和右键菜单 `220 ms` 生命周期均通过 `--todo-layout-only` |
| reminder 内存 | 完整热集 exact `33,652,536 B`，当前 manifest best-fit worst `37,583,616 B`，`36 MiB` 余量 `165,120 B`。受控三轮中 resident/pool `47.06 → 33.90 MiB`、managed `65.24 → 52.08 MiB`、Private `186.88 → 173.76 MiB`、Working Set `246.23 → 233.19 MiB`；预热后两圈 `+0 allocation / +0 reuse / 0 pending` |
| 自动化与构建 | `DesktopPet` 与 `UiStateChecks` Release 构建为 `0 warning / 0 error`；`--todo-cut-only`、`--todo-layout-only`、`--scheduled-editor-only`、`--todo-only`、`--reminder-only`、`--resident-cache-only`、`--roam-interaction-only`、`--edge-dock-only`、`--pet-size-only`、`--work-mode-only`、`--clip-clock-only`、`--reaction-random-only`、`--deadline-only` 均通过；完整 `UiStateChecks` 连续两轮输出 `UI state checks passed.` |
| 产品质量边界 | 不改 Assets、manifest、像素、画质、帧数、FPS、作者绝对时间线、动作选择、任务存储或提醒语义；DPI 失败路径安全跳过吸附，不从混合几何启动动画。内存数字仅代表受控 reminder 场景，Working Set 仍受系统压力影响 |
| 源码与发布 | 待真实发布后回填：功能提交、tag/Release target、EXE bytes、SHA-256、FileVersion、ProductVersion、Authenticode、附件 size/digest 与独立回下载复核 |

## v1.0.76 发布验证

以下源码差异、构建、自动回归与受控真实 WPF 对照于 `2026-08-13` 完成；真实提交、标签、EXE 和 GitHub Release 证据待发布后回填。

| 项目 | 当前实测或待验证结果 |
| --- | --- |
| 动态 reaction 热集 | 完整 wake / action / loop / reverse clip 的 resident 上限分别为 cry `99 MiB`、cute `83 MiB`、like `109 MiB`、eat `97 MiB`；提醒页同时预载时 `+12 MiB`。roam/preload `92 MiB` 与 work `57/73 MiB` 状态优先，其他状态 `52 MiB`，稳定 idle `20 秒`后深裁至 `12 MiB` |
| 退出与所有权 | natural、edge、Todo、Reminder 与 page failure 均立即按新状态收敛 `52 MiB`；pool `92 MiB` hard budget 仍只约束 free storage。锁屏保留 active reaction；点击接管 roam preload 只取消不可见 decode 并保留 due cadence |
| 真实 WPF 对照 | 候选 10/10 轮 cold pending 为 0，原 `52 MiB` 基线 10/10 轮均出现；四动作两轮均值的分页获取下降 `88.4%–91.6%`、managed allocation 下降 `66.9%–76.9%`、进程 CPU 下降 `54.0%–72.5%`。这些结果只适用于受控动作场景，不外推为全局固定降幅，也不声称所有 Rendering 最大间隔下降 |
| 内存剖面 | startup idle resident/private/working 为 `10.86/138.52/197.55 MiB`；active cry reaction 为 `90.15/219.23/299.92 MiB`；依次完成全部 reaction 后为 `46.38/173.95/283.36 MiB`；active roam 为 `79.53/208.43/337.73 MiB`；trimmed idle 为 `10.86/139.11/269.07 MiB`。Working Set 受文件缓存与系统压力影响，仅作同次剖面观察；reaction resident 是有意的短时换稳定，不声称稳态内存下降 |
| 自动化与构建 | `DesktopPet` 与 `UiStateChecks` Release 构建均为 `0 warning / 0 error`；`--resident-cache-only`、`--clip-clock-only`、`--deadline-only`、`--edge-dock-only`、`--roam-interaction-only`、`--todo-only`、`--reminder-only`、`--work-mode-only`、`--pet-size-only` 均通过，`--memory-profile` 完成且数值契约满足；完整 `UiStateChecks` 连续两轮输出 `UI state checks passed.` |
| 产品质量边界 | Assets、manifest、像素、画质、帧数、FPS、作者绝对时间线、输入、Todo、定时任务与提醒业务不变；reaction clip 内部页面前瞻顺序和动作选择不变，仅点击抢占尚未显示的 roam preload 时取消旧所有者 decode，原 roam due cadence 不变 |
| 源码与发布 | 待真实发布后回填：功能提交、tag/Release target、EXE bytes、SHA-256、FileVersion、ProductVersion、Authenticode、附件 size/digest 与独立回下载复核 |

## v1.0.75 发布验证

以下源码差异、构建、自动回归、同机隔离 A/B、真实发布与独立回下载验证于 `2026-08-13` 完成。

| 项目 | 当前实测或待验证结果 |
| --- | --- |
| 像素复用边界 | 仅 `Typing` normal / serious loop，在 zero blend、无 active blend、direct provenance / bounds 匹配且前后帧 page/source/destination 描述符完全相同时跳过 `CopyFramePixels + WritePixels`。逻辑 frame/name/index、descriptor callback、枕头、呼噜、绝对时钟和预取继续原路径；其他状态、clip、blend、bounds 或描述符差异均真实写入 |
| 静态上限 | normal / serious 作者序列各 96 帧、各 23 处相邻重复候选；每次命中避免 `656,844 B`，完整作者顺序圈最多避免 `15,107,412 B`（`14.408 MiB`）及 23 次 `WritePixels`。serious 实际以 2 倍速呈现并会跳帧，23 不是其真实每圈命中数 |
| normal CPU 隔离 A/B | 同机两轮交替的真实 `MainWindow` normal 稳定段：候选 `8.5677% / 7.2135%`，基线 `13.4635% / 12.4740%`；合并均值为基线 `12.96875%` → 候选 `7.890625%`，绝对减少 `5.078125` 个百分点、相对减少 `39.1566%`。仅代表该隔离场景，不声称全局固定 CPU 降幅 |
| 内存与缓存 | 工作精确 resident 热集仍为 `55,392,540 B`；resident / pool 均保持 4 页，LRU 与预算不变，Private / Working Set 未见持续增加。本版不声称降低内存 |
| 自动化与构建 | `DesktopPet` 与 `UiStateChecks` 两个 Release 构建均为 `0 warning / 0 error`；`--work-mode-only`、`--clip-clock-only`、`--resident-cache-only`、`--roam-interaction-only`、`--todo-only`、`--reminder-only`、`--edge-dock-only`、`--pet-size-only` 专项全部通过，完整 `UiStateChecks` 输出 `UI state checks passed.` |
| 审计拒绝项 | action-limited prefetch 候选未进入版本功能：真实 WPF like-L2 仍只有 `2/3` cold boundary 通过，未满足质量零妥协门槛；当前预取行为保持不变 |
| 产品质量边界 | Assets、manifest、像素、画质、帧数、FPS、动画与输入时序、Todo、定时任务、提醒、resident/pool/LRU 和预取语义不变；只消除已证明相同的重复显示提交 |
| 源码与版本 | 功能提交 `146e9512cb55570a5dbb3258ed63f12e24b325ee`；`FileVersion=1.0.75.0`，`ProductVersion=1.0.75+146e9512cb55570a5dbb3258ed63f12e24b325ee` |
| EXE | 单文件框架依赖 EXE 为 `93,348,998` bytes，SHA-256 `AEA69C285F4AA4B38DC6BC33DF4D57CCB82F8F70EDFDF63387713332C4FE47B3`；Authenticode 状态为 `NotSigned` |
| GitHub Release | [`v1.0.75`](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.75) 为非 Draft、非 prerelease；annotated tag 剥离后的提交、Release target 与功能提交一致，且只有一个 `LubanDesktopPet.exe` 附件。附件元数据 size 为 `93,348,998`、digest 为 `sha256:aea69c285f4aa4b38dc6bc33df4d57ccb82f8f70edfdf63387713332c4fe47b3`；独立全新目录回下载后的大小、SHA-256、FileVersion、ProductVersion 与签名状态逐项一致。验真过程未运行附件，也未停止或替换用户正在运行的 `v1.0.69` |

## v1.0.74 发布验证

以下源码差异、本地回归、语义终审、真实发布与独立回下载验证于 `2026-08-13` 在同一 Windows / .NET SDK `8.0.418` 环境完成。

| 项目 | 当前实测或待验证结果 |
| --- | --- |
| 动态预算边界 | 普通、normal 工作、serious 工作、巡游与稳定空闲分别为 `52 / 57 / 73 / 92 / 12 MiB`，LOH 门槛仍为 `8 MiB`。serious requested 在尚未成为 active clip 时即选择 `73 MiB`，enter / loop / exit 全部保持；绕屏 active / preload 始终优先选择 `92 MiB`，normal 单击仍保持 `57 MiB` |
| 静态容量证明 | normal idle + 3 页精确为 `55,392,540 B`（`52.8264 MiB`），best-fit 最坏可达 `59,191,344 B`（`56.4493 MiB`），距 `57 MiB` 上限 `577,488 B`。serious idle + 3 loop + serious-exit 精确为 `71,156,796 B`（`67.8604 MiB`），best-fit 最坏可达 `75,982,272 B`（`72.4623 MiB`），距 `73 MiB` 上限 `563,776 B` |
| serious 自然往返 | normal → serious → normal 首次只有 4 次 serious 必要分页获取与 3 次 normal 必要分页获取，合计 7 次；预热后连续两个完整 serious 周期为 `+0 allocation / +0 reuse`，没有冷页 pending。serious-exit 完成同一调用恢复 normal `57 MiB`，退出工作恢复 `52 MiB`，深裁后唯一 idle 页为 `11,383,992 B` 并低于 `12 MiB` |
| serious 右键顺序 | serious loop 到中性缝前保留 serious-exit resident，work-exit 不得成为 desired / prefetch 或增加分页获取；中性缝上的 serious-exit 首帧立即显示、无 pending，随后在 serious-exit 播放窗口内预热 work-exit，并无冷页停顿地进入 work-exit。normal loop 和 work-enter 中途退出的早预热保持不变 |
| 产品质量边界 | 本轮只改动 resident 预算选择和分页预取顺序；动画 Assets、manifest、像素、画质、帧数、FPS、绝对时序、输入、Todo、定时任务、提醒和其他状态机不变。serious resident 在该短时状态内有意提高，不将本轮表述为常驻内存下降或固定 CPU 降幅 |
| 自动化与终审 | `DesktopPet` 与 `UiStateChecks` 的 Release 构建均为 `0 warning / 0 error`；强化后 `--resident-cache-only` 通过，完整 `UiStateChecks` 输出 `UI state checks passed.`。独立只读语义终审通过，确认动态预算优先级、serious 右键 guard 边界和 normal / work-enter 旧行为保留 |
| 源码与版本 | 功能提交 `b33af9875f8d8e2b6703db82ec2d83f59fce3db2`；`FileVersion=1.0.74.0`，`ProductVersion=1.0.74+b33af9875f8d8e2b6703db82ec2d83f59fce3db2` |
| EXE | 单文件框架依赖 EXE 为 `93,348,486` bytes，SHA-256 `83B409F77D537C87695FAC3418AD8735E4FA610A6A0298555DAC2F3A3BFB913B`；Authenticode 状态为 `NotSigned` |
| GitHub Release | [`v1.0.74`](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.74) 为非 Draft、非 prerelease；标签与 Release target 均指向功能提交，且只有一个 `LubanDesktopPet.exe` 附件。附件元数据 size 为 `93,348,486`、digest 为 `sha256:83b409f77d537c87695fac3418ad8735e4fa610a6a0298555dac2f3a3bfb913b`；独立新目录回下载后的大小、SHA-256、FileVersion、ProductVersion 与签名状态逐项一致。验真过程未运行附件，也未停止或替换用户正在运行的 `v1.0.69` |

## v1.0.73 发布验证

以下构建、定向等价测试、隔离 A/B 与完整自动化结果于 `2026-08-12` 在同一 Windows / .NET 环境中取得；功能提交、Git 标签、GitHub Release 与独立回下载验真于 `2026-08-13` 完成。

| 项目 | 当前实测或待验证结果 |
| --- | --- |
| 精确水平镜像契约 | 快路径只接受逆矩阵逐项严格等于 `[-1,0;0,1,width,0]` 的完整水平镜像；平移、缩放、分数偏移、旋转、alias、非正尺寸及其他矩阵均拒绝快路径并保持旧 axis/general 算法。6 种尺寸和真实 `GetPetVisualMatrix` 与旧路径逐字节一致；素材、像素、帧数、FPS、时序和输入语义不变 |
| 镜像 A/B | 纯镜像中位约 `3.93 → 0.15 ms`；计入 `WritePixels` 的完整显示提交约 `4.49–4.57 → 0.202–0.218 ms`。该代码只在低频右侧边界交互触发，不将微基准外推为全局 CPU 显著下降 |
| 工作热集边界 | normal / serious 各自需要固定 idle 页加 3 个工作页，精确为 `55,392,540 B`（`52.826 MiB`）；当前 manifest 在相邻容量 best-fit 规则下可达到的最坏热集为 `59,191,344 B`，低于工作专用 `57 MiB`。普通 `52 MiB`、巡游 `92 MiB`、稳定空闲 `12 MiB` 与 LOH `8 MiB` 门槛不变 |
| 工作分配 A/B | 原 `52 MiB` 每 10 秒出现 `+12 / +13 allocation`，汇总托管分配为 `17.881 MiB/s`；`57 MiB` 预热后 normal / serious 两个循环均为 `+0 allocation / +0 reuse`，托管分配为 `0.0859 MiB/s`（`-99.52%`） |
| resident 取舍 | 工作 active resident 从 baseline `37.17–39.67 MiB` 提高到 candidate 恒定 `52.83 MiB`。这是明确的空间换稳定：保留完整热集以消除逐圈淘汰、解码和 LOH 分配，不声称工作 resident 本身下降 |
| 进程 A/B | 每组从第 `20–70 s` 取 `6` 个稳定样本。Private：baseline `308.61–425.75 MiB`、中位 `355.87 MiB`；candidate `253.86–282.27 MiB`、中位 `282.04 MiB`。Working Set：baseline `382.95–501.45 MiB`、中位 `432.96 MiB`；candidate `328.22–355.94 MiB`、中位 `355.29 MiB`。范围离散且属于跨运行总体 A/B，只记录观察，不把中位差写成固定节省 |
| CPU 观察 | 同一串行跨运行采样为 `16.41% → 12.42%`；只作为减少解码抖动的方向性信号，不能隔离调度、机器负载等运行间变量，也不能声称稳定 CPU 降幅 |
| 产品质量边界 | 动画素材、像素、画质、帧数、FPS、绝对时序、输入、Todo、定时任务、提醒及其他功能不变；不通过降清晰度、删帧、降帧率或改变交互换取指标 |
| 完整自动化 | `DesktopPet` 与 `UiStateChecks` 的 Release 构建连续执行均为 `0 warning / 0 error`；8 个 pass/fail 专项 `--resident-cache-only`、`--work-mode-only`、`--clip-clock-only`、`--edge-dock-only`、`--todo-only`、`--roam-interaction-only`、`--pet-size-only`、`--reminder-only` 全部通过；`--memory-profile` 已完成剖面输出且数值契约满足，该入口不是 pass/fail 测试。完整 `UiStateChecks` 在最小测试夹具隔离修复后连续 2 轮均输出 `UI state checks passed.`。另有 fresh snore `10/10` 与 edge + deadline + snore `3/3` 隔离诊断通过，不替代完整套件 |
| 源码与版本 | 功能提交 `011bb136d4d833cd997ef7249e0d59d7dca8852d`；`FileVersion=1.0.73.0`，`ProductVersion=1.0.73+011bb136d4d833cd997ef7249e0d59d7dca8852d` |
| EXE | 单文件框架依赖 EXE 为 `93,347,974` bytes，SHA-256 `9476E8B95855B09FB4B0BDFFBC504A7C7E151AF5CCB3C5CA3BC96F11F17BB4FD`；Authenticode 状态为 `NotSigned` |
| GitHub Release | [`v1.0.73`](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.73) 为非 Draft、非 prerelease；标签与 Release target 均指向功能提交，且只有一个 `LubanDesktopPet.exe` 附件。附件元数据 size 为 `93,347,974`、digest 为 `sha256:9476e8b95855b09fb4b0bdffbc504a7c7e151af5ccb3c5ca3bc96f11f17bb4fd`；独立新目录回下载后的大小、SHA-256、FileVersion、ProductVersion 与签名状态逐项一致。验真过程未运行附件，也未停止或替换用户正在运行的 `v1.0.69` |

## v1.0.72 发布验证

以下结果于 `2026-08-12` 在 .NET SDK `8.0.418` 环境完成；功能、构建、真实发布及独立回下载验证均已完成。

| 项目 | 实测结果 |
| --- | --- |
| idle-trim 调度 | 提醒待确认、可见 Todo、打工、会话 inactive 和绕屏这五类长期阻塞会停止 idle-trim `DispatcherTimer`，不再每 `5 秒`空轮询；退出后按原 `20 秒`宽限重新安排。desired、prefetch、拖动、缩放和 `Rendering` 等短期阻塞继续保留 `5 秒` watchdog |
| 隔离探针 | 每次 handler 中位耗时 `1.860 us`、临时分配 `440 B`；持续阻塞一天消除 `17,280` 次 UI Dispatcher 唤醒，折合约 `32.1 ms` 直接 handler CPU 和 `7.25 MiB/天`可回收分配。该量级不用于声称常驻内存下降或明显 CPU 降幅 |
| 产品质量边界 | collection timer、GC 淘汰债务、LOH `30 秒`节流、`52 / 92 / 12 / 8 MiB` 预算、素材、像素、画质、帧数、FPS、时序、输入、待办、提醒和其他交互全部不变 |
| 自动化与构建 | `DesktopPet` 与 `UiStateChecks` 的 Release 构建均为 `0 warning / 0 error`；`--resident-cache-only`、`--work-mode-only`、`--todo-only`、`--reminder-only`、`--roam-interaction-only` 专项全部通过；完整 `UiStateChecks` 输出 `UI state checks passed.` |
| 内存剖面 | startup idle：resident / pool / managed / private 为 `10.86 / 10.86 / 15.90 / 137.09 MiB`；active roam 为 `79.53 / 79.53 / 84.60 / 208.59 MiB`；trimmed idle 为 `10.86 / 10.86 / 15.93 / 139.27 MiB`。这些结果验证内存边界未回退，不声称本轮降低常驻内存 |
| 源码与版本 | 源码版本 `v1.0.72`；功能提交 `ec8a0a3f5476c7567b523b3c886a4ae411c985a2`，`FileVersion=1.0.72.0`，`ProductVersion=1.0.72+ec8a0a3f5476c7567b523b3c886a4ae411c985a2` |
| EXE | 单文件框架依赖 EXE 为 `93,347,462` bytes，SHA-256 `4F9911BF5F2F998AB023D8048A958A87865880E7AE001095DA660C051819E9DA`；Authenticode 状态为 `NotSigned` |
| GitHub Release | `v1.0.72` 为非 Draft、非 prerelease，标签与 Release target 均指向功能提交 `ec8a0a3f5476c7567b523b3c886a4ae411c985a2`；附件元数据 digest 为 `sha256:4f9911bf5f2f998ab023d8048a958a87865880e7ae001095da660c051819e9da`，独立新目录回下载后的大小、SHA-256、FileVersion 与 ProductVersion 均和发布源逐项一致 |

## v1.0.71 发布验证

以下结果于 `2026-08-12` 在 .NET SDK `8.0.418` 环境完成。功能提交、标签和 GitHub Release 均固定到 `9008ec7765a48be77740519393fb645b9ddae952`；Release 附件已下载到独立目录复核。为避免影响用户正在运行的 `v1.0.69`，本次没有停止或替换该进程及其 EXE。

| 项目 | 实测结果 |
| --- | --- |
| 直接帧提交 A/B | old/new bounds 并集和单次 `WritePixels` 保持不变，只清 previous 与 next 不重叠差集；正式 bounds 模型清零流量 `479.886 → 1.929 MiB`（`-99.60%`），计入 Copy 后模型流量 `-33.24%`，隔离微基准 `32.822 → 21.179 us/帧`（`-35.5%`） |
| delta-sub A/B | 逐行 `Buffer.BlockCopy` 保留可见/屏外透明/重复 sprite/overlap/hash/fail-closed 契约；warm `work-loop-part-02` 中位数 `33.427 → 13.348 ms`（`-60.1%`），全部 16 个 delta 页 warm 总中位数 `1052.763 → 798.671 ms`（`-24.1%`） |
| 产品质量边界 | Assets、manifest、人物像素、画质、帧数、FPS、时序、输入、待办、提醒及 `52 / 92 / 12 / 8 MiB` 预算不变；本轮不声称降低稳态内存 |
| 源码与版本 | 功能提交 `9008ec7765a48be77740519393fb645b9ddae952`；`FileVersion=1.0.71.0`，`ProductVersion=1.0.71+9008ec7765a48be77740519393fb645b9ddae952` |
| 自动化与构建 | `DesktopPet` 与 `UiStateChecks` Release 构建均为 `0 warning / 0 error`；`--atlas-hash-only`、`--memory-profile`、完整 `UiStateChecks` 全部通过；Python 图集测试 `29 / 29` 通过；隔离单文件 `dotnet publish` 退出码为 `0` |
| EXE | `LubanDesktopPet.exe`，`93,347,462` bytes，SHA-256 `6D66F890B904633E6DE57E042D14EA42160E40DCBB183791CEB54D6495246C21`，Authenticode `NotSigned` |
| GitHub Release | [`v1.0.71`](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.71) 为正式非草稿、非预发布 Release；标签和 Release target 均为功能提交，附件 size/digest 与本地一致，独立回下载后的大小、SHA-256、文件版本及产品版本全部一致 |

## v1.0.70 发布验证

以下结果于 `2026-08-12` 在 .NET SDK `8.0.418` 环境完成。Release 附件已在独立目录重新下载复核；为避免中断用户正在运行的 `v1.0.69`，本次没有停止进程或替换用户路径 EXE。

| 项目 | 实测结果 |
| --- | --- |
| 源码与版本 | 功能提交 `03322f7ff92fbd252c08c40c75113d7966cd4ce3`；文件版本 `1.0.70.0`，产品版本 `1.0.70+03322f7ff92fbd252c08c40c75113d7966cd4ce3` |
| 缓冲与缓存语义 | 精灵页缓冲新分配按真实解码字节申请；free 数组继续在旧有相邻容量复用边界内 best-fit 选择最小足够数组；全部绕屏页就绪后只丢弃 free decode arrays，resident 正播与逆播帧不动；预算保持常规 `52 MiB`、巡游 `92 MiB`、稳定空闲 `12 MiB`、LOH 淘汰债务 `8 MiB`，图集、像素、帧率和时序不变 |
| 自动化与构建 | DesktopPet 与 UiStateChecks 的 Release 构建均为 `0` 警告、`0` 错误；完整 UiStateChecks 输出 `UI state checks passed.` |
| resident / managed 剖面 | 同一 `--memory-profile` baseline → candidate：idle resident `11.00 → 10.86 MiB`；active reaction resident `49.00 → 47.77 MiB`、managed `57.12 → 55.37 MiB`；all reactions resident `48.00 → 46.38 MiB`、managed `53.13 → 51.53 MiB`；active roam resident `82.00 → 79.53 MiB`、managed `87.14 → 84.70 MiB` |
| 进程 Private | 三轮巡游 Private 中位数 baseline → candidate 为 `209.43 → 208.08 MiB`；这是小幅实测下降，不声称内存减半 |
| EXE | framework-dependent 单文件 EXE 为 `93,346,438` 字节，SHA-256 `F27257B3EE7525770D5C753B09ADDCBA679C15BFB6F1F8737A3749F0702F221E`，Authenticode 状态 `NotSigned`；隔离发布目录只包含这一个文件，本次未替换用户路径 EXE |
| GitHub Release | 已发布 [v1.0.70](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.70)，非草稿、非预发布；标签与 Release 均指向功能提交 `03322f7ff92fbd252c08c40c75113d7966cd4ce3`，附件大小为 `93,346,438` 字节，digest 为 `sha256:f27257b3ee7525770d5c753b09addcba679c15bfb6f1f8737a3749f0702f221e`，独立回下载的字节数、SHA-256、文件版本和产品版本与本地候选完全一致 |

## v1.0.69 发布验证

以下结果于 `2026-08-11` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418` 环境完成。正式 EXE 已替换到用户指定路径并保持运行，Release 附件也已在独立目录重新下载复核。

| 项目 | 实测结果 |
| --- | --- |
| 源码与版本 | 功能提交 `dbf5090fc9fb0e6cbd504986b4a9412eca9bf174`；文件版本 `1.0.69.0`，产品版本 `1.0.69+dbf5090fc9fb0e6cbd504986b4a9412eca9bf174` |
| 三项定向回归 | `--roam-interaction-only`、`--edge-dock-only`、`--work-mode-only`、`--resident-cache-only` 全部通过；覆盖静止光标下窗口逆播不误判拖动、无 `MouseMove` 的快速松开仍正确拖动吸附、太阳拖动中同步可见但不可命中、边缘隐藏，以及 Rendering 回调只排队不直接淘汰分页 |
| 自动化与构建 | DesktopPet、UiStateChecks、TodoStoreChecks 的 Release 构建均为 `0` 警告、`0` 错误；完整 UiStateChecks 输出 `UI state checks passed.`；TodoStore、AppSettingsStore、ScheduledTaskStore 检查通过 |
| 内存实测 | `--memory-profile` 中 startup idle 为 resident `11.00 MiB`、managed `16.13 MiB`、private `136.87 MiB`；active roam 为 resident / pool `82.00 / 82.00 MiB`、managed `87.16 MiB`、private `210.11 MiB`；完成巡游后 trimmed idle 收敛为 resident / pool `11.00 / 11.00 MiB`、managed `16.16 MiB`、private `138.20 MiB`。普通动作 `20 秒`缓存、`52 / 92 MiB` 预算及图集素材和帧数未变 |
| EXE 与用户数据 | 单文件 EXE 为 `93,345,926` 字节，SHA-256 `4027B8112CEABF0A4D4E95B07CDFD88B4B2917CBE004D946CBD7FB4637A616F8`，Authenticode 状态 `NotSigned`；用户路径仅一个进程（PID `6480`）且响应正常。替换前后 `todos.json`、`settings.json`、`scheduled-tasks.json` 哈希分别保持 `BA09FAF7D7CD2DA5FC98E47A9E4C6ADF08019ACB4F23E15A6849A0D03E645C11`、`7A938AD904254D85CD5D287EA7208E2FE439B85A21CB890BBD2E093112483E7C`、`8BFA42E4BF20E9006F9BCD9018A93F48F54B74C464835C29D9A5E9906198785E` |
| GitHub Release | `v1.0.69` 标签与 Release 均指向功能提交 `dbf5090fc9fb0e6cbd504986b4a9412eca9bf174`；附件 digest 为 `sha256:4027b8112ceabf0a4d4e95b07cdfd88b4b2917cbe004d946cbd7fb4637a616f8`，独立回下载 SHA-256 与本地候选完全一致 |

## v1.0.68 发布验证

以下结果于 `2026-08-10` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418` 环境完成。正式 EXE 已替换到用户指定路径并保持运行，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码与版本 | 正式功能提交 `aa2b13c2244fafa590778d79d98dc3fc139ab715`；文件版本 `1.0.68.0`，产品版本 `1.0.68+aa2b13c2244fafa590778d79d98dc3fc139ab715` |
| 三项定向回归 | 循环展开免打扰后，定时列表 `166 → 134 DIP`、滚动视口 `164 → 132 DIP`，内容 extent 保持 `193 DIP`，滚动到底后最后一项完整落在列表边界内。隐藏 Todo HWND 被压成半宽并移到 `(-25000,-25000)` 后，不经过逻辑恢复即可由生产定位器按锚点 DPI 恢复 `292×414 DIP` 对应的原生尺寸并夹入工作区；隐藏恢复再右键打开的完整链路也通过。巡游点击在 `150%` DPI 下验证 `5.9 px` 不触发、`6.0 px` 触发 `4 DIP` 拖动阈值，窗口局部坐标漂移 `48 DIP` 而屏幕指针不动时不误拖；随后以生产 `AdvanceEdgeRoaming` 的 `60 Hz` 时钟自然完成退场并稳定回到 idle |
| 自动化与构建 | DesktopPet、UiStateChecks、TodoStoreChecks Release 构建均为 0 警告、0 错误；`--todo-only`、`--reminder-only`、`--roam-interaction-only`、`--edge-dock-only`、`--work-mode-only`、TodoStoreChecks 和完整 UiStateChecks 全部通过。完整套件还验证了 `49/52 MiB` 普通分页容量、`89/92 MiB` 巡游容量和空闲 resident `32 → 11 MiB` / pool `32 → 12 MiB` 的既有内存边界 |
| EXE 与用户数据 | 正式 EXE 为 `93,345,414` 字节，SHA-256 `3E4D215058F8421C8DC06DB2D11E9B1F825E9A637326F996D2C458FE6373442A`，`NotSigned`。用户路径启动后响应，二次启动自行退出且始终只有一个实例；恢复版与用户路径两个 `dist` EXE 完全一致。Run 键仍指向 `F:\agent\pet\dist\LubanDesktopPet.exe --autostart`；`todos.json`、`settings.json`、`scheduled-tasks.json` 在备份、替换和启动前后 SHA-256 完全不变 |
| GitHub Release | 已发布 [v1.0.68](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.68)；标签与 Release 目标均为功能提交 `aa2b13c2244fafa590778d79d98dc3fc139ab715`。GitHub 附件大小与 digest、独立回下载、本地候选、恢复版 `dist` 和用户路径 EXE 的大小及 SHA-256 完全一致 |

## v1.0.67 发布验证

以下结果于 `2026-08-10` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418` 环境完成。正式 EXE 已替换到用户指定路径并保持运行，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码与版本 | 正式功能及测试隔离提交 `e4b63d9a8af1a302ce44aea429131d083794342e`；文件版本 `1.0.67.0`，产品版本 `1.0.67+e4b63d9a8af1a302ce44aea429131d083794342e` |
| 免打扰尾部提示 | 真实 WPF 排版中，固定 `292 DIP` 任务面板的“可跨夜”文字槽为 `35.00 DIP`、字体自然宽度为 `28.50 DIP`；`350 DIP` 最小修改窗中分别为 `49.00 / 30.00 DIP`。两处均为 `NoWrap / TextTrimming=None`，没有元素 Clip、布局 Clip 或越过行容器右边界 |
| 自动化与构建 | DesktopPet、UiStateChecks、TodoStoreChecks Release 构建均为 0 警告、0 错误；`--scheduled-editor-only`、`--todo-only`、TodoStoreChecks 和完整 UiStateChecks 全部通过。完整套件中的尺寸手势复用新 `--pet-size-only` 进程，原 30 次同值点击 `scaleEvents == 0` 严格断言未放宽 |
| EXE 与用户数据 | `F:\agent\pet\dist\LubanDesktopPet.exe` 启动后响应，二次启动以退出码 0 自行退出且始终只有一个实例；Run 键路径不变。`todos.json`、`settings.json`、`scheduled-tasks.json` 在备份、替换和启动前后 SHA-256 完全不变。正式 EXE 为 `93,343,366` 字节，SHA-256 `9EA13CF78FCA9670FB007B8DA47F2576800ECA864967FD94C1A23E2B65721135`，`NotSigned` |
| GitHub Release | 已发布 [v1.0.67](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.67)；标签与 Release 目标均为 `e4b63d9a8af1a302ce44aea429131d083794342e`，GitHub digest、独立回下载、本地候选、恢复版 `dist` 和用户路径 EXE 的大小及 SHA-256 完全一致 |

## v1.0.66 发布验证

以下结果于 `2026-08-10` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换到用户指定路径并保持运行，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码、版本与图集 | 正式功能提交 `85109464444a02b38b13776157c66cd6b891c03a`；文件版本 `1.0.66.0`，产品版本 `1.0.66+85109464444a02b38b13776157c66cd6b891c03a`；正式清单仍为 41 页、1240 个源帧和 1240 个分页帧 |
| 侧边完整短弯前臂 | 左侧 48/48 帧唯一，循环 ROI Alpha IoU 最低 `0.957418`、面积变化最高 `3.2806%`、腕心步进最高 `1.5495 px`、曲线端点步进最高 `1.9621 px`、水平底边最长 `6 px`、端点桥最小 Alpha 面积 `295 px`；右侧继续精确水平镜像。真实 `190×242 DIP` 运行渲染按 `0.75 / 1.40` 缩放检查左右浅探、深探及浅/深背景，均无下方缺口、闪断、旧横管或平切。Bottom 的 52 张 PNG、图集页和清单条目与上一版完全一致 |
| 可复建与图集 | 完整 `install → generate --edge-peek → fix` clean path 通过，4 个 key 精确对应 `f048 / f012 / f024 / f036`；48 帧二次投影为零像素变化。Dense Motion QA 为 `failures=[]`，Atlas Motion QA 为 41 页、1240 帧、`failure_count=0`；41 个图集页中仅 `luban-edge-left.pbgra.br` 改变，其 SHA-256 为 `DFD6BF4A57228604787F79543C72B89C83CA0B1EDBC00962EA04C39E84FD2F9D` |
| 自动化、构建与内存 | Python 37/37 通过；DesktopPet、UiStateChecks、TodoStoreChecks Release 构建均为 0 警告、0 错误。`--edge-dock-only`、`--work-mode-only`、`--resident-cache-only`、`--atlas-hash-only`、`--memory-profile`、`--todo-only`、三套存储检查及完整 UiStateChecks 全部通过。普通/绕屏容量为 `49/52 MiB`、`89/92 MiB`，空闲裁剪后 resident/pool 为 `11/11 MiB`、managed 约 `16.05 MiB` |
| EXE 与用户数据 | `F:\agent\pet\dist\LubanDesktopPet.exe` 启动后响应，二次启动以退出码 0 自行退出且始终只有一个实例；Run 键路径不变。`todos.json`、`settings.json`、`scheduled-tasks.json` 在备份、替换和启动前后 SHA-256 完全不变。正式 EXE 为 `93,342,854` 字节，SHA-256 `3EEF809A71056316FDDEFB8231129C7E51354595A4877A2CB02E8620BFE003CD`，`NotSigned` |
| GitHub Release | 已发布 [v1.0.66](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.66)；标签与 Release 目标均为功能提交 `85109464444a02b38b13776157c66cd6b891c03a`，GitHub digest、独立回下载、本地候选、恢复版 `dist` 和用户路径 EXE 的大小及 SHA-256 完全一致 |

本机只有一块 `2560×1440` 显示器，因此多屏、负坐标副屏和混合 DPI 结论仍来自自动化矩阵，不冒充本轮真实双屏硬件验收。Computer Use 能确认桌宠按设计不进入普通可定位窗口列表；本轮真实输入复现使用诊断拖放链，最终左右人物画面使用正式 Assets 和运行缩放链逐帧验收，Bottom 则以源文件、图集页和清单条目逐字节锁定。

## v1.0.65 发布验证

以下结果于 `2026-08-10` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换到用户指定路径，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码、版本与图集 | 正式功能提交 `aead0d84a5cfa781819c426619d4f3f4b5115995`；文件版本 `1.0.65.0`，产品版本 `1.0.65+aead0d84a5cfa781819c426619d4f3f4b5115995`；正式清单为 41 页、1240 个源帧和 1240 个分页帧 |
| 四种点击与退役资源 | `--reaction-random-only`、`--clip-clock-only` 均通过；真实失败路径确认忙碌时不提交点击历史，下一次成功仍排除上一个成功动作。生产源码、XAML、项目 Resource、清单及最终 EXE 对 `star-wish / StarWish / WishStar / luban-wish-star` 和原中文对白均为 0 命中 |
| 左右紧凑支撑手臂 | 48/48 帧唯一，循环 ROI Alpha IoU 最低 `0.96237`、面积变化最高 `1.865%`、腕心步进最高 `1.868 px`、水平底边最长 `6 px`；Dense QA、Atlas QA 和左右浅/深背景 100%/140% 逐帧视觉检查全部通过。右侧为左侧精确镜像；Bottom 文件及解码像素哈希保持不变 |
| 缓存与内存 | `--resident-cache-only` 与 `--memory-profile` 通过；缓存命中、重复、忙碌拒绝和仅 Rendering 延迟请求不会再重置空闲裁剪宽限，真正接管的新冷页请求才延长宽限。普通动作容量 `49/52 MiB`、绕屏容量 `89/92 MiB`；裁剪后 resident/pool 为 `11/11 MiB`，托管内存约 `16.05 MiB` |
| 自动化与构建 | Python 素材/图集测试 36/36 通过，Dense Motion 与 Atlas Motion QA 通过；DesktopPet、UiStateChecks、TodoStoreChecks Release 构建均为 0 警告、0 错误。TodoStore/AppSettingsStore/ScheduledTaskStore、随机动作、时钟、缓存、Todo、提醒、打工、边缘、Alt+Tab、图集哈希专项及完整 UiStateChecks 全部通过 |
| EXE 冒烟与用户数据 | `F:\agent\pet\dist\LubanDesktopPet.exe` 启动后响应；二次启动以退出码 0 自行退出且始终只有一个实例。`todos.json`、`settings.json`、`scheduled-tasks.json` 的 SHA-256 在备份、替换和启动后完全不变 |
| EXE 与 GitHub Release | `93,249,158` 字节，SHA-256 `8A864958742C1C65F48E60F46E45A93491B7C8709FC9A458B33D1C9042404BE7`，`NotSigned`；已发布 [v1.0.65](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.65)，标签与 Release 目标均为功能提交 `aead0d84a5cfa781819c426619d4f3f4b5115995`，GitHub digest、独立回下载、本地候选和两个 `dist` EXE 的大小及 SHA-256 完全一致 |

本机只有一块 `2560×1440` 显示器，因此多屏、负坐标副屏和混合 DPI 结论仍来自自动化矩阵，不冒充本轮真实双屏硬件验收；本轮实机覆盖最终用户路径启动、待机画面、单实例、用户数据不变，侧边人物素材则按左右浅/深背景 100%/140% 联系表及逐帧 GIF 验收。

## v1.0.64 发布验证

以下结果于 `2026-08-09` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换到用户指定路径，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码与版本 | 正式功能提交 `5e336b72b54e5ae4c5cbc0c42f1d3451b9a8d689`；文件版本 `1.0.64.0`，产品版本 `1.0.64+5e336b72b54e5ae4c5cbc0c42f1d3451b9a8d689` |
| 待办占位文字 | 新增输入框仅在空文本且未聚焦时显示“写下待办事项...”，输入或聚焦后立即隐藏；占位层不可命中。`--todo-only` 通过，原有 IME、`Ctrl+C`、`Ctrl+X`、回车新增、拖拽排序和完成项沉底契约未回退 |
| 太阳/月亮切换 | 硬切改为 `420 ms` 绝对时间轴：轻压、下沉旋转、弹入、双色柔光和单颗闪星均复用唯一 `CompositionTarget.Rendering + Stopwatch`。双向约 20 ms 实机抽帧确认无全透明空帧、闪白或图标横跳；中途反转、总透明度 `1.00–1.05`、59/60/120/144 Hz、250 ms 阻塞与稳态退订契约通过 |
| 自动化与构建 | DesktopPet 与 UiStateChecks Release 构建均为 0 警告、0 错误；`--todo-only`、`--work-mode-only`、完整 `UiStateChecks` 和 TodoStore/AppSettingsStore/ScheduledTaskStore 检查全部通过。图集与人物素材未改变 |
| EXE 冒烟与用户数据 | `F:\agent\pet\dist\LubanDesktopPet.exe` 启动后响应，二次启动自行退出且始终只有一个实例；`todos.json`、`settings.json`、`scheduled-tasks.json` 的 SHA-256 在替换和启动前后完全不变 |
| EXE 与 GitHub Release | `93,426,822` 字节，SHA-256 `6D65CCA809A7B457C9D9329ED57AC21EFCCB6B2F80B67D9CF097A688E5E88615`，`NotSigned`；已发布 [v1.0.64](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.64)，标签与 Release 目标为功能提交 `5e336b72b54e5ae4c5cbc0c42f1d3451b9a8d689`，GitHub digest、独立回下载、本地候选和两个 `dist` EXE 的大小及 SHA-256 完全一致 |

本机只有一块 `2560×1440` 显示器，因此多屏、负坐标副屏和混合 DPI 结论仍来自自动化矩阵，不冒充本轮真实双屏硬件验收；本轮实机覆盖待办占位文字、太阳/月亮双向切换、最终用户路径启动、单实例和用户数据不变。

## v1.0.63 发布验证

以下结果于 `2026-08-09` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换到用户指定路径，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码与版本 | 正式功能提交 `9229603db13804a10e8bbd939b6ddf5e033b8c89`；文件版本 `1.0.63.0`，产品版本 `1.0.63+9229603db13804a10e8bbd939b6ddf5e033b8c89` |
| 五种普通动作 | 运行时点击循环与随机袋严格为 `star-wish / cry / cute / like / eat`；旧 butterfly 与失败 star-cuddle 的 ActionName、人物页、资源入口和中文对白在生产代码、程序集与最终 EXE 中均为 0 命中 |
| 许愿星与 Todo | `star-wish` 逐帧复用 `cute` 56 帧及自然反向退场；唯一 `96×96` 透明星星以常驻、默认透明、不可命中的 `22 DIP` overlay 完成“画面右侧手边沿帽檐外缘升起 → 头顶轻摇 → 飞走”。真实 WPF 预览逐帧复核确认始终只有一颗星、不穿脸、不遮手、无残影或人物缩放；Todo 仍保持原有完整入场、稳定 `think` 姿势和反向退场 |
| 打工吸附与 Alt+Tab | 打工状态拖到左、右、下边缘时，热页直接原子切换到 edge-rest；冷页保持当前工作画面并冻结绝对时钟，加载完成后再原子交接，不再经过枕头待机。六个正式窗口的真实 HWND 均带 `WS_EX_TOOLWINDOW`、清除 `WS_EX_APPWINDOW`，且未设置 `WS_EX_NOACTIVATE`；`--work-mode-only`、`--edge-dock-only` 与 `--alt-tab-only` 均通过 |
| 图集与资源 | 正式清单保持 41 页、1240 个源帧和 1240 个分页帧；仅新增 `Assets/luban-wish-star.png` WPF Resource，旧蝴蝶和失败 144 帧人物素材、生成源及图集页均未打包；稠密素材 QA 与图集 QA 零失败，Python 测试 32/32 通过 |
| 内存 | 预算保持常规 `52 MiB`、巡游 `92 MiB`、稳定空闲 `12 MiB`、LOH 淘汰债务阈值 `8 MiB`。`--memory-profile` 实测启动空闲为 `11 MiB` resident / `16.03 MiB` managed / `136.17 MiB` private，活跃普通动作为 `46 MiB` resident / `171.62 MiB` private，活跃巡游为 `82 MiB` resident / `210.57 MiB` private，收缩后空闲为 `11 MiB` resident / `138.66 MiB` private |
| 自动化与实机 | DesktopPet、UiStateChecks、TodoStoreChecks 的 Release 构建均为 0 警告、0 错误；完整 `UiStateChecks`、三套存储检查、59/59.94/60/120/144 Hz、250 ms 阻塞、冷页、DPI、待办、定时任务和生产预览均通过。用户路径 EXE 短时启动保持响应，3 个用户数据文件哈希前后不变 |
| EXE 与 GitHub Release | `93,413,510` 字节，SHA-256 `C69688718250F5534274BB46ADD5B48937341124567CF0F40FEF4BCA35F10B05`，`NotSigned`；已发布 [v1.0.63](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.63)，标签与 Release 目标为功能提交 `9229603db13804a10e8bbd939b6ddf5e033b8c89`，GitHub digest、独立回下载、本地候选和两个 `dist` EXE 的大小及 SHA-256 完全一致 |

本机只有一块 `2560×1440` 显示器，因此多屏、负坐标副屏和混合 DPI 结论来自自动化矩阵，不冒充本轮真实双屏硬件验收；实机冒烟覆盖最终用户路径启动、许愿星完整预览、窗口工具样式和用户数据不变。

## v1.0.62 发布验证

以下结果于 `2026-08-09` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换到用户指定路径并保持运行，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码与版本 | 正式功能提交 `756e3734e03bb446dad5e23ae4e6359c19d26847`；文件版本 `1.0.62.0`，产品版本 `1.0.62+756e3734e03bb446dad5e23ae4e6359c19d26847` |
| 五种普通动作 | 运行时点击循环严格为 `butterfly / cry / cute / like / eat`，自动袋为这五种动作加一次待机；旧 yawn 文案、`ActionName`、图集页、RID 程序集和最终 EXE 命中均为 0 |
| 蝴蝶与 Todo | 蝴蝶为一张 `96×96` 透明 Resource，人物复用 56 张 `think` 高清帧；实机生产预览完整循环确认蝴蝶从耳机外侧绕入、在鼻尖停留扇翅并独立从左下方飞出；Todo 仍保持 214 帧完整入场、`think-smooth-056` 最终姿势和中途严格反向续播 |
| 图集与素材 QA | 最终清单为 41 页、1240 个源帧和 1240 个分页帧；稠密素材 QA 和图集 QA 零失败，Python 测试 31/31 通过；`yawn`、`loop-cute`、`cute-smooth-057..090` 和全身 butterfly 图集页均为 0 |
| 内存 | 实测预算为常规 `49/52 MiB`、巡游 `89/92 MiB`；`--memory-profile` 的空闲收缩后为 1 页 / `11 MiB` resident、`16.06 MiB` managed、`139.05 MiB` private，活跃普通动作为 `46 MiB` resident，活跃巡游为 `82 MiB` resident；不降低人物分辨率或帧率 |
| 自动化与实机 | Release 构建 0 警告、0 错误，完整 `UiStateChecks` 通过；绝对时钟覆盖 59/60/120/144 Hz 与 250 ms 阻塞。`F:\agent\pet\dist\LubanDesktopPet.exe` 启动后响应，二次启动自行退出且始终只有一个实例；3 个用户数据文件哈希不变 |
| EXE | `93,412,486` 字节，SHA-256 `3114D92B8A9F86511056D421D721218D4673E93D303EEA6F6C01FDF542584E99`，`NotSigned` |
| GitHub Release | 已发布 [v1.0.62](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.62)；标签 peeled 提交与 GitHub `main` 均为 `756e3734e03bb446dad5e23ae4e6359c19d26847`，GitHub digest、独立回下载、本地候选和用户路径 EXE 的大小及 SHA-256 完全一致 |

本机只有一块 `2560×1440` 显示器，因此多屏、负坐标副屏和混合 DPI 结论来自自动化矩阵，不冒充本轮真实双屏硬件验收；实机冒烟覆盖最终用户路径启动、单实例、完整蝴蝶动画预览和用户数据不变。

## v1.0.61 发布验证

以下结果于 `2026-08-09` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换到用户指定路径并保持运行，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码与版本 | 正式功能提交 `44013c36851a4282cd9e7d0e4afb7ae81a526095`；文件版本 `1.0.61.0`，产品版本 `1.0.61+44013c36851a4282cd9e7d0e4afb7ae81a526095` |
| 五种普通动作 | 运行时点击循环严格为 `yawn / cry / cute / like / eat`，自动袋为这五种动作加一次待机；普通 `think`、普通 `wave` 及两句已移除对白在正式 RID 程序集和 EXE 中均为 0 命中 |
| 右键 Todo 思考 | 保留 214 帧 `idle → wake → think` 入场、56 帧 `think` smooth、稳定 `smooth-056` 托腮姿势和严格反向退场；自动化与实机右键截图均确认任务小屋正常打开并保持该姿势 |
| 图集与素材 QA | 最终清单为 47 页、1454 个源帧和 1454 个分页帧；`action-think` 为 2 页、56 个唯一资源，`loop-think` 和普通 `action-wave / loop-wave` 均为 0；稠密素材 QA 与图集 QA 零失败，Python 测试 24/24 通过 |
| 打工三边交接 | `--work-mode-only`、`--edge-dock-only` 和完整 `UiStateChecks` 全部通过；左/右/下保持打工绝对相位并平滑收工，再进入真实 `EdgePeek`，冷页不会闪出待机中间帧，顶部和共享接缝不触发 |
| 其他自动化 | `--clip-clock-only`、`--todo-only`、`--memory-profile` 与完整 UI 套件通过；绝对时钟覆盖 59/60/120/144 Hz，Todo/设置/定时任务存储检查通过，Release 构建为 0 警告、0 错误 |
| EXE、实机与用户数据 | `113,246,342` 字节，SHA-256 `28B154165D3A070C799A9D364CBA0FA628B69F578A2008CC842E588672D09E97`，`NotSigned`；`F:\agent\pet\dist\LubanDesktopPet.exe` 启动后响应，二次启动自行退出且始终只有一个实例；实机完成右键 Todo 思考、面板收起和萌太阳/月亮打工往返，3 个用户数据文件哈希不变 |
| GitHub Release | 已发布 [v1.0.61](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.61)，标签与 Release 目标均为 `44013c36851a4282cd9e7d0e4afb7ae81a526095`；GitHub digest、独立回下载、本地候选和用户路径 EXE 的大小及 SHA-256 完全一致 |

本机只有一块 `2560×1440` 显示器，因此多屏、负坐标副屏和混合 DPI 的结论来自自动化矩阵，不冒充本轮真实双屏硬件验收；本轮实机冒烟覆盖启动、单实例、右键 Todo 专用思考、面板收起和打工入口往返，三边拖拽交接由真实 WPF 状态路径测试覆盖。

## 10. v1.0.60 发布验证

以下结果于 `2026-08-09` 在 Windows 11 Pro Insider Preview x64（`10.0.26220`）、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换到用户指定路径并保持运行，Release 附件也已在独立目录重新下载复核。

| 项目 | 实际结果 |
| --- | --- |
| 源码与版本 | 正式功能提交 `543e4df031971e9ba664cb016cab8e3378e9f60e`；文件版本 `1.0.60.0`，产品版本 `1.0.60+543e4df031971e9ba664cb016cab8e3378e9f60e` |
| 六种普通动作 | 运行时严格为 `yawn / cry / cute / like / eat / think`；点击袋为六种动作加一次待机，“嗨～我在这里！”普通挥手入口与片段均已删除 |
| 挥手资源边界 | `pic/小鲁班8.png` 保留，SHA-256 `F84E2CFDE288BB3C56F6B59A20FADB44927FC8F481ADE6F76535340F862569A5`；`Assets`、清单、RID 程序集和最终 EXE 的普通 `action-wave / loop-wave / luban-wave-*` 命中均为 0；可选熊猫 `roam-wave` 契约保留 |
| 图集与素材 QA | `qa_dense_motion_assets.py --require-edge-peek --contacts` 零失败；`qa_sprite_atlas_motion.py --contacts` 零失败；实际清单为 48 页、1502 个源帧和 1502 个分页帧；Python 测试 22/22 通过 |
| 打工三边交接 | `--work-mode-only`、`--edge-dock-only` 和完整 `UiStateChecks` 全部通过；左/右/下会保持打工绝对相位、平滑收工，再进入真实 `EdgePeek`。冷页会保持收工末帧直至目标页就绪，解码失败则安全回到待机，不闪趴枕头中间帧；顶部和共享接缝不触发 |
| 其他回归 | 完整 UI 套件覆盖待办、定时任务、提醒、巡游、普通三边探头、桌宠大小、100%/125%/150% DPI 与负坐标几何；`TodoStoreChecks` 通过 |
| EXE 与进程冒烟 | `115,999,878` 字节，SHA-256 `FE47C10D528174E886316659A41087F93522EF51B25E4FEEA05E1E9FBA3B3EBF`，`NotSigned`；`F:\agent\pet\dist\LubanDesktopPet.exe` 启动后响应，UI Automation 实际完成“去打工 → 去睡觉 → 去打工”切换，二次启动自行退出且只有一个实例；替换时 3 个用户数据文件哈希不变 |
| GitHub Release | 已发布 [v1.0.60](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.60)，目标提交 `543e4df031971e9ba664cb016cab8e3378e9f60e`；GitHub 附件与独立回下载均为 `115,999,878` 字节，SHA-256 完全一致 |

本机只有一块 `2560×1440` 显示器，因此多屏、负坐标副屏和混合 DPI 的结论来自自动化矩阵，不冒充本轮真实双屏硬件验收；候选 EXE 的实机冒烟覆盖了启动、单实例和打工入口往返，三边拖拽交接由真实 WPF 状态路径测试覆盖。

## 11. v1.0.59 发布验证

以下结果于 `2026-08-08` 在 Windows 11 x64、.NET SDK `8.0.418`、.NET Desktop Runtime `8.0.24` 环境完成。正式 EXE 已替换本地 v1.0.58 并保持运行，Release 附件也已在独立目录重新下载复核。仓库记录的 `8.0.29` 安装包版本仍可作为更新的 .NET 8 Desktop Runtime 使用，但不是本轮实机环境。

| 项目 | 结果 |
| --- | --- |
| 源码版本 | `v1.0.59`；Release 源码提交 `b3ece2111517de2cf9ff2dfda3c6a2c20a7ceffd` |
| Release 构建 | 主项目与测试项目均通过，`0` 警告、`0` 错误；框架依赖 win-x64 单文件发布成功 |
| 自动回归 | 完整 `UiStateChecks` 通过；`TodoStoreChecks` 通过；Python 单元测试 `17/17`；源 PNG QA 与最终图集 QA 均为失败数 `0` |
| 图集与旧版贴边 | 最终图集 `53` 页、`1648` 个源帧/分页帧；打工 `9` 页、`264` 帧且无 `work-tap`；左右侧 `52` 张源 PNG 与 v1.0.57 提交 `66fdbd2...` 逐文件一致，侧边页解码 SHA-256 为 `0B8E23EEE30F63742B6EC5814CE392E3E3EC1CB8508F3EBEC2CACB8FFE5E13FD` |
| 贴边与入口实机 | 实际打开任务面板后拖到左边缘，面板立即关闭并进入旧版休息姿势；再拖到右边缘，镜像方向正确；待机太阳与打工月亮均已实机截图检查，无文字胶囊，打工贴边仍保留月亮退出入口 |
| 托盘实机 | “退出小鲁班”完整左对齐；连续 `20` 轮单次右键均只出现一个菜单，点击其他窗口后 `300 ms` 内均关闭；另 `10` 轮单次右键后按 `Esc` 均关闭。当前机器只有一个 `2560×1440` 屏幕，跨屏异 DPI 只通过自动契约，未写成实机已验证 |
| EXE 与进程冒烟 | `128,385,670` 字节，文件版本 `1.0.59.0`，产品版本包含 `b3ece21...`，SHA-256 `FC90C53938E277B3337E4DE523489EC4C142DB09EB7D0FAB355E3DF75131074B`，`NotSigned`；启动后响应，二次启动自行退出且只有一个实例；替换前后待办、定时任务和设置文件 SHA-256 完全一致 |
| GitHub Release | 已发布 [v1.0.59](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.59)，目标提交 `b3ece2111517de2cf9ff2dfda3c6a2c20a7ceffd`；GitHub 记录与独立回下载均为 `128,385,670` 字节，SHA-256 完全一致 |

## 12. v1.0.58 发布验证

以下结果于 `2026-08-08` 在 Windows 11 x64、.NET SDK 8、.NET Desktop Runtime `8.0.29` 环境完成。正式 EXE 已替换本地 v1.0.57 并启动，Release 附件也已在独立目录重新下载复核。

| 项目 | 结果 |
| --- | --- |
| 源码版本 | `v1.0.58`；Release 源码提交 `9ce5faa6561f9cdb9e10c1bf48ad7045c28935f8` |
| Release 构建 | 主项目与测试项目均通过，`0` 警告、`0` 错误；框架依赖 win-x64 单文件发布成功 |
| 自动回归 | `TodoStoreChecks` 通过；Python 契约 `19/19`；打工生成 QA、源 PNG 连续性 QA 与最终图集 QA 均为失败数 `0`；恢复全部非 tap 动态覆盖后的完整 `UiStateChecks` 通过 |
| 图集与交互 | 最终图集 `53` 页、`1648` 帧；打工 `9` 页、`264` 帧且无 `work-tap`；单击冷页不抢占，双击认真、frame 094 跨页、进入/退出/中断均通过；左右立即探头，下边缘保留原休息节奏 |
| 待办行为 | 真实勾选事件链验证中间完成项只移动到末尾一次、其他项相对顺序不变并立即持久化；取消完成保留当前位置，手动拖拽顺序仍有效 |
| EXE 与进程冒烟 | `128,479,878` 字节，文件版本 `1.0.58.0`，产品版本包含 `9ce5faa...`，SHA-256 `E54FF1CD78D197462D355F0635D4C9C25A6390CD2D12856FE0B771D8DA7DAD00`，`NotSigned`；启动后响应，二次启动自行退出且只有一个实例；替换前后待办、定时任务和设置文件 SHA-256 完全一致 |
| GitHub Release | 已发布 [v1.0.58](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.58)，目标提交 `9ce5faa6561f9cdb9e10c1bf48ad7045c28935f8`；GitHub 记录与独立回下载均为 `128,479,878` 字节，SHA-256 完全一致 |

## 13. v1.0.57 发布验证

以下结果于 `2026-08-07` 在 Windows 11 x64、.NET SDK 8、.NET Desktop Runtime `8.0.29` 环境完成。正式 EXE 已替换本地旧版并启动，Release 附件也已在独立目录重新下载复核。

| 项目 | 结果 |
| --- | --- |
| 源码版本 | `v1.0.57`；Release 源码提交 `66fdbd23758009f83fa55c9f3fd319a2e75c45fe` |
| Release 构建 | 主项目与测试项目均通过，`0` 警告、`0` 错误；框架依赖 win-x64 单文件发布成功 |
| 自动回归 | `TodoStoreChecks` 通过；Python 契约 `15/15`；`--todo-only`、`--edge-dock-only`、`--startup-only` 均通过；最终完整 `UiStateChecks` 为 `49/49` |
| 待办 Computer Use 实机 | 在独立预览数据中实际勾选第二条待办；该项立即离开第二行，滚动到底部后以已完成状态显示在最后一行；正式 `todos.json`、`scheduled-tasks.json`、`settings.json` 均按备份 SHA-256 原样恢复 |
| 边缘与托盘定位 | 48 张侧边帧保留 `DestinationX=-2`，左边缘及生产像素镜像后的右边缘逐帧均有至少 `40` 个有效 Alpha 接触像素；托盘契约验证 Win32 光标、通知图标矩形回退、`RelativePoint` 与物理坐标/DIP 往返，真实多屏异 DPI 位置仍按第 8 节人工验收 |
| EXE 与进程冒烟 | `128,914,054` 字节，文件版本 `1.0.57.0`，产品版本包含 `66fdbd2...`，SHA-256 `E4420504F3A0D3B47848717B1431630FD936E3062744A95AE5B62748F3A3CA19`，`NotSigned`；本地正式路径启动后响应，二次启动退出且仅保留一个实例 |
| GitHub Release | 已发布 [v1.0.57](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.57)，目标提交 `66fdbd23758009f83fa55c9f3fd319a2e75c45fe`；附件与独立回下载均为 `128,914,054` 字节，SHA-256 完全一致 |

## 14. v1.0.56 发布验证

以下结果于 `2026-08-07` 在 Windows 11 x64、.NET SDK 8、.NET Desktop Runtime `8.0.29` 环境完成。正式 EXE 已替换本地旧版并启动，Release 附件也已在独立目录重新下载复核。

| 项目 | 结果 |
| --- | --- |
| 源码版本 | `v1.0.56`；正式源码提交 `41669694ccae89944502b97d943d1c0ba768eb5c` |
| Release 构建 | 主项目通过，`0` 警告、`0` 错误；框架依赖 win-x64 单文件发布成功 |
| 回归测试 | `TodoStoreChecks` 通过；Python 动画 QA `15/15` 通过；`--work-mode-only` 为 `11/11`；完整 `UiStateChecks` 为 `48/48`；图集运动 QA `passed=true`、失败数 `0` |
| 打工实机交互 | 真实 WPF 预览稳定进入 `Typing / work-loop / 1x`，按钮内容与无障碍名称均显示“去睡觉”；待机普通吸附仍隐藏“去打工” |
| EXE 与进程冒烟 | `128,913,542` 字节，文件版本 `1.0.56.0`，产品版本包含 `41669694...`，SHA-256 `BEC0C51A1D98DA377AF08921F2814340356AF7C04A9979B032230B856CD119DE`；替换本地 v1.0.55 后启动并持续响应 |
| GitHub Release | 已发布 [v1.0.56](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.56)，目标提交 `41669694ccae89944502b97d943d1c0ba768eb5c`；附件与独立回下载均为 `128,913,542` 字节，SHA-256 完全一致 |

## 15. v1.0.55 发布验证

以下结果于 `2026-08-06` 在 Windows 11 x64、.NET SDK 8、.NET Desktop Runtime `8.0.29` 环境重新执行。Release 指向正式源码提交，附件已从 GitHub 独立回下载复核。

| 项目 | 结果 |
| --- | --- |
| 源码版本 | `v1.0.55`；正式源码提交 `3b0ef48bca22c0bfa5afb96ec95753b0cee31fb7` |
| Release 构建 | 主项目通过，`0` 警告、`0` 错误；框架依赖 win-x64 单文件发布成功 |
| 存储与 Python 契约 | `TodoStoreChecks` 通过；Python 动画 QA `15/15` 通过 |
| `--work-mode-only` | `11/11` 通过；包含 65 张独特循环位图、9 个精确接缝、单/双击、打工拖动保持 state/clip/绝对相位/倍速、拖动不吸附，以及左/右/下吸附隐藏并禁用入口 |
| 完整 `UiStateChecks` | `48/48` 通过，最终输出 `UI state checks passed.`；普通分页需求 `61/64 MiB`，巡游分页需求 `101/104 MiB` |
| 图集运动 QA | `passed=true`、失败数 `0`；最终图集 `55` 页、`1696` 个逻辑帧；正式打工资源恢复为 `v1.0.53` 的 v5 `48/96/48/96/24` 契约 |
| 打工实机交互 | 真实 WPF 预览进入 `Typing / work-loop / 1x` 后拖动成功；松手并继续观察后仍保持同一工作循环和倍速，没有切回待机或触发点击反应 |
| EXE 与进程冒烟 | `128,913,030` 字节（`122.94 MiB`），文件版本 `1.0.55.0`，产品版本包含 `3b0ef48...`，SHA-256 `4BEA28D93DEBFA27CFD4273384EA82F233B3FFF39BFD0BB4D98E24ABC0FD5BBA`，`NotSigned`；30 秒持续响应，私有内存 `209.97–210.94 MiB`、工作集 `283.63–284.65 MiB`；二次启动自行退出且仅保留一个实例 |
| GitHub Release | 已发布 [v1.0.55](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.55)，目标提交 `3b0ef48bca22c0bfa5afb96ec95753b0cee31fb7`；附件与独立回下载均为 `128,913,030` 字节，SHA-256 均为 `4BEA28D93DEBFA27CFD4273384EA82F233B3FFF39BFD0BB4D98E24ABC0FD5BBA` |

## 16. v1.0.54 历史发布验证

以下结果于 `2026-08-05` 在 Windows 11 x64、.NET SDK 8、.NET Desktop Runtime `8.0.29` 环境完成。该版本的 v6 动画已经在 `v1.0.55` 按用户偏好回退，本节只保留历史发布事实。

| 项目 | 结果 |
| --- | --- |
| 源码版本 | `v1.0.54`；正式源码提交 `11800af0db9ef690c7a7987232aef6c090b2bfe3` |
| 打工生成 QA | v6 `48/96/48/96/24` 共 312 帧通过；普通/认真循环均为 81 张独特位图，8 个精确中性接缝，最长连续相同画面为 3/2 帧；10 个强触键事件的指尖位移为 `5.814–7.055 px`，肩 `<` 肘 `<` 腕 `<` 指尖，脸、电脑与双臂允许区域外漂移均为 `0 px`；30/59/60/120/144Hz 时钟与可见姿势覆盖通过 |
| 打工帧复建 | 312 张正式打工帧的逐帧聚合 SHA-256 为 `F2245F7F18D54137902458A37D53197C7F0CEAF91632C277F4CE76E1C8A837ED` |
| Brotli 图集 | 55 页、1696 个源帧、1696 个分页帧；最终图集运动 QA 失败数 `0`；清单 SHA-256 `BF2D60413132CABBC7E6C80715F6D454A54C4A1C61FD44B51A70CA899C3A933A` |
| Python 单元测试 | `15/15` 通过 |
| 存储契约 | `TodoStoreChecks`、`AppSettingsStore`、`ScheduledTaskStore` 全部通过 |
| WPF 状态契约 | 全量 `UiStateChecks` 通过；其中 `--work-mode-only` 为 `10/10`，普通分页预算 `64.00/64.00 MiB`、巡游分页预算 `101.00/104.00 MiB` |
| 原有边缘源素材检查 | 全局 dense-source QA 仍报告 `edge.left` 的绿边、低 Alpha 拖影与关键帧不匹配；该 QA 脚本和对应边缘源图与 `v1.0.53` 基线逐字节一致，本次未改动。最终运行图集 QA、边缘状态机及全量 UI 契约均通过 |
| EXE 发布与实机冒烟 | `130,228,870` 字节（`124.20 MiB`），文件版本 `1.0.54.0`，产品版本包含 `11800af0...`，SHA-256 `FCD5E1B2729929692A85DF6605C3A679B34FC349536C8C149F6549CB4370742A`，`NotSigned`；持续响应，私有内存约 `212.02 MiB`、工作集约 `296.17 MiB`；二次启动退出且仅保留一个实例 |
| GitHub Release | 已发布 [v1.0.54](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.54)，目标提交 `11800af0db9ef690c7a7987232aef6c090b2bfe3`；附件 `130,228,870` 字节，GitHub 摘要和独立回下载 SHA-256 均为 `FCD5E1B2729929692A85DF6605C3A679B34FC349536C8C149F6549CB4370742A` |

## 17. v1.0.53 历史发布验证

以下结果只保留 `v1.0.53` 的历史证据，不能作为 `v1.0.55` 的通过结果。

| 项目 | 结果 |
| --- | --- |
| 日期 | `2026-08-05` |
| 源码版本 | `v1.0.53` 工作树 |
| Release 构建 | 主项目通过，`0` 警告、`0` 错误；框架依赖单文件发布成功 |
| `TodoStoreChecks` | 通过；待办、设置与定时任务存储契约均通过，完整运行约 `2.5 s` |
| `--work-mode-only` | `10/10` 通过；覆盖 `48/96/48/96/24`、普通 1.6 秒、9 个精确接缝、65 张独特位图、最长 5 帧中性停顿、单击回 1 倍速、双击立即 2 倍速到接缝后约 133 ms 认真眉过渡、完整 4 秒认真状态、frame 094 冷页环回预取，以及认真状态单击先放松再敲头 |
| 完整 `UiStateChecks` | 全部通过，退出码 `0`，最终输出 `UI state checks passed.`；普通常驻页需求 `62/64 MiB`，巡游需求 `101/104 MiB` |
| 图集 Python 与解码 QA | `15/15` 单元测试通过；最终图集为 `55` 页、`1696` 个源帧和 `1696` 个重建帧；Pbgra32 解码运动 QA `passed=true`、失败数 `0` |
| 打工素材与接缝 | 312 张正式打工帧；两次复建的逐帧聚合 SHA-256 同为 `A708E41A7D50249744D6C46FDAF99FD4CDE04DB1D0352712D7204D8B95D17C70`；8 次不等间隔四指触键的指尖位移为 `5.528–6.049 px`，v5 语义手/袖口蒙版的电脑、键盘、肩部、躯干锁区漂移均为 `0 px`，全部声明接缝逐像素一致 |
| 打工实机预览 | 真实 WPF 预览中完成待机→普通打字→单击判定→双击认真过渡/2 倍速→下班→待机；状态与按钮同步，未见人物缩放、键盘拖动、袖口断裂、光纹或硬切表情 |
| EXE 发布与实机冒烟 | `128,913,030` 字节（`122.94 MiB`），文件版本 `1.0.53.0`，SHA-256 `D40E1275C9F6C0D5188678A7DC7FAC27DCC604B1C44C5B826EA9E7EE0ED4B04B`，`NotSigned`；40 秒持续响应，私有内存 `210.47–211.70 MiB`、工作集 `283.44–284.62 MiB`；二次启动后仍只有一个实例 |
| GitHub Release | 已发布 [v1.0.53](https://github.com/guoqihan342-svg/xlb-pet/releases/tag/v1.0.53)，目标提交 `a663e202306e20c56db1993681be5602ea980ee9`；附件 `128,913,030` 字节，GitHub 摘要与独立回下载 SHA-256 均为 `D40E1275C9F6C0D5188678A7DC7FAC27DCC604B1C44C5B826EA9E7EE0ED4B04B` |

### v1.0.52 历史验证记录

以下记录只用于保留更早版本证据，不能作为 `v1.0.55` 的通过结果。

| 项目 | 结果 |
| --- | --- |
| 日期 | `2026-08-05` |
| 源码版本 | `v1.0.52` 工作树 |
| Release 构建 | 主项目与 `UiStateChecks` 均通过，0 警告、0 错误 |
| `TodoStoreChecks` | `5` 组通过；覆盖待办、设置和定时任务存储契约，完整运行 `2.133 s` |
| `--work-mode-only` | `10/10` 通过；覆盖普通/认真各 48 帧全唯一循环、24 帧认真退出、单/双击、4 秒 2 倍速、59/60/120/144 Hz 采样、接缝、抢占、左/右/下边缘入口及按钮状态 |
| 完整 `UiStateChecks` | `47` 个具名检查全部通过，0 警告、0 错误，含构建完整运行 `94.796 s` |
| 图集 Python 与解码 QA | `11/11` 单元测试通过；最终图集为 53 页、1600 个源帧和 1600 个分页帧；Pbgra32 解码运动 QA 失败数 `0` |
| 打工素材与接缝 | 216 张正式运行帧及 44 个生成源/QA 文件复建后新增、删除、哈希变化均为 `0`；四根目标手指峰值变化 `355–468 px`，脸、头、肩、躯干、电脑和键盘锁区漂移 `0 px`，所有状态接缝逐像素相等 |
| 打工实机预览 | 真实 WPF 窗口分别观察普通 1 倍速与认真 2 倍速；四根手指落键姿势可辨，袖口、掌根和键盘稳定，未见整手溶解、双描边、光纹或残影 |
| EXE 发布与实机冒烟 | 已生成框架依赖单文件 EXE，`128,237,702` 字节（`122.30 MiB`），文件版本 `1.0.52.0`，SHA-256 `E950B658C1892B60E83DA3373E237C25A439E7DF4899E963B8853C1F834D3672`，未签名；40 秒持续响应，私有内存 `215.0–216.7 MiB`、工作集 `290.5–291.7 MiB`，第二次启动退出且仅保留一个实例 |
| GitHub Release | 本轮未上传；工作树源码版本不代表 GitHub Release 已更新 |

上述结果验证了 `v1.0.52` 源码、图集契约和本地发布文件；它仍不代表 GitHub Release 已更新。对外分发前还应按第 8 节完成附件上传、重新下载和哈希复核。
