# 小鲁班桌面宠物

一个面向 Windows 11 的轻量桌面宠物，使用 WPF 和 .NET 8 构建。启动后，小鲁班会趴在枕头上打呼噜，安静待在屏幕右下角、任务栏上方。

## 功能与操作

### 人物动作

- 左键单击小鲁班会依次触发 7 组独立动作：打哈欠、委屈、欢呼卖萌、点赞、吃饼干、挥手和托腮思考；第 8 次有效点击重新从打哈欠开始。原有跑步动作已经完整移除，不会出现在点击或随机动作中。
- 每组点击动作都会经过“趴枕头待机 → 连续起身 → 专属动作 → 缓慢循环 → 原路返回待机”。姿势以 60fps 目标节拍播放，起身、动作入口和变化较大的动作内部使用经过连续性检查的补间姿势；实际帧数由最终资源序列和图集清单动态决定，不在代码或文档中写死。
- 动画播放期间再次点击不会重启动作或追加队列；拖动也不会误触点击动作。
- 没有操作时，每轮活动结束并空闲约 10 秒后，小鲁班会从 7 组动作和趴枕头打呼噜待机中随机选择一种，不按固定顺序机械重复。

### 手动屏幕边缘探头

- 左键拖动可以移动小鲁班。只有人物窗口真正碰到当前显示器工作区的左、右、上、下边界并松手，才会吸附并进入探头状态；距离边界还有空隙时不会提前触发。
- 四个方向都有对应姿势。边缘画面只保留小鲁班的头和扒住边界的两只手，背景透明，不绘制黑墙或黑色挡板。
- 探头只在手动拖到边缘并松手后触发；再次把小鲁班拖离边缘后，会恢复普通待机和随机可爱动作。
- 自动绕屏、沿边爬行、跑步和蠕动已经取消。空闲时只播放原地可爱动作，不会自行沿屏幕边界移动；右键菜单也不再保留绕屏开关。
- 支持双屏和多屏：按人物当前所在显示器读取独立工作区，支持副屏负坐标、不同 DPI/缩放率，并避开各屏任务栏。显示器热插拔或分辨率变化后，会重新校准人物位置和边缘吸附状态。

### 待办事项

- 右键单击小鲁班可打开或收起备忘窗口。窗口顶部提供“待办事项 / 定时任务”两个胶囊选项卡，每次打开默认显示待办事项。待办支持新增、回车添加、勾选完成、删除、修改文字和拖拽排序，内容与当前顺序都会自动保存到 `%LocalAppData%\LubanDesktopPet\todos.json`。
- 每行左侧的 `≡` 是专用拖拽手柄；从手柄拖动即可调整顺序，不会抢占正文的鼠标选择和 `Ctrl+C`。单击每行右侧笔尖朝左下的铅笔图标，或选中文字后按 `F2`，可进入行内修改；`Enter` 保存、`Esc` 取消，空白内容不会覆盖原待办。
- 待办使用独立的 WPF `Owned Window`，而不是嵌在 `Popup` 中。这解决了微软拼音候选框跑到屏幕左上角的问题，也能正确保护输入法选词期间的 Enter 键。
- 拖动小鲁班时，待办窗口会跨屏、跨 DPI 跟随人物并保持展开；单击小鲁班（非拖动）、桌面或其他应用会自动收起。
- 列表区域可完整显示五行待办，更多内容使用垂直滚动条，不会继续撑大窗口。
- 待办窗口提供“收起”“退出”和桌宠大小滑块。默认逻辑尺寸为 `190×242`，比旧版高度增加约 1.5 厘米；可在 75%～140% 之间连续调节。缩放预览由屏幕合成时钟平滑驱动，停止调节后才一次性保存到 `%LocalAppData%\LubanDesktopPet\settings.json`，避免频繁重排窗口和写磁盘。所有姿势共用同一尺寸基准，打开或收起待办不会让人物忽大忽小。
- 桌宠、待办窗口、提示文字和控件统一使用微软雅黑，避免不同窗口回退到不同系统字体。

### 定时任务

