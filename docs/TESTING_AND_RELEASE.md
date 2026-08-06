# v1.0.55 测试与发布

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
| `--todo-only` | Owned Window、箭头、多屏、待办、排序和定时选项卡 |
| `--todo-arrow-only` | 气泡箭头在多屏/DPI/换边时指向人物 |
| `--scheduled-editor-only` | 定时任务新增与修改组件 |
| `--reminder-only` | 提醒堆叠、分页、关闭语义和免打扰运行时 |
| `--startup-only` | 当前用户开机自启和原生托盘 |
| `--edge-dock-only` | `12 DIP` 磁吸、快速越界、顶部可拖达但不吸附、左/右/下吸附和面板收起 |
| `--pet-drag-preview` | 显示可由 Computer Use 识别的真实 WPF 拖动窗口；标题实时给出人物可见顶边、工作区顶边和物理像素误差 |
| `--roam-source-only` | 绕屏状态机、路线、朝向、原地退场、延迟打开待办、负坐标副屏和资源契约 |
| `--roam-interaction-only` | 真实 WPF 状态下的巡游左键退场、禁止追加点击动作、右键退场后只打开一次默认待办页 |
| `--deadline-only` | 1 分钟原地动作、10 分钟巡游和 20 秒忙碌重试截止 |
| `--pet-size-only` | 尺寸滑块手势、连续缩放和待办布局 |
| `--clip-clock-only` | 单缓冲预乘 Alpha、冷页时钟和绝对时间轴 |
| `--work-mode-only` | `48/96/48/96/24` 序列、普通 1.6 秒、65 张独特循环位图、9 个精确中性接缝、单击/双击、认真退出、打工拖动保持相位与倍速、拖动不吸附、吸附态隐藏入口及交互抢占 |
| `--resident-cache-only` | 分页预热、淘汰、空闲收缩、迟到结果和退出清理 |
| `--atlas-hash-only` | 图集页上限、Brotli payload 和像素哈希失败关闭 |
| `--memory-profile` | 本地内存剖面输出；不是普通 pass/fail 快速测试 |

### 人工预览入口

以下入口会打开可交互测试窗口，应由测试者手动关闭：

| 参数 | 预览内容 |
| --- | --- |
| `--todo-preview` | 多行待办、长文本、完成项、悬停和滚动条 |
| `--picker-preview` | 日期与时分秒选择器 |
| `--delete-style-preview` | 蓝色待办删除和橘色定时删除确认窗 |
| `--pet-size-preview` | 桌宠大小滑块与人物连续缩放 |
| `--roam-preview` | 按 `Space` 准备一轮真实登乘中段姿势；用于观察左键退场、拖动接管和右键退场后打开待办，预览不会自动重启 |
| `--reminder-close-preview` | 提醒关闭和分页交互 |
| `--work-preview` | 头顶胶囊按钮、v5 语义手/袖口关节运动、8 次不等间隔四指落键、单击/双击、打工中拖动保持动画、吸附态隐藏入口与收工 |

预览窗口只用于人工观察，不替代自动断言。

## 4. 动画素材 QA

仅在修改动画或图集时运行：

```powershell
# 重新生成侧边帧后，确定性显露下方支撑手臂
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

详细生成顺序和不变量见 [动画与图集管线](ANIMATION_PIPELINE.md)。

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

不要从 README 或旧测试复制固定帧数。清单、实际分页、嵌入资源和源集指纹必须来自同一次构建。

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
- 七种点击动作从待机连续起身并返回；稳定待机只有一个半透明泡泡。
- 完全待机时可通过“去打工”进入电脑场景。普通 96 帧循环必须在 1.6 秒内显示 8 次不等间隔四指落键，最长中性停顿不超过 5 帧；手指、指节、掌根、手腕和袖口自然跟随，专用眉形补丁之外的脸、头、躯干、电脑及非目标键盘区域不得漂移。工作中单击只播敲头并回普通 1 倍速；双击不敲头，必须立即连续切到 2 倍速，在最近精确中性接缝经约 133 ms 认真眉过渡后再完整保持至少 4 秒；“下班啦”在收工完成前保持正确状态，随后回到稳定待机。
- 左、右、下边缘探头时“去打工”必须隐藏、禁用且不可命中；侧边下方支撑手臂不得被边界截断，顶部仍不吸附。
- 打工期间不启动自动动作、呼噜或绕屏；拖动应继续当前动画与绝对相位，松手不吸附且不遗留延迟单击；右键和定时提醒接管时不闪待机中间帧。
- 右键打开和收起面板、快速拖动大小滑块时不闪帧、不抖动、不改变人物比例。
- 待办新增、`Ctrl+C / Ctrl+X / Ctrl+V`、F2 修改、拖拽排序和长文本全文窗正常；普通/完成项悬停都应显示浅蓝填充与完整蓝色圆角框，相邻行不得出现上下横线。
- 定时任务日期/秒级时间、循环、免打扰、修改、每页 5 条提醒和确认语义正常。
- 左、右、下边缘探头正常；在 75%、100%、125%、140% 大小下，人物可见像素可在一个物理像素内拖到工作区顶沿，但顶部中央不吸附、不探头。
- 默认熊猫巡游可以被点击、拖动、右键和提醒抢占；左键不追加卖萌动作，拖动立即接管，右键只打开一次默认待办页，退出时无回跳、翻转或闪帧，竖边方向旋转正确。
- 负坐标副屏、100%/125%/150% DPI、任务栏避让和显示器热插拔正常。
- 开机自启启用/关闭与旧路径修复正常，最终恢复测试前的真实注册表状态。
- 托盘“退出小鲁班”保存数据并移除图标；运行前后都不生成 `log/` 文件夹。

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

## 10. v1.0.55 发布验证

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

## 11. v1.0.54 历史发布验证

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

## 12. v1.0.53 历史发布验证

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
