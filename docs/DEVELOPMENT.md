# 开发说明

本文说明如何在不误伤素材和本地数据的前提下开发小鲁班桌面宠物。运行时状态所有权见[架构与状态机](ARCHITECTURE.md)，测试与发布命令见[测试与发布](TESTING_AND_RELEASE.md)，动画素材细节见[动画与图集管线](ANIMATION_PIPELINE.md)。

## 1. 技术栈与环境

- Windows 11 x64。
- .NET 8 SDK，项目目标为 `net8.0-windows`。
- WPF `WinExe`，Per-Monitor V2 DPI 感知。
- PowerShell 示例均假设当前目录为仓库根目录。
- Python 只在重新生成或检查动画素材时需要，普通 C#/XAML 修改不需要 Python。

当前仓库没有 `global.json`、Python 锁文件或一键环境安装脚本。因此文档只记录已验证边界，不把本机环境写成仓库级锁定版本。

## 2. 源码结构

| 路径 | 作用 |
| --- | --- |
| `MainWindow.xaml(.cs)` | 桌宠窗口、动作状态机、绝对时间轴、巡游、边缘探头、提醒调度和图集缓存 |
| `TodoWindow.xaml(.cs)` | 待办/定时任务面板、输入法、编辑、拖拽排序和设置控件 |
| `ReminderWindow.xaml(.cs)` | 每页最多 5 条的提醒窗口 |
| `TodoStore.cs` | 待办 JSON 持久化 |
| `ScheduledTaskStore.cs`、`ScheduledRepeatRule.cs`、`ScheduledQuietHours.cs` | 定时任务、循环和免打扰持久化/计算 |
| `AppSettingsStore.cs` | 绕屏开关和桌宠大小 |
| `MonitorWorkArea.cs`、`OwnedWindowPositioner.cs` | 多屏、DPI 和窗口位置 |
| `StartupRegistration.cs` | 当前用户级开机自启 |
| `TrayIconService.cs` | 原生通知区域图标和退出菜单 |
| `Assets/` | 已验收的运行时素材、图集清单和 Brotli 分页 |
| `pic/` | 用户提供的原始参考图，只读边界 |
| `tools/` | 素材安装、补帧、图集构建和 QA 工具 |
| `tests/UiStateChecks/` | WPF 状态、动画、内存、输入、提醒和多屏契约 |
| `tests/TodoStoreChecks/` | 待办、设置、循环任务和免打扰存储契约 |

## 3. 日常开发流程

### 还原、运行和构建

```powershell
dotnet restore .\DesktopPet.csproj
dotnet run --project .\DesktopPet.csproj
dotnet build .\DesktopPet.csproj -c Release
```

普通代码或 XAML 修改直接使用仓库中已经验收的 `Assets/`。不要把“重新生成全部动画”当成每次构建的前置步骤。

### 建议的最小验证

```powershell
dotnet build .\tests\UiStateChecks\UiStateChecks.csproj -c Release
dotnet run --project .\tests\TodoStoreChecks\TodoStoreChecks.csproj -c Release
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj -c Release --no-build -- --todo-layout-only
```

根据改动范围选择更多定向入口，最终合并或发布前再运行完整 UI 套件。完整矩阵见 [测试与发布](TESTING_AND_RELEASE.md)。

## 4. 本地数据和测试隔离

正式程序默认读写：

```text
%LocalAppData%\LubanDesktopPet\todos.json
%LocalAppData%\LubanDesktopPet\scheduled-tasks.json
%LocalAppData%\LubanDesktopPet\settings.json
```

自动化测试会使用 `%TEMP%` 下的随机目录，不应覆盖真实用户数据。开发预览入口同样应显式注入临时 Store，不能把测试任务写入正式目录。

当前生产代码不包含文件日志。新增诊断时不要在渲染回调内同步读写磁盘；如果未来重新引入日志，需要先更新 UI/性能契约和用户隐私说明。

## 5. 可选 Python 素材环境

工具脚本实际使用以下第三方包：

- `Pillow`
- `numpy`
- `opencv-python`（导入名 `cv2`）
- `brotli`

仓库尚未提供锁定版本的 `requirements.txt`。需要运行素材工具时，可在独立虚拟环境安装依赖：

```powershell
python -m venv .\.venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install Pillow numpy opencv-python brotli
```

不要把 `.venv/`、下载工具或大型中间帧提交到 Git。

### RIFE 可选依赖

普通动作的密集补帧需要外部 `rife-ncnn-vulkan.exe` 和 `rife-anime` 模型目录。它们不在仓库中，也不是运行桌宠所需依赖。

```powershell
$env:XLB_RIFE_ROOT = 'C:\path\to\rife-ncnn-vulkan-20221029-windows'
$env:XLB_RIFE_JOBS = '1:1:1'  # load:proc:save，均为正整数
python .\tools\generate_dense_motion_assets.py --wake --actions --loops --edge-peek --reminder
```

运行前确认显卡/Vulkan 和所选第三方工具的许可证、来源与系统兼容性。文档不授权绕过驱动或 Windows 安全提示。

## 6. 工具脚本