- 在右键窗口切换到“定时任务”，填写提醒内容、日期和 `HH:mm:ss` 时间即可创建一次性提醒，精确保存到秒。待提醒列表按时间稳定排序，并自动保存到 `%LocalAppData%\LubanDesktopPet\scheduled-tasks.json`。
- 到点时，小鲁班会中断普通动作、平滑放大到 140%，用现有高清挥手帧举起矢量小喇叭，并在黄白色可爱对话框中显示提醒内容；长内容可滚动、选中和复制。点击“知道啦”后才算完成，并平滑恢复提醒前的桌宠大小，不会把临时 140% 写进用户设置。
- 同一秒到期的多条任务会按创建顺序逐条提示，确认上一条后显示下一条。程序休眠、界面短暂阻塞或到点时未运行，都不会快速补播计时；下次可用时会立即检查所有逾期任务。
- 到点不会立刻从磁盘删除任务，只有用户点击“知道啦”后才原子保存删除结果。因此提醒期间退出或崩溃时，尚未确认的内容会在下次启动重新提示，不会静默丢失。
- 如果启动时定时任务文件暂时被占用、无权读取或 JSON 损坏，本次运行会进入只读保护，不会在退出时把原文件覆盖成空列表；保护状态会写入 `log`。

项目不需要联网、账号或后台服务。

## 动画与内存

- `pic` 文件夹中的 9 张原图全部保留：`小鲁班2.png` 用作趴枕头打呼噜待机，7 张对应现有点击动作；`小鲁班4.png` 仅作为原始参考图保留，跑步动作移除后不再参与运行时动画和图集。
- 最终起身、动作和循环序列采用可变长度编号帧。构建器根据磁盘上的连续编号资源自动分配分页；最终逻辑帧数、页内帧数和分页数以 `Assets\luban-sprite-pages.json` 的 `sourceFrameCount`、`pageFrameCount` 和 `pages` 为准。生成尚未完成时不要沿用旧清单中的数字。
- 精灵以 `399×509` 高密度像素渲染，对应 `190×242` 逻辑显示基准。即使 Windows 使用 150% DPI 且桌宠大小调到 140%，仍有足够的源像素，不需要把低清小图放大；所有姿势使用统一逻辑边界和基线，避免动画中的缩放抖动。
- 枕头使用独立的静态透明层，人物待机和起身帧只更新人物本身。这样枕头不会随每一帧反复淡入淡出，也不会把枕头边缘的 Alpha 波动表现成光纹。
- 图集清单使用 `version: 4`、`compression: "brotli"` 契约。运行时资源是无损 Brotli 压缩的 Pbgra32 分页（`*.pbgra.br`），PNG 只用于构建和目视检查；每一页解码后不超过清单声明的 24 MiB 上限。
- 启动时只同步解码首个待机页，随后在后台依次预热其余分页并保留在常驻页缓存；用户立即触发的动作可以抢占普通预热。此实现明确用更多稳定内存换取后续切页无解压卡点，不在动作结束时驱逐页面或强制 GC。总内存取决于最终清单中各页的 `uncompressedByteCount`、Windows、DPI 和显卡驱动，因此不承诺固定数字。
- 人物动作和手动边缘探头由单一 `CompositionTarget.Rendering + Stopwatch` 绝对时间轴驱动。人物姿势目标为 60fps，窗口呈现跟随显示器合成刷新；回调稍晚时直接定位正确姿势，不快速补播积压帧。相邻姿势直接发布清晰单帧，较大变化由专用桥接姿势连接，不做整图交叉淡化。
- 动画播放速度通过 `MainWindow.xaml.cs` 顶部的代码常量 `AnimationPlaybackSpeed` 配置，默认值为 `1.25`；`1.0` 表示原速，大于 `1.0` 时播放更快。当前不提供 UI 滑块，也不会持久化该值，修改后需要重新编译程序；自动待机间隔和桌宠大小缩放动画不受影响。
- 渲染过程复用一个可见 `399×509 Pbgra32` 位图和工作缓冲，只提交新旧人物边界的脏矩形。渲染回调不读盘、不解压、不写日志，也不会为每一帧创建新位图；图集载入、动作开始和结束摘要通过后台日志队列写入。

