# 小鲁班桌面宠物

一个面向 Windows 11 的轻量桌面宠物，使用 WPF 和 .NET 8 构建。启动后，小鲁班会趴在枕头上打呼噜，安静待在屏幕右下角、任务栏上方。

## 功能与操作

### 人物动作

- 左键单击小鲁班会依次触发 7 组独立动作：打哈欠、委屈、欢呼卖萌、点赞、吃饼干、挥手和托腮思考；第 8 次有效点击重新从打哈欠开始。原有跑步动作已经完整移除，不会出现在点击或随机动作中。
- 每组点击动作都会经过“趴枕头待机 → 连续起身 → 专属动作 → 缓慢循环 → 原路返回待机”。姿势以 60fps 目标节拍播放，起身、动作入口和变化较大的动作内部使用经过连续性检查的补间姿势；实际帧数由最终资源序列和图集清单动态决定，不在代码或文档中写死。
- 动画播放期间再次点击不会重启动作或追加队列；拖动也不会误触点击动作。
- 没有操作时，每轮活动结束并空闲约 10 秒后，小鲁班会从 7 组动作和趴枕头打呼噜待机中随机选择一种，不按固定顺序机械重复。

### 手动屏幕边缘探头

- 左键拖动可以移动小鲁班。只有人物窗口真正碰到当前显示器工作区的左、右或下边界并松手，才会吸附并进入探头状态；距离边界还有空隙时不会提前触发。顶部中央不吸附、不播放边缘动作；拖到左上角或右上角时仍分别按左、右边缘处理。
- 左、右、下三个方向保留对应姿势。小鲁班会按“藏起来 → 悄悄偷看 → 开心地完全探头 → 害羞缩回”的节奏，交替抓紧边缘、歪头、眨眼并露出一点肩膀；开心探头会停留 650ms，缩回休息会停留 800ms，让表情和小动作更容易看清。背景透明，不绘制黑墙或黑色挡板。
- 探头只在手动拖到边缘并松手后触发；再次把小鲁班拖离边缘后，会恢复普通待机和随机可爱动作。
- 支持双屏和多屏：按人物当前所在显示器读取独立工作区，支持副屏负坐标、不同 DPI/缩放率，并避开各屏任务栏。显示器热插拔或分辨率变化后，会重新校准人物位置和边缘吸附状态。

### 自动熊猫坐骑巡游

- 可爱的绕屏动画默认开启。小鲁班会在空闲且没有待办、提醒、拖拽或其他人物动作时登上大头熊猫坐骑出发；熊猫戴着铃铛、背着竹筒，沿当前显示器工作区内侧的圆角矩形完整巡游。人物和帽子始终正立，不使用已经删除的跑步，也不把人物横放成爬行或蠕动姿势。
- 坐骑上的小鲁班恢复原版大眼造型：暖棕色分层虹膜、白色眼缘和稳定的主副高光。闭眼关键姿势保持原样，开眼、眨眼和重新睁眼由密集帧连续衔接；眼部只修改 RGB，熊猫、人物轮廓、透明 Alpha、尺寸和位置均不改变。
- 自动路线可以平滑经过工作区顶部，但它是独立的熊猫坐骑巡游段，不会恢复手动顶部吸附。把人物拖到顶部中央松手仍不探头，手动边缘状态继续只支持左、右、下。
- 右键窗口在“桌宠大小”上方提供“绕屏动画”勾选项，默认勾选。取消勾选会先倒放登乘序列，让小鲁班平稳离开熊猫坐骑，再恢复稳定待机；重新勾选只安排下一次空闲巡游，不会关闭其他七种随机可爱动作。选择会保存到 `%LocalAppData%\LubanDesktopPet\settings.json`，下次启动继续沿用。
- 每次出发只使用人物当时所在显示器的独立 `WorkArea`，不会从双屏共享边界突然跳到另一块屏幕，也不会盖住任务栏。负坐标副屏、100%/125%/150% DPI、不同缩放率和显示器热插拔都会重新计算路线；用户把人物拖到另一块屏幕后，下一次巡游使用新屏幕。
- 左键点击或拖动、右键打开任务窗口、手动边缘探头、尺寸调节和定时提醒都会优先结束巡游。开始时正放登乘动画，结束时倒放同一组动画；停止后不补播积压位置或姿势，也不会让熊猫坐骑画面一闪而过。