| 脚本 | 用途 |
| --- | --- |
| `tools/install_generated_motion_assets.py` | 将验收后的创作源安装到 `Assets/` |
| `tools/generate_dense_motion_assets.py` | 生成起身、动作、循环、边缘和提醒密集帧 |
| `tools/build_roam_flight_assets.py` | 生成熊猫登乘和巡游运行素材 |
| `tools/build_work_animation.py` | 从 v5 中性手、键盘底图和四指参考确定性生成 `48/96/96/24` 四阶段打工序列及 QA |
| `tools/build_sprite_atlas.py` | 构建 Brotli Pbgra32 分页图集和清单 |
| `tools/qa_dense_motion_assets.py` | 检查源 PNG 连续性、透明通道、接触点和轮廓 |
| `tools/qa_sprite_atlas_motion.py` | 解码最终图集并验证像素、清单和连续性 |
| `tools/normalize_sprite.py` | 统一单张精灵的画布、缩放和对齐 |
| `tools/split_sprite_sheet.py` | 拆分精灵表并执行预乘 Alpha 缩放 |
| `tools/build_animation_preview.py` | 生成仅供人工检查的动画预览 |

工具的实际参数以 `python <script> --help` 为准。不要从旧文档复制已经变化的固定总帧数。

## 7. 仓库边界

| 路径 | Git 与清理约定 |
| --- | --- |
| 根目录 `*.cs`、`*.xaml`、`DesktopPet.csproj` | 正式源码，必须跟踪 |
| `pic/` | 用户原图，必须跟踪；生成脚本不得覆盖或删除 |
| `Assets/` | 正式运行时素材和图集，必须跟踪；人工预览 PNG 除外 |
| `tools/generated_sources/` | 正式创作源应受 Git 跟踪；未甄选候选应放 `_scratch/`，QA 输出应放 `_qa/` |
| `.codex_tmp/`、`tmp/` | 本地中间产物，不能作为唯一素材备份 |
| `bin/`、`obj/`、`tests/**/bin/`、`tests/**/obj/` | 可重建输出，忽略 |
| `dist/` | 本地发布目录，所有 EXE 忽略；正式二进制走 GitHub Release |
| `runtime/` | 只跟踪运行时说明，不跟踪微软安装包 EXE |

判断正式素材时使用：

```powershell
git ls-files Assets pic tools/generated_sources
```

不要把整个 `Assets/` 或 `tools/generated_sources/` 当缓存清理。工作树可能包含用户尚未提交的素材候选，修改前先检查：

```powershell
git status --short
git status --short --ignored
```

## 8. 关键开发约束

- 保持微软雅黑、透明窗口、Per-Monitor DPI 和负坐标副屏行为一致。
- 动画位置使用高精度逻辑坐标，最终写入窗口位置时才对齐物理像素。
- 渲染回调不得同步读盘、解压、写文件或逐帧分配大对象。
- 冷页未就绪时保持旧帧；延迟恢复后定位绝对时间对应帧，不快速补播积压帧。
- 打工普通/认真循环必须保持 96 个逻辑帧、65 张独特位图、9 个精确中性接缝、最长 5 帧中性停顿和 8 次不等间隔四指触键；普通循环为 1.6 秒。v5 语义手/袖口蒙版之外的静态锁区必须零漂移，四根目标手指实际位移必须保持在 `5.528–6.049 px` 验收区间。
- 打工单击是严格视觉无操作；仅当普通循环没有任何分页正在解码时，才允许用非紧急请求预热认真入口页，不能抢占普通循环冷页、改变画面、相位、速度或期限。双击立即把当前相位切到 2 倍速，到最近精确接缝后播放约 133 ms 认真眉过渡，再从认真循环开始完整计时 4 秒；不得用临时加速补播积压帧或在触键途中硬切。
- 手动顶部不吸附，但固定 `454×454` 透明包络不能成为顶部拖动限制。拖动必须保持鼠标捕获，并按光标所在显示器的原生 `rcWork`、真实 HWND 和当前帧可见边界以物理像素实时定位；不得调用系统 `DragMove`，不得新增 `EdgeDock.Top`。自动熊猫路线仍可经过顶部。
- 巡游中断必须冻结最后一次已经呈现的支撑点。左键允许反向播放真实登乘帧，拖动必须在捕获式物理定位开始前释放巡游的位置所有权；右键只能在退场完成后通过缓存的 Dispatcher 回调打开待办，不能在 `Rendering` 中重入 owned window。
- 待办行静止态必须用透明 `BorderBrush` 保留一像素布局占位，悬停时才同时切换填充和完整四边描边；外层 `ListBoxItem` 不得恢复系统悬停触发器。
- 待办、设置和定时任务写入必须保持临时文件替换；`ScheduledTaskStore` 还必须保持加载失败后的禁止覆盖保护。待办和设置损坏时当前只回退为空值或默认值，排查前应先备份原文件。
- `dist/**/*.exe` 与运行时安装包不得进入普通 Git 历史。

## 9. 当前文档边界

- 仓库没有 `LICENSE`、`CONTRIBUTING.md`、`SECURITY.md` 或 CI 工作流；不要在对外说明中声称已有这些流程。
- 当前源码版本与 GitHub 已发布版本可能不同。版本可用性以 Releases 页面为准，源码行为以当前工作树和测试结果为准。
