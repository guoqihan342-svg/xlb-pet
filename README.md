# 小鲁班桌面宠物

一个面向 Windows 11 的轻量桌面宠物，使用 WPF 和 .NET 8 构建。启动后，小鲁班会趴在枕头上打呼噜，安静待在屏幕右下角、任务栏上方。

## 功能与操作

### 人物动作

- 左键单击小鲁班会依次触发 7 组独立动作：打哈欠、委屈、欢呼卖萌、点赞、吃饼干、挥手和托腮思考；第 8 次有效点击重新从打哈欠开始。原有跑步动作已经完整移除，不会出现在点击或随机动作中。
- 每组点击动作都会经过“趴枕头待机 → 连续起身 → 专属动作 → 缓慢循环 → 原路返回待机”。姿势时钟约为 30fps，并在起身、动作入口和变化较大的动作内部加入经过逐帧检查的真实补间姿势；循环次数同步增加，因此丝滑度提高但整段动作不会突然变快。
- 动画播放期间再次点击不会重启动作或追加队列；拖动也不会误触点击动作。
- 没有操作时，每轮活动结束并空闲约 10 秒后，小鲁班会从 7 组动作和趴枕头打呼噜待机中随机选择一种，不按固定顺序机械重复。

### 屏幕边缘探头

- 左键拖动可以移动小鲁班。只有人物窗口真正碰到当前显示器工作区的左、右、上、下边界并松手，才会吸附并进入探头状态；距离边界还有空隙时不会提前触发。
- 四个方向都有对应姿势。边缘画面只保留小鲁班的头和扒住边界的两只手，背景透明，不绘制黑墙或黑色挡板。
- 探头只在手动拖到边缘并松手后触发；再次把小鲁班拖离边缘后，会恢复普通待机和随机可爱动作。
- 支持双屏和多屏：按人物当前所在显示器读取独立工作区，支持副屏负坐标、不同 DPI/缩放率，并避开各屏任务栏。显示器热插拔或分辨率变化后，会重新校准人物位置和边缘吸附状态。

### 待办事项

- 右键单击小鲁班可打开或收起待办。待办支持新增、回车添加、勾选完成和删除，内容自动保存到 `%LocalAppData%\LubanDesktopPet\todos.json`。
- 待办使用独立的 WPF `Owned Window`，而不是嵌在 `Popup` 中。这解决了微软拼音候选框跑到屏幕左上角的问题，也能正确保护输入法选词期间的 Enter 键。
- 拖动小鲁班时，待办窗口会跨屏、跨 DPI 跟随人物并保持展开；单击小鲁班（非拖动）、桌面或其他应用会自动收起。
- 列表区域可完整显示五行待办，更多内容使用垂直滚动条，不会继续撑大窗口。
- 待办窗口提供“收起”“退出”和桌宠大小滑块。默认逻辑尺寸为 `190×242`，比旧版高度增加约 1.5 厘米；可在 75%～140% 之间连续调节。缩放预览由屏幕合成时钟平滑驱动，停止调节后才一次性保存到 `%LocalAppData%\LubanDesktopPet\settings.json`，避免频繁重排窗口和写磁盘。所有姿势共用同一尺寸基准，打开或收起待办不会让人物忽大忽小。
- 桌宠、待办窗口、提示文字和控件统一使用微软雅黑，避免不同窗口回退到不同系统字体。

项目不需要联网、账号或后台服务。

## 动画与内存

