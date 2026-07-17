# 小鲁班桌面宠物

一个轻量的 Windows 11 桌面宠物。启动后，小鲁班以睡觉待机状态显示在主屏幕右下角、任务栏上方。

## 使用方式

- 左键单击：立即进入一个独立人物动作；人物先用 12 张起身姿势放下抱枕，再用 24 张专属姿势进入动作，主体阶段以 4 张轻微眨眼/呼吸姿势循环 6 秒，最后沿原路径返回睡觉待机。完整一轮约 9.6 秒。
- 8 个点击动作按“打哈欠 → 委屈 → 奔跑 → 欢呼 → 点赞 → 吃饼干 → 挥手 → 思考”的顺序轮换；第 9 次有效点击重新从打哈欠开始。
- `pic` 文件夹中的 9 张原图会跨多次点击全部用上：`小鲁班2.png` 提供固定睡觉待机，其余 8 张分别对应 8 个短动作。
- 8 组动作都由一次生成的连续 24 帧图集拆分，人物比例、服装、道具和镜头在同一序列内保持一致；公共起身序列负责从真实睡觉待机自然衔接到站姿。
- 进入和返回阶段以每帧 50ms（约 20 FPS）播放；一轮运行时间线共 112 帧，进场和返程各约 1.8 秒，动作微循环 6 秒，总时长约 9.6 秒。
- 图片切换期间主图层始终 `Opacity=1`，备用覆盖图层永久折叠；所有画面都是单个人物的冻结 `Pbgra32` 实图，不做相邻图片透明混合，避免图层交接、实时解码或短暂双影造成闪烁。
- 240 张动画姿势和 1 张待机图均在启动阶段预加载；点击时只替换已经准备好的单层位图，不在动画过程中做图片计算。
- 不操作桌宠时：每 10 秒进入一次自动活动；普通活动继续按“小鲁班1 → 小鲁班2抱枕呼吸 → 小鲁班3 … → 小鲁班9”轮换，开启绕屏功能后每第 4 次自动活动会改为沿当前屏幕边缘移动。
- 自动绕屏包含趴着蠕动、四肢爬行和走走跳跃三种方式，横向和竖向各有独立 4 帧循环；移动使用连续时间差驱动，到达拐角会先用 320ms 平滑缩身转向再继续，避免横竖姿势瞬跳。
- 抱枕呼吸使用 5 秒的轻微缓慢缩放，点击时可以立即打断并进入人物动作。
- 动画播放期间再次点击：直接忽略，不重启动作、不切换对白、不延长时间，也不会排队追加下一轮。
- 左键拖动：移动桌宠位置；拖动不会误触动画。
- 右键单击：在小鲁班左侧打开或收起白色对话气泡待办。
- 拖到任意显示器的左、右、上、下工作区边界并松手：小鲁班会吸附在该屏幕边缘，两只手扒住边界，循环探头和眨眼；手动探头期间不会启动自动绕屏，只有再次拖离边缘才恢复普通状态。
- 双屏/多屏按小鲁班当前所在显示器分别读取工作区，支持副屏负坐标，并避开各屏任务栏。
- 待办气泡内的“自动绕屏移动”开关只控制绕屏移动；关闭后，其他自动可爱动作和抱枕呼吸仍会继续。设置保存到 `%LocalAppData%\LubanDesktopPet\settings.json`。
- 点击待办内部可以正常编辑；点击人物、窗口其他区域、桌面或其他应用会自动收起待办。
- 对白和待办使用独立浮层，宠物主窗口始终固定为 `145×185`，打开或收起气泡时不再缩放、移动或裁剪人物窗口，从根源避免人物闪现和跳位。
- 待办支持新增、回车添加、勾选完成和删除。
- 待办自动保存到 `%LocalAppData%\LubanDesktopPet\todos.json`。
- 待办气泡右上角提供“收起”和“退出”。

项目没有联网、账号、提醒或后台服务。

## 日志

- 程序会在 EXE 同级的 `log` 文件夹写入按天滚动的 UTF-8 日志，例如 `log\xlb-pet-2026-07-17.log`。
- 日志记录应用启动/退出、动作开始/结束、待办气泡开关、待办数量变化和未处理异常；不会写入待办正文。
- EXE 同级目录不可写时，会自动回退到 `%LocalAppData%\LubanDesktopPet\log`；日志写入失败不会影响桌宠运行。

## 动画素材对应关系

| 原图 | 动画状态 | 项目资源 |
| --- | --- | --- |
| `pic\小鲁班1.jpg` | 犯困/打哈欠 | `Assets\luban-yawn-frame-01.png` … `24.png` |
| `pic\小鲁班2.png` | 睡觉待机 | `Assets\luban-idle.png` |
| `pic\小鲁班3.png` | 委屈哭泣 | `Assets\luban-cry-frame-01.png` … `24.png` |
| `pic\小鲁班4.png` | 奔跑 | `Assets\luban-run-frame-01.png` … `24.png` |
| `pic\小鲁班5.png` | 欢呼卖萌 | `Assets\luban-cute-frame-01.png` … `24.png` |
| `pic\小鲁班6.png` | 眨眼点赞 | `Assets\luban-like-frame-01.png` … `24.png` |
| `pic\小鲁班7.png` | 吃圆形饼干 | `Assets\luban-eat-frame-01.png` … `24.png` |
| `pic\小鲁班8.png` | 挥手 | `Assets\luban-wave-frame-01.png` … `24.png` |
| `pic\小鲁班9.png` | 托腮思考 | `Assets\luban-think-frame-01.png` … `24.png` |

8 个点击动作分别拥有独立的 24 姿势序列，并共享 12 张连续起身姿势。每次点击固定经过“待机 → 起身 12 帧 → 本动作 24 帧 → 主体微循环 6 秒 → 反向序列 → 待机”，不会夹入其他动作。另有 12 张边缘探头姿势和 24 张绕屏移动姿势。运行素材统一为 `450×550 RGBA` 透明 PNG，约为桌宠实际显示尺寸的 3 倍，并使用统一人物比例和定向边缘锚点；加载时预解码为约 `240×293 Pbgra32`，在 `145×185` 的桌宠窗口中保持清晰并控制包体与内存占用。

## 运行

已发布程序位于 `dist\LubanDesktopPet.exe`，适用于 Windows 11 x64，需要 .NET 8 Desktop Runtime。项目已附带[微软官方 .NET Desktop Runtime 8.0.29 x64 安装包](runtime/dotnet-desktop-runtime-8.0.29-win-x64/windowsdesktop-runtime-8.0.29-win-x64.exe)，其 SHA-512 和数字签名校验信息见[安装包说明](runtime/dotnet-desktop-runtime-8.0.29-win-x64/README.md)。当前桌宠 EXE 未做商业代码签名，首次从网络下载运行时 Windows SmartScreen 可能会显示提示。

```powershell
dotnet run --project .\DesktopPet.csproj
```

## 验证

```powershell
$env:LUBAN_UI_RENDER_DIR='.\tmp\ui-renders'
dotnet run --project .\tests\UiStateChecks\UiStateChecks.csproj -c Release
dotnet run --project .\tests\TodoStoreChecks\TodoStoreChecks.csproj -c Release
```

## 重新发布

```powershell
dotnet publish .\DesktopPet.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -o .\dist
```