### 待办事项

- 右键单击小鲁班可打开或收起备忘窗口。窗口顶部提供“待办事项 / 定时任务”两个胶囊选项卡，每次打开默认显示待办事项。待办支持新增、回车添加、勾选完成、删除、修改文字和拖拽排序，内容与当前顺序都会自动保存到 `%LocalAppData%\LubanDesktopPet\todos.json`。
- 每行左侧的 `≡` 是专用拖拽手柄；从手柄拖动即可调整顺序，不会抢占正文的鼠标选择和 `Ctrl+C`。单击每行右侧笔尖朝左下的铅笔图标，或选中文字后按 `F2`，可进入行内修改；`Enter` 保存、`Esc` 取消，鼠标点到编辑框外的窗口空白、其他行或控件也会自动保存，空白内容不会覆盖原待办。微软输入法仍在选词时会等候文字正式上屏再保存。
- 待办使用独立的 WPF `Owned Window`，而不是嵌在 `Popup` 中。这解决了微软拼音候选框跑到屏幕左上角的问题，也能正确保护输入法选词期间的 Enter 键。
- 拖动小鲁班时，待办窗口会跨屏、跨 DPI 跟随人物并保持展开；单击小鲁班（非拖动）、桌面或其他应用会自动收起。
- 列表区域可完整显示五行待办，更多内容使用垂直滚动条，不会继续撑大窗口。
- 待办窗口提供“收起”“退出”、默认勾选的“绕屏动画”和桌宠大小滑块；绕屏勾选直接位于尺寸滑块上方，只控制自动熊猫坐骑巡游。默认逻辑尺寸为 `190×242`，比旧版高度增加约 1.5 厘米；可在 75%～140% 之间连续调节。缩放预览由屏幕合成时钟平滑驱动，停止调节后才一次性保存到 `%LocalAppData%\LubanDesktopPet\settings.json`，避免频繁重排窗口和写磁盘。所有姿势共用同一尺寸基准，打开或收起待办不会让人物忽大忽小。
- 桌宠、待办窗口、提示文字和控件统一使用微软雅黑，避免不同窗口回退到不同系统字体。

### 定时任务

- 在右键窗口切换到“定时任务”，填写提醒内容、日期和 `HH:mm:ss` 时间即可创建一次性提醒，精确保存到秒。单击任务右侧与关闭按钮同风格的铅笔图标，可把内容和时间回填到表单中修改；“保存”会保留任务身份、重新排序并重新调度，“取消”、切换选项卡或关闭窗口都不会误改原任务。待提醒列表自动保存到 `%LocalAppData%\LubanDesktopPet\scheduled-tasks.json`。
- 到点时，小鲁班会中断普通动作、平滑放大到 140%，换成闭眼笑着双手举喇叭的专用姿势，先轻轻回正，再进行左右小幅播报摇摆，并在黄白色可爱对话框中显示提醒内容；人物、双手、喇叭和声效线始终是同一个完整高清轮廓，不再使用会悬空、穿模或产生光纹的矢量贴层。长内容可滚动、选中和复制。点击“知道啦”后才算完成，并平滑恢复提醒前的桌宠大小，不会把临时 140% 写进用户设置。
- 同一秒到期的多条任务会按创建顺序逐条提示，确认上一条后显示下一条。程序休眠、界面短暂阻塞或到点时未运行，都不会快速补播计时；下次可用时会立即检查所有逾期任务。
- 到点不会立刻从磁盘删除任务，只有用户点击“知道啦”后才原子保存删除结果。因此提醒期间退出或崩溃时，尚未确认的内容会在下次启动重新提示，不会静默丢失。
- 如果启动时定时任务文件暂时被占用、无权读取或 JSON 损坏，本次运行会进入只读保护，不会在退出时把原文件覆盖成空列表；保护状态会写入 `log`。

项目不需要联网、账号或后台服务。

## 动画与内存