## 动画素材对应关系

| 原图 | 动画状态 | 项目资源 |
| --- | --- | --- |
| `pic\小鲁班1.jpg` | 犯困/打哈欠 | `Assets\luban-yawn-frame-01.png` … `24.png` |
| `pic\小鲁班2.png` | 趴枕头打呼噜待机 | `Assets\luban-idle.png` |
| `pic\小鲁班3.png` | 委屈哭泣 | `Assets\luban-cry-frame-01.png` … `24.png` |
| `pic\小鲁班4.png` | 原始参考图（跑步已移除） | 保留原图，不参与运行时图集 |
| `pic\小鲁班5.png` | 欢呼卖萌 | `Assets\luban-cute-frame-01.png` … `24.png` |
| `pic\小鲁班6.png` | 眨眼点赞 | `Assets\luban-like-frame-01.png` … `24.png` |
| `pic\小鲁班7.png` | 吃圆形饼干 | `Assets\luban-eat-frame-01.png` … `24.png` |
| `pic\小鲁班8.png` | 挥手 | `Assets\luban-wave-frame-01.png` … `24.png` |
| `pic\小鲁班9.png` | 托腮思考 | `Assets\luban-think-frame-01.png` … `24.png` |

## 日志

- 程序优先在 EXE 同级的 `log` 文件夹写入按天滚动的 UTF-8 日志，例如 `log\xlb-pet-2026-07-17.log`。
- 日志记录应用启动/退出、动作开始/结束、边缘探头、待办状态、定时任务标识与触发时间以及未处理异常，不会记录待办或提醒正文。
- EXE 同级目录不可写时，会回退到 `%LocalAppData%\LubanDesktopPet\log`；日志写入失败不会阻止桌宠运行。

## 运行环境

- 系统：Windows 11 x64
- 框架：x64 `.NET 8 Desktop Runtime 8.0.29`
- 已发布程序：`dist\LubanDesktopPet.exe`
- 为避免把约 56 MiB 的微软安装包重复提交到 GitHub，仓库只记录版本、官方地址和校验信息，详见 [`runtime\dotnet-desktop-runtime-8.0.29-win-x64\README.md`](runtime/dotnet-desktop-runtime-8.0.29-win-x64/README.md)。

首次使用时，如系统尚未安装该运行时，请从微软官方地址下载并安装，再运行 `dist\LubanDesktopPet.exe`。当前桌宠 EXE 未做商业代码签名，从网络下载后 Windows SmartScreen 可能显示提示。

## 开发、构建与验证

在项目根目录使用 PowerShell：

```powershell
# 开发运行
dotnet run --project .\DesktopPet.csproj

# Release 构建
dotnet build .\DesktopPet.csproj -c Release

# 安装统一缩放和定位的基础姿势、静态枕头层与手动边缘探头帧
python .\tools\install_generated_motion_assets.py --v6-motion --source-directory .\tools\generated_sources --assets-directory .\Assets

# 首次从基础姿势生成60fps可变长度序列（需要RIFE；离线生成耗时较长）
# 如工具不在默认的.codex_tmp目录，先设置：
# $env:XLB_RIFE_ROOT = 'C:\path\to\rife-ncnn-vulkan-20221029-windows'
python .\tools\generate_dense_motion_assets.py --wake --actions --loops --edge-peek

# 源PNG连续性、透明通道与相邻姿势检查
python .\tools\qa_dense_motion_assets.py --contacts

# 确定性重建Brotli v4分页图集及清单；分页数由最终资源动态决定
python .\tools\build_sprite_atlas.py

# 解码最终Pbgra分页并检查清单、像素和连续性
python .\tools\qa_sprite_atlas_motion.py --contacts

# 查看最终动态计数，不要从旧README或旧清单复制数字
$manifest = Get-Content .\Assets\luban-sprite-pages.json -Raw | ConvertFrom-Json
[pscustomobject]@{
    Version = $manifest.version
    Compression = $manifest.compression
    SourceFrames = $manifest.sourceFrameCount
    PageFrames = $manifest.pageFrameCount
    Pages = $manifest.pages.PSObject.Properties.Count
}

# UI 状态、动画、手动边缘探头、多屏、待办与定时提醒契约检查
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj -c Release

# 待办、定时任务和设置持久化检查
dotnet run --project .\tests\TodoStoreChecks\TodoStoreChecks.csproj -c Release
```

