# 小鲁班桌面宠物

一个面向 Windows 11 的轻量桌面宠物，使用 WPF 和 .NET 8 构建。启动后，小鲁班会趴在枕头上打呼噜，安静待在屏幕右下角、任务栏上方。

## 功能与操作

### 人物动作

- 左键单击小鲁班会依次触发 8 组独立动作：打哈欠、委屈、奔跑、欢呼卖萌、点赞、吃饼干、挥手和托腮思考；第 9 次有效点击重新从打哈欠开始。
- 每组点击动作都会经过“趴枕头待机 → 连续起身 → 专属动作 → 缓慢循环 → 原路返回待机”。播放节奏已经放慢，并使用真实连续姿势补帧，使起身、表演和返回的切换更连贯。
- 动画播放期间再次点击不会重启动作或追加队列；拖动也不会误触点击动作。
- 没有操作时，每轮活动结束并空闲约 10 秒后，小鲁班会从 8 组动作和趴枕头打呼噜待机中随机选择一种，不按固定顺序机械重复。

### 屏幕边缘探头

- 左键拖动可以移动小鲁班。只有人物窗口真正碰到当前显示器工作区的左、右、上、下边界并松手，才会吸附并进入探头状态；距离边界还有空隙时不会提前触发。
- 四个方向都有对应姿势。边缘画面只保留小鲁班的头和扒住边界的两只手，背景透明，不绘制黑墙或黑色挡板。
- 手动探头时不会自动绕屏。再次把小鲁班拖离边缘后，才恢复普通待机和自动活动。
- 支持双屏和多屏：按人物当前所在显示器读取独立工作区，支持副屏负坐标、不同 DPI/缩放率，并避开各屏任务栏。显示器热插拔或分辨率变化后，会重新校准人物位置和绕屏路径。

### 自动绕屏

- 右键打开待办窗口后，可通过“自动绕屏移动”复选框单独开关绕屏；关闭它不会关闭其他随机可爱动作。
- 从关闭切换为勾选时，会收起待办并立即开始完整绕当前屏幕一圈；如果人物正处于手动边缘探头状态，会在拖离边缘后补跑这一圈。
- 每次完整绕屏结束后，下一次会随机安排在 10～20 分钟后开始。正常未交互状态下，这一调度为走完一整圈留出了余量，保证半小时内至少完成一次绕屏。
- 绕屏会随机使用趴着蠕动、四肢爬行、走走跳跃三种方式，并随机选择顺时针或逆时针。人物位置按连续时间差更新，拐角会平滑转向，不是一格一格跳动。
- 开关状态保存到 `%LocalAppData%\LubanDesktopPet\settings.json`。

### 待办事项

- 右键单击小鲁班可打开或收起待办。待办支持新增、回车添加、勾选完成和删除，内容自动保存到 `%LocalAppData%\LubanDesktopPet\todos.json`。
- 待办使用独立的 WPF `Owned Window`，而不是嵌在 `Popup` 中。这解决了微软拼音候选框跑到屏幕左上角的问题，也能正确保护输入法选词期间的 Enter 键。
- 拖动小鲁班时，待办窗口会跨屏、跨 DPI 跟随人物并保持展开；单击小鲁班（非拖动）、桌面或其他应用会自动收起。
- 列表区域可完整显示五行待办，更多内容使用垂直滚动条，不会继续撑大窗口。
- 待办窗口提供“收起”“退出”和“自动绕屏移动”开关。宠物主窗口始终保持 `145×185`，打开或收起待办不会缩放、裁剪或挪动人物。

项目不需要联网、账号或后台服务。

## 动画与内存

- `pic` 文件夹中的 9 张原图均已利用：`小鲁班2.png` 用作趴枕头打呼噜待机，其余 8 张分别对应 8 组点击动作。
- 289 个逻辑帧在构建前离线整理为 13 张紧凑分页图集；只裁掉完全透明的空白，每个精灵保留 2 像素透明隔离带，人物像素、透明通道、帧数和播放时序都不降低。
- 运行时只常驻一个 `1023×815 Pbgra32 WriteableBitmap`。切换动作时把当前分页一次写入同一缓冲区，后续逐帧仅改变整数 `Viewbox`；`ImageSource`、人物图层和分辨率始终不变，因此既避免纹理越播越多，也消除换图源造成的闪烁。
- 图集分页首次载入、动作完成和阈值回收都会写入日志。这里不承诺固定内存数字，实际占用会随 Windows、DPI、显卡驱动和运行环境变化。

## 动画素材对应关系

| 原图 | 动画状态 | 项目资源 |
| --- | --- | --- |
| `pic\小鲁班1.jpg` | 犯困/打哈欠 | `Assets\luban-yawn-frame-01.png` … `24.png` |
| `pic\小鲁班2.png` | 趴枕头打呼噜待机 | `Assets\luban-idle.png` |
| `pic\小鲁班3.png` | 委屈哭泣 | `Assets\luban-cry-frame-01.png` … `24.png` |
| `pic\小鲁班4.png` | 奔跑 | `Assets\luban-run-frame-01.png` … `24.png` |
| `pic\小鲁班5.png` | 欢呼卖萌 | `Assets\luban-cute-frame-01.png` … `24.png` |
| `pic\小鲁班6.png` | 眨眼点赞 | `Assets\luban-like-frame-01.png` … `24.png` |
| `pic\小鲁班7.png` | 吃圆形饼干 | `Assets\luban-eat-frame-01.png` … `24.png` |
| `pic\小鲁班8.png` | 挥手 | `Assets\luban-wave-frame-01.png` … `24.png` |
| `pic\小鲁班9.png` | 托腮思考 | `Assets\luban-think-frame-01.png` … `24.png` |

## 日志

- 程序优先在 EXE 同级的 `log` 文件夹写入按天滚动的 UTF-8 日志，例如 `log\xlb-pet-2026-07-17.log`。
- 日志记录应用启动/退出、动作和绕屏开始/结束、边缘探头、待办状态以及未处理异常，不会记录待办正文。
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

# 原始帧变更后，确定性重建 13 张分页图集及清单
python .\tools\build_sprite_atlas.py

# UI 状态、动画、边缘、多屏与待办契约检查
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
