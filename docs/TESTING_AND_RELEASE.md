# v1.0.67 测试与发布

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
| `--todo-layout-only` | 待办布局、主题、输入法、修改、拖拽、悬停像素和滚动 |
| `--todo-cut-only` | `Ctrl+X`、剪贴板占用和 IME 键转换 |
| `--todo-only` | Owned Window、箭头、多屏、待办完成自动移底、取消完成保留位置、拖拽排序、定时选项卡，以及免打扰尾部提示在固定面板和最小宽度修改窗中的完整排版 |
| `--todo-arrow-only` | 气泡箭头在多屏/DPI/换边时指向人物 |
| `--scheduled-editor-only` | 定时任务新增与修改组件 |
| `--reminder-only` | 提醒堆叠、分页、关闭语义和免打扰运行时 |
| `--startup-only` | 当前用户开机自启、原生托盘、Dispatcher 合并打开、前台所有权、外点关闭、通知区焦点归还、显式锚点和物理坐标/DIP 往返契约 |
| `--alt-tab-only` | 六个正式 WPF 窗口的真实 HWND 扩展样式；必须含 `WS_EX_TOOLWINDOW`、不含 `WS_EX_APPWINDOW`/`WS_EX_NOACTIVATE` 且不进入 Alt+Tab 候选集合，同时保持输入与焦点能力 |
| `--edge-dock-only` | `12 DIP` 磁吸、快速越界、顶部可拖达但不吸附、左/右/下 `rest-first` 吸附、48 帧侧边支撑手臂像素接触、完整短弯前臂，以及待办/定时面板自动关闭与迟到回调防重开 |
| `--pet-drag-preview` | 显示可由 Computer Use 识别的真实 WPF 拖动窗口；标题实时给出人物可见顶边、工作区顶边和物理像素误差 |
| `--roam-source-only` | 绕屏状态机、路线、朝向、原地退场、延迟打开待办、负坐标副屏和资源契约 |
| `--roam-interaction-only` | 真实 WPF 状态下的巡游左键退场、禁止追加点击动作、右键退场后只打开一次默认待办页 |
| `--deadline-only` | 1 分钟原地动作、10 分钟巡游和 20 秒忙碌重试截止 |
| `--pet-size-only` | 尺寸滑块手势、连续缩放和待办布局 |
| `--reaction-random-only` | 许愿星资源彻底退役、四个保留动作、用户点击随机且不连续重复、失败不提交历史，以及自动洗牌袋独立 |
| `--clip-clock-only` | 单缓冲预乘 Alpha、冷页时钟和四种普通动作的绝对时间轴 |
| `--work-mode-only` | `48/96/96/24` 序列、普通 1.6 秒、65 张独特循环位图、9 个精确中性接缝、单击严格无操作、双击认真表情、太阳/月亮 420 ms 绝对时钟双向切换，以及打工拖动命中左/右/下时跳过普通退出与 idle 的热页/冷页原子交接 |
| `--resident-cache-only` | 仅待机页常驻、`52/92/12 MiB` 预算、`8 MiB` LOH 门槛、分页预热、淘汰、迟到结果和退出清理 |
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
| `--roam-preview` | 按 `Space` 准备一轮真实登乘中段姿势；用于观察左键退场、拖动接管和右键退场后打开待办，预览不会自动重启 |
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
- 打工从 Entering、普通/认真打字、认真进出或 Exiting 拖到左、右、下边缘时，热页和冷页 descriptor 序列都只能是“当前工作帧 → 目标 edge-rest”；不得播放普通 `work-exit`，也不得出现 idle 页、枕头、待机帧、尺寸包络或锚点跳变。目标页失败才回退 idle，顶部和未命中拖放保持工作状态。
- 主桌宠、任务、提醒、确认和两个编辑窗口的真实 HWND 均含 `WS_EX_TOOLWINDOW`、清除 `WS_EX_APPWINDOW`、不设置 `WS_EX_NOACTIVATE` 且 `ShowInTaskbar=false`；人工按 `Alt+Tab` 时不得出现透明框或任何辅助窗口，输入法、焦点和编辑仍须正常。
- 完全待机时可点击人物视觉左上角的萌太阳进入电脑场景，进入后用 420 ms 绝对时间轴原位交叉切换为萌月亮；退出请求必须在一个刷新周期内开始反向，中途反转从当前混合状态连续折返，总透明度保持 `1.00–1.05`，59/60/120/144 Hz 同一绝对时间结果一致，250 ms UI 阻塞后直接定位且不补播。人物镜像时图标仍保持屏幕左上和正向，不得出现文字胶囊。普通 96 帧循环必须在 1.6 秒内显示 8 次不等间隔四指落键；工作中单击严格保持活动帧、相位、倍速与认真期限，双击立即连续切到 2 倍速并完整保持至少 4 秒认真状态；点击月亮后平滑回到稳定待机。
- 左、右、下普通边缘探头时太阳必须隐藏、禁用且不可命中；三边从末尾休息姿势进入。侧边下手必须保持边缘接触，紫袖为短而圆的上扬弧线，不能出现横向长管、平切底边、重手、黑边或光纹；右侧必须是左侧精确镜像，底边素材保持不变，顶部仍不吸附。任务面板打开时拖入任一受支持边缘，待办/定时页面必须立即关闭且不得被迟到回调重新打开。
- 侧边素材必须在最终 `190×242 DIP` 真实 WPF 窗口中复核，而不是只看 `450×550` 源图：分别以 75%（`0.75`）和 140%（`1.40`）桌宠大小检查左、右浅探与深探。每种组合都要看见下手后的完整短弯前臂，深探时下半部分不得显得被裁掉；右侧镜像和 Bottom 不变需同时确认。
- 打工期间不启动自动动作、呼噜或绕屏；拖动过程应继续当前动画与绝对相位。松手命中左、右或下外边缘时，必须锁定目标边缘并完全跳过普通 `work-exit`、枕头和 idle：热页在同一渲染提交中从当前工作描述符原子切到 edge-rest，冷页冻结当前工作描述符直至 edge-rest resident；目标页失败才允许安全回到 idle。顶部及双屏内部接缝不触发边缘交接，右键和定时提醒接管时不得留下迟到请求。
- 右键打开和收起面板、快速拖动大小滑块时不闪帧、不抖动、不改变人物比例。
- 待办新增、`Ctrl+C / Ctrl+X / Ctrl+V`、F2 修改、拖拽排序和长文本全文窗正常；勾选中间项后该项移至末尾、其他项顺序不变，取消完成不自动上移，重启后顺序保持；普通/完成项悬停都应显示浅蓝填充与完整蓝色圆角框，相邻行不得出现上下横线。
- 定时任务日期/秒级时间、循环、免打扰、修改、每页 5 条提醒和确认语义正常；“可跨夜”在固定任务面板和最小宽度修改窗中均完整显示，无裁剪或省略。
- 左、右、下边缘探头正常；在 75%、100%、125%、140% 大小下，人物可见像素可在一个物理像素内拖到工作区顶沿，但顶部中央不吸附、不探头。
- 默认熊猫巡游可以被点击、拖动、右键和提醒抢占；左键不追加卖萌动作，拖动立即接管，右键只打开一次默认待办页，退出时无回跳、翻转或闪帧，竖边方向旋转正确。
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