## 重新发布

以下命令生成依赖 .NET 8 Desktop Runtime 的 Windows x64 单文件版本：

```powershell
dotnet publish .\DesktopPet.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o .\dist
```

发布结果位于 `dist\LubanDesktopPet.exe`。这是依赖 .NET 8 Desktop Runtime 的框架依赖单文件程序，不是自包含运行时包。

发布前必须先确认最终清单已经是 Brotli v4，并检查 EXE 大小和哈希：

```powershell
$manifest = Get-Content .\Assets\luban-sprite-pages.json -Raw | ConvertFrom-Json
if ($manifest.version -ne 4 -or $manifest.compression -ne 'brotli') {
    throw '最终图集不是Brotli v4，禁止发布'
}

$exe = Get-Item .\dist\LubanDesktopPet.exe
if ($exe.Length -ge 100MB) {
    throw 'EXE达到GitHub普通Git对象100 MiB硬限制，请改用Release附件或Git LFS'
}
if ($exe.Length -ge 95MB) {
    Write-Warning 'EXE已接近100 MiB硬限制；发布前应减少边缘序列或启用选择性无损差分压缩'
}
if ($exe.Length -ge 50MB) {
    Write-Warning 'GitHub会对超过50 MiB的普通Git对象给出大文件警告'
}

$exe | Select-Object FullName, Length, LastWriteTime
Get-FileHash $exe.FullName -Algorithm SHA256
Get-AuthenticodeSignature $exe.FullName | Select-Object Status, StatusMessage
```

当前程序没有商业代码签名，因此签名状态预计为 `NotSigned`；这不是哈希失败。最终发布还需要实际启动 EXE，逐项冒烟检查点击动作、快速拖动大小、待办输入与复制、窗口外点击收起、手动四边探头、双屏/DPI，以及 `log` 文件夹中的异常和分页预热记录。

### 发布验收清单

- [ ] 源 PNG QA、最终 Pbgra 图集 QA、Release 构建、UI 检查和持久化检查全部通过，且没有忽略失败项。
- [ ] 最终清单声明 `version: 4` 和 `compression: "brotli"`；`sourceFrameCount`、`pageFrameCount`、实际分页和嵌入资源彼此一致，输出目录不再残留 `*.pbgra.lz4`。
- [ ] 使用上面的框架依赖单文件命令发布；目标机器已安装 x64 .NET 8 Desktop Runtime。
- [ ] `LubanDesktopPet.exe` 小于 GitHub 普通 Git 对象的 100 MiB 硬限制，并记录最终字节数和 SHA-256；超过限制时不要强行提交，改用 GitHub Release 附件或 Git LFS。
- [ ] 实机连续触发七种点击动作并观察起身、循环、返回和随机待机；无自动绕屏、逐帧抖动、透明光纹、忽大忽小或冷页快速补播。
- [ ] 快速往返拖动大小滑块，人物和滑块都连续；待办支持 `Ctrl+C`、长文本换行/提示、文字选择复制、浅蓝色行悬停、专用手柄拖拽排序、行内修改，以及微软/搜狗输入法。
- [ ] 分别设置普通秒级提醒、同秒两条提醒和长文字提醒；到点后人物平滑放大、举喇叭、正文可复制，逐条点击“知道啦”后才删除，最后恢复原尺寸且 `settings.json` 未写成 140%。
- [ ] 待办随人物跨屏拖动，点击其他位置自动收起；四边手动探头、负坐标副屏、100%/125%/150% DPI 和显示器变化恢复正常。
- [ ] 等后台分页预热结束后复测动作，并检查内存进入稳定区间、没有持续增长；`log` 中没有未处理异常、Brotli 解码失败或分页预热失败。
- [ ] Git 暂存仅包含计划交付文件，不包含 `.codex_tmp`、生成缓存、绿幕中间图或无关历史 QA 文件。