- `pic` 文件夹中的 9 张原图全部保留：`小鲁班2.png` 用作趴枕头打呼噜待机，7 张对应现有点击动作；`小鲁班4.png` 仅作为原始参考图保留，跑步动作移除后不再参与运行时动画和图集。
- 最终起身、动作、循环和熊猫坐骑巡游序列采用可变长度编号帧。`Assets\luban-roam-boarding-001.png` 起的登乘序列与 `Assets\luban-roam-flight-001.png` 起的连续主循环都至少 48 帧且每帧姿势不同：开始巡游正放 boarding，停止巡游倒放同一序列，随后才回到待机。`roam-wave` 只是可选的可爱补充，可以不加载或不播放，不能按固定 7 秒硬切打断 flight 主循环。构建器按磁盘资源动态分配 `roam-boarding`、`roam-flight` 及可选 `roam-wave` 分页；最终逻辑帧数、页内帧数和分页数以 `Assets\luban-sprite-pages.json` 的 `sourceFrameCount`、`pageFrameCount` 和 `pages` 为准，不在代码或文档中写死最终图集总数。
- 精灵以 `399×509` 高密度像素渲染，对应 `190×242` 逻辑显示基准。即使 Windows 使用 150% DPI 且桌宠大小调到 140%，仍有足够的源像素，不需要把低清小图放大；所有姿势使用统一逻辑边界和基线，避免动画中的缩放抖动。
- 枕头使用独立的静态透明层，人物待机和起身帧只更新人物本身。这样枕头不会随每一帧反复淡入淡出，也不会把枕头边缘的 Alpha 波动表现成光纹。
- 图集清单使用 `version: 4`、`compression: "brotli"` 契约。运行时资源是无损 Brotli 压缩的 Pbgra32 分页（`*.pbgra.br`）；默认构建不保存约 `115.6 MiB` 的派生分页预览 PNG，每一页解码后不超过清单声明的 24 MiB 上限。
- 启动时只同步解码首个待机页，不再常驻预热提醒、左/下探头、熊猫坐骑巡游或后续起身页；右侧镜像复用左侧分页，手动顶部分页仍不进入运行时图集。动作、`roam-boarding` 和 `roam-flight` 的分页会在播放前后台载入，可选 `roam-wave` 只在实际使用时加载。为了避免巡游中途因冷页停顿，活动期间会保护 boarding 与 flight 所需分页，结束后立即重新进入普通 LRU 回收，而不是在整个进程生命周期永久钉住。定时提醒首分页则在到期前 `2 秒`预取。最终 121 帧登乘、64 帧飞行和完整待机/起身链合计约 `122.6 MiB`，因此活动期 LRU 软预算仅从旧版 `112 MiB` 调整为 `128 MiB`；完整待机/起身链按名称固定，但后续分页仍等第一次动作才按需载入。活动结束后缓存仍裁到 `64 MiB` 待机目标，保留约 `54 MiB` 的完整待机/起身链，避免不同动作间反复解码大对象；累计释放至少 `48 MiB` 大页时，只在无动作、巡游、提醒、拖动、缩放、待办或分页解码的空闲窗口延迟请求一次非压缩 Gen2，且两次至少间隔 30 秒。它不在 Rendering 回调或动作中执行，也不压缩 LOH，不降低像素、帧数或采样质量；总进程内存仍会随 Windows、DPI、CLR 和显卡驱动变化。
- 人物动作、自动熊猫坐骑巡游和手动边缘探头共用单一 `CompositionTarget.Rendering + Stopwatch` 绝对时间轴。巡游的窗口位置按显示器刷新率连续更新，登乘和退场按绝对时间在 `roam-boarding` 中正向或反向定位，移动阶段则持续循环可变长度 `roam-flight`；逻辑坐标保持高精度，只在写入 `Left/Top` 时对齐物理像素。超过 250ms 的休眠或界面阻塞不会补移动距离，也不会快速补播积压姿势。边缘探头继续使用 `48` 帧闭环、原生 60fps 姿势节拍，在序列前半完成“藏起来 → 偷看 → 开心探头”并停留 650ms，再连续害羞缩回，在末帧休息 800ms。相邻姿势直接发布清晰单帧，较大变化由专用桥接姿势连接，不做整图交叉淡化。
- 边缘探头补帧先在画布内保留完整的头、肩膀和双手轮廓，最后才按连续位移裁到 Windows 边界；不能把已经裁断的手交给补帧器。这样双手在接触线附近不会突然出现、消失，也不会用整图淡化产生双层轮廓、光纹或缩放抖动。构建前会把完整关键姿势按端点偏移重新裁切，并与左、下共 8 张运行时关键帧逐像素比对，源图不一致时在启动耗时补帧前立即失败；顶部 4 张关键姿势只作为未嵌入的创作源保留。
- 定时提醒素材库保留 8 张核心姿势和 8 张桥接候选，运行时选用其中闭眼举喇叭的完整姿势生成 33 帧阻尼回正和 48 帧轻柔播报摇摆；收起过程反向复用入场帧，不保存第二套退场 PNG。生成时始终对人物、双手、喇叭和声效线这一整块预乘 Alpha 轮廓做确定性刚体变换，不在不同人物姿势之间做光流变形或整图淡化，因此不会生成双手、双喇叭、光纹和忽大忽小的插值中间态。
- 点击动作和自动原地动作的播放速度通过 `MainWindow.xaml.cs` 顶部的代码常量 `AnimationPlaybackSpeed` 配置，默认值为 `1.25`；熊猫坐骑的移动速度使用独立代码常量，二者都不提供速度滑块，也不会持久化，修改后需要重新编译程序。“绕屏动画”勾选只保存启用状态，不会改变七种原地动作、手动边缘探头、自动待机间隔或桌宠大小缩放动画。
- 渲染过程复用一个可见 `399×509 Pbgra32` 位图和工作缓冲，只提交新旧人物边界的脏矩形。Brotli 分页会直接流式写入最终图集缓冲，delta-sub 也逐头、逐行重建，不再为最大分页永久保留约 `22 MiB` 的压缩和 payload 工作区；所有淡化时长为零时也不会分配三张未使用的整帧缓冲。渲染回调不读盘、不解压、不写日志，也不会为每一帧创建新位图；图集载入、动作开始和结束摘要通过后台日志队列写入。

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
| 现有大头小鲁班造型参考 | 登上带铃铛、竹筒的大头熊猫坐骑巡游 | `Assets\luban-roam-boarding-001.png`、`luban-roam-flight-001.png` 起的必需序列；`roam-wave` 可选 |