- `pic` 文件夹中的 9 张原图全部保留：`小鲁班2.png` 用作趴枕头打呼噜待机，7 张对应现有点击动作；`小鲁班4.png` 仅作为原始参考图保留，跑步动作移除后不再参与运行时动画和图集。
- 218 个逻辑源帧离线整理为 8 张紧凑分页图集，分页后共 218 个页内帧；只裁掉完全透明的空白，每个精灵保留 2 像素透明隔离带，人物像素和透明通道不降低。起身过程使用 27 个独立姿势，7 种动作都带独立入口桥，其中打哈欠、哭泣和思考还各有一帧动作内部桥；待机、起身和手动边缘探头共用常驻分页，构建脚本从资源清单动态校验逻辑帧数和分页帧数，不依赖硬编码总数。
- 精灵以 `399×509` 高密度像素渲染，对应 `190×242` 逻辑显示基准。即使 Windows 使用 150% DPI 且桌宠大小调到 140%，仍有足够的源像素，不需要把低清小图放大；所有姿势使用统一逻辑边界和基线，避免动画中的缩放抖动。
- 分页图集以无损 Pbgra32 LZ4 数据嵌入程序，PNG 只作为构建和目视检查预览；运行时复用一个共享压缩输入缓冲、当前页和预取页两个固定解码缓冲、一个可见 `399×509 Pbgra32` 位图及固定工作数组。分页在后台解码，完成后由 UI 线程原子交换，渲染回调不会读盘或解压，也不会为每帧重复创建位图。所有相邻姿势都直接发布清晰单帧，较大变化由专用桥接姿势连接，不叠加两个半透明人物，因此不会出现逐帧重影、像素光纹或旧帧双重亮边。
- 人物动作与边缘探头由单一 `CompositionTarget.Rendering + Stopwatch` 绝对时间轴驱动。不同刷新率下都按同一绝对时间定位姿势，不逐帧重启计时器，也不会在卡顿后快速补播积压帧。手动边缘探头的表情姿势以 70ms 间隔直接切换，进出状态不做整图交叉淡化，避免两个轮廓叠加形成闪烁和像素光纹。
- 图集分页首次载入和动作完成都会写入日志。运行时复用固定分页缓冲并交由 .NET 自然回收，不在动作结束帧安排手工 GC 或额外的内存扫描，避免收尾时出现卡点。这里不承诺固定内存数字，实际占用会随 Windows、DPI、显卡驱动和运行环境变化。

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
- 日志记录应用启动/退出、动作开始/结束、边缘探头、待办状态以及未处理异常，不会记录待办正文。
- EXE 同级目录不可写时，会回退到 `%LocalAppData%\LubanDesktopPet\log`；日志写入失败不会阻止桌宠运行。

## 运行环境

- 系统：Windows 11 x64
- 框架：.NET 8 Desktop Runtime
- 已发布程序：`dist\LubanDesktopPet.exe`
- 随项目提供的微软官方安装包：`runtime\dotnet-desktop-runtime-8.0.29-win-x64\windowsdesktop-runtime-8.0.29-win-x64.exe`
- 安装包版本、SHA-512 和数字签名校验信息见 [`runtime\dotnet-desktop-runtime-8.0.29-win-x64\README.md`](runtime/dotnet-desktop-runtime-8.0.29-win-x64/README.md)。

首次使用时，如系统尚未安装 .NET 8 Desktop Runtime，请先双击上述安装包，再运行 `dist\LubanDesktopPet.exe`。当前桌宠 EXE 未做商业代码签名，从网络下载后 Windows SmartScreen 可能显示提示。

## 开发、构建与验证

在项目根目录使用 PowerShell：

```powershell
# 开发运行
dotnet run --project .\DesktopPet.csproj

# Release 构建
dotnet build .\DesktopPet.csproj -c Release

# 安装统一缩放和定位的起身、七种点击动作与手动边缘探头帧
python .\tools\install_generated_motion_assets.py --v6-motion --source-directory .\tools\generated_sources --assets-directory .\Assets

# 确定性重建 8 张分页图集及清单
python .\tools\build_sprite_atlas.py

# UI 状态、动画、手动边缘探头、多屏与待办契约检查
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj -c Release

# 待办和设置持久化检查
dotnet run --project .\tests\TodoStoreChecks\TodoStoreChecks.csproj -c Release
```

## 重新发布

以下命令生成依赖 .NET 8 Desktop Runtime 的 Windows x64 单文件版本：

```powershell
dotnet publish .\DesktopPet.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o .\dist
```

发布结果位于 `dist\LubanDesktopPet.exe`。