## 日志

- 程序优先在 EXE 同级的 `log` 文件夹写入按天滚动的 UTF-8 日志，例如 `log\xlb-pet-2026-07-17.log`。
- 日志记录应用启动/退出、动作开始/结束、熊猫坐骑巡游开关与开始/结束摘要、边缘探头、待办状态、定时任务标识与触发时间以及未处理异常，不会记录待办或提醒正文，也不会在每个巡游渲染帧写日志。
- EXE 同级目录不可写时，会回退到 `%LocalAppData%\LubanDesktopPet\log`；日志写入失败不会阻止桌宠运行。
- 日志在后台按 `2 MiB` 单文件滚动，最多保留 `8` 个受管日志、总计 `8 MiB` 且不超过 `14` 天；单条异常信息最多 `32 KiB`。清理严格不触碰待办、设置、定时任务或其他文件。
- 程序使用当前 Windows 会话内的单实例锁；重复双击不会再启动第二个高内存进程，也不会让两个进程同时覆盖本地 JSON 或日志。

## 运行环境

- 系统：Windows 11 x64
- 框架：x64 `.NET 8 Desktop Runtime 8.0.29`
- 本地发布结果：`dist\LubanDesktopPet.exe`；超过 GitHub 普通 Git 对象上限的成品 EXE 通过 [GitHub Releases](https://github.com/guoqihan342-svg/xlb-pet/releases) 提供，不再直接塞进仓库历史。
- 为避免把约 56 MiB 的微软安装包重复提交到 GitHub，仓库只记录版本、官方地址和校验信息，详见 [`runtime\dotnet-desktop-runtime-8.0.29-win-x64\README.md`](runtime/dotnet-desktop-runtime-8.0.29-win-x64/README.md)。

首次使用时，如系统尚未安装该运行时，请从微软官方地址下载并安装，再运行本地发布的 `dist\LubanDesktopPet.exe` 或 Release 附件。当前桌宠 EXE 未做商业代码签名，从网络下载后 Windows SmartScreen 可能显示提示。

## 开发、构建与验证

在项目根目录使用 PowerShell：

```powershell
# 开发运行
dotnet run --project .\DesktopPet.csproj

# Release 构建
dotnet build .\DesktopPet.csproj -c Release

# 安装统一缩放和定位的基础姿势、静态枕头层与手动边缘探头帧
python .\tools\install_generated_motion_assets.py --v6-motion --source-directory .\tools\generated_sources --assets-directory .\Assets

# 重建12张边缘探头创作源；运行时只打包左、下共8张，右侧镜像左侧，顶部源不嵌入
python .\tools\install_generated_motion_assets.py --edge-peek --source-directory .\tools\generated_sources --assets-directory .\Assets

# 安装定时提醒的8张核心姿势与8张桥接候选（运行时序列从桥接第8张生成）
python .\tools\install_generated_motion_assets.py --reminder --source-directory .\tools\generated_sources --assets-directory .\Assets

# 首次从基础姿势生成60fps可变长度序列（普通动作需要RIFE；提醒序列使用确定性刚体生成）
# 如工具不在默认的.codex_tmp目录，先设置：
# $env:XLB_RIFE_ROOT = 'C:\path\to\rife-ncnn-vulkan-20221029-windows'
python .\tools\generate_dense_motion_assets.py --wake --actions --loops --edge-peek --reminder

# 从透明熊猫坐骑创作源生成当前boarding/flight运行素材；
# 大头熊猫的铃铛、竹筒必须始终属于同一完整轮廓。当前生成出的具体帧数
# 只是本次素材产物，不是运行时或图集清单的固定总帧接口。
python .\tools\build_roam_flight_assets.py

# boarding和flight都从001起连续编号、各至少48帧且不得重复；wave允许不存在。
# 人物和帽子保持正立，不得混入run、crawl或wriggle旧素材。最终帧数由磁盘资源动态决定。
Get-ChildItem .\Assets\luban-roam-boarding-*.png, .\Assets\luban-roam-flight-*.png, .\Assets\luban-roam-wave-*.png -ErrorAction SilentlyContinue |
    Sort-Object Name |
    Select-Object Name, Length

# 源PNG连续性、透明通道、相邻姿势、熊猫/铃铛/竹筒完整轮廓、固定接触点、单调探出和至少8 DIP真实探出深度检查
# 轮廓距离只排除由独立接触门禁检查的边界裁切线 4 个源像素，人物主体仍执行完整连续性门禁
python .\tools\qa_dense_motion_assets.py --require-edge-peek --contacts

# 确定性重建Brotli v4分页图集及清单；包括动态roam-boarding/flight分页，总页数由最终资源决定
python .\tools\build_sprite_atlas.py

# 只有人工验图时才临时输出派生预览PNG（已被Git忽略，可随时删除）
$env:XLB_ATLAS_WRITE_PREVIEWS = '1'
python .\tools\build_sprite_atlas.py
Remove-Item .\Assets\sprite-pages\*.png
Remove-Item Env:XLB_ATLAS_WRITE_PREVIEWS

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

# UI 状态、动画、自动熊猫坐骑巡游、手动边缘探头、多屏、待办与定时提醒契约检查
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj -c Release

# 只检查绕屏开关、状态机抢占、单一渲染时钟、缓存门禁、资源命名和负坐标副屏圆角路线
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj -c Release -- --roam-source-only

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
if ($exe.Length -ge 100000000) {
    Write-Warning 'EXE达到GitHub普通Git对象100,000,000字节硬限制；请上传GitHub Release附件，不要提交到Git历史'
}
if ($exe.Length -ge 95000000) {
    Write-Warning 'EXE已接近100,000,000字节硬限制；发布前应减少边缘序列或改用Release附件'
}
if ($exe.Length -ge 50MB) {
    Write-Warning 'GitHub会对超过50 MiB的普通Git对象给出大文件警告'
}

$exe | Select-Object FullName, Length, LastWriteTime
Get-FileHash $exe.FullName -Algorithm SHA256
Get-AuthenticodeSignature $exe.FullName | Select-Object Status, StatusMessage
```

当前程序没有商业代码签名，因此签名状态预计为 `NotSigned`；这不是哈希失败。最终发布还需要实际启动 EXE，逐项冒烟检查点击动作、默认开启和取消熊猫坐骑巡游、快速拖动大小、待办输入与复制、窗口外点击收起、左/右/下手动探头、顶部中央不吸附、双屏/DPI，以及 `log` 文件夹中的异常和分页预热记录。

### 发布验收清单

- [ ] 源 PNG QA、最终 Pbgra 图集 QA、Release 构建、UI 检查和持久化检查全部通过，且没有忽略失败项。
- [ ] 最终清单声明 `version: 4` 和 `compression: "brotli"`；`sourceFrameCount`、`pageFrameCount`、实际分页和嵌入资源彼此一致，输出目录不再残留 `*.pbgra.lz4`。
- [ ] 使用上面的框架依赖单文件命令发布；目标机器已安装 x64 .NET 8 Desktop Runtime。
- [ ] 记录 `LubanDesktopPet.exe` 的最终字节数和 SHA-256；小于 `100,000,000` 字节时可作为普通 Git 对象提交，达到或超过时不要强行提交，改用 GitHub Release 附件或 Git LFS。
- [ ] 实机连续触发七种点击动作并观察起身、循环、返回和随机待机；默认绕屏不应混入第八种点击动作，取消绕屏也不应关闭七种原地动作。所有动作均无逐帧抖动、透明光纹、忽大忽小或冷页快速补播。
- [ ] 首次启动确认“绕屏动画”位于桌宠大小上方且默认勾选；完整观察boarding正放、小鲁班骑着带铃铛和竹筒的大头熊猫完成顺/逆方向巡游、boarding倒放退场。人物和帽子全程正立，不出现跑步、横向爬行或蠕动；flight连续循环，不得每7秒硬切wave。左键、拖动、右键、提醒和取消勾选应平稳抢占；重新勾选只安排下一次空闲巡游。重启后确认勾选状态与桌宠大小都分别保存。
- [ ] 放大检查巡游中的小鲁班眼睛：开眼时应保持暖棕色分层虹膜、白色眼缘和清晰主副高光，眨眼应连续闭合再睁开；熊猫眼睛、脸型和坐骑轮廓不得随此修复变化，64帧循环首尾不得出现眼睛跳位、闪点或忽大忽小。
- [ ] 快速往返拖动大小滑块，人物和滑块都连续；待办支持 `Ctrl+C`、长文本换行/提示、文字选择复制、浅蓝色行悬停、专用手柄拖拽排序、行内修改、点击编辑框外自动保存，以及微软/搜狗输入法。
- [ ] 新建并修改普通秒级提醒，确认表单回填、取消、保存后的重新排序和重新调度正确；再设置同秒两条提醒和长文字提醒，确认到点后人物平滑放大、专用举喇叭动画无悬空/穿模/光纹、正文可复制，逐条点击“知道啦”后才删除，最后恢复原尺寸且 `settings.json` 未写成 140%。
- [ ] 待办随人物跨屏拖动，点击其他位置自动收起；左/右/下手动探头、顶部中央不吸附、左上/右上角仍归入侧边均保持原契约。熊猫坐骑路线只锁定出发时的当前屏幕独立工作区：在负坐标副屏及100%/125%/150% DPI各连续运行10秒，逻辑距离与代码速度一致、位置误差不超过1物理像素；共享边界不跳屏，显示器断开后安全夹回有效屏幕。
- [ ] 用59/60/120/144Hz可控时间戳验证相同绝对时间得到相同熊猫坐骑位置和姿势；模拟超过250ms的UI阻塞后不得瞬移或快速补帧。完整圆角路线每个转角只过渡一次，顶部自动路段不产生`EdgeDock.Top`，巡游与手动探头状态永不同时活跃。
- [ ] 冷启动后立即复测动作、熊猫坐骑巡游和边缘探头，并设置 `2 秒后`提醒，确认按需分页没有可见停顿；resident 解码页遵守 `128 MiB` 活动软预算、普通待机后回落到 `64 MiB`，巡游期间保护 boarding/flight 活动页并在结束后释放保护，可选wave未使用时不得被强制常驻，提醒期间动态保护完整入场/保持页，LRU 冷页能重新载入。连续运行至少10分钟或两圈后内存无持续增长；Gen2 间隔不少于30秒且不在巡游热路径触发，`log` 中没有逐帧巡游日志、未处理异常、Brotli解码失败或分页预取失败。
- [ ] Git 暂存仅包含计划交付文件，不包含 `.codex_tmp`、生成缓存、绿幕中间图或无关历史 QA 文件。
