# 小鲁班桌面宠物

一个轻量的 Windows 11 桌面宠物。启动后，小鲁班以睡觉待机状态显示在主屏幕右下角、任务栏上方。

## 使用方式

- 左键单击：立即进入一个独立人物动作；桥接姿势平滑进入后，主体动作会完整静止显示 5 秒，再沿原路径返回抱枕睡觉待机。完整一轮约 6.7–7.8 秒。
- 8 个点击动作按“打哈欠 → 委屈 → 奔跑 → 欢呼 → 点赞 → 吃饼干 → 挥手 → 思考”的顺序轮换；第 9 次有效点击重新从打哈欠开始。
- `pic` 文件夹中的 9 张原图会跨多次点击全部用上：`小鲁班2.png` 提供固定睡觉待机，其余 8 张分别对应 8 个短动作。
- 每个动作只使用自己的 1–2 张关键姿势桥接图，从睡觉姿势沿同一肢体轨迹进入动作，再按相反顺序返回待机；桥接帧稳定停留 220ms，过渡采用 240ms 新姿势显现加 80ms 旧姿势退出。
- 图片切换期间始终至少保留一个完全可见图层，不再使用会让透明 PNG 周期性变暗的互补透明度淡入，因此点击和回待机时不会一闪一闪。
- 所有运行时图片均在启动时预加载，避免第一次播放某个动作时卡顿。
- 不操作桌宠时：每 10 秒触发一次自然动画，按“小鲁班1 → 小鲁班2抱枕呼吸 → 小鲁班3 … → 小鲁班9”的顺序循环；自动动画不弹对白，不会打扰待办。
- 抱枕呼吸使用 5 秒的轻微缓慢缩放，点击时可以立即打断并进入人物动作。
- 动画播放期间再次点击：直接忽略，不重启动作、不切换对白、不延长时间，也不会排队追加下一轮。
- 左键拖动：移动桌宠位置；拖动不会误触动画。
- 右键单击：在小鲁班左侧打开或收起白色对话气泡待办。
- 点击待办内部可以正常编辑；点击人物、窗口其他区域、桌面或其他应用会自动收起待办。
- 对白和待办使用独立浮层，宠物主窗口始终固定为 `145×185`，打开或收起气泡时不再缩放、移动或裁剪人物窗口，从根源避免人物闪现和跳位。
- 待办支持新增、回车添加、勾选完成和删除。
- 待办自动保存到 `%LocalAppData%\LubanDesktopPet\todos.json`。
- 待办气泡右上角提供“收起”和“退出”。

项目没有联网、账号、提醒或后台服务。

## 动画素材对应关系

| 原图 | 动画状态 | 项目资源 |
| --- | --- | --- |
| `pic\小鲁班1.jpg` | 犯困/打哈欠 | `Assets\luban-yawn.png` |
| `pic\小鲁班2.png` | 睡觉待机 | `Assets\luban-idle.png` |
| `pic\小鲁班3.png` | 委屈哭泣 | `Assets\luban-cry.png` |
| `pic\小鲁班4.png` | 奔跑 | `Assets\luban-run.png` |
| `pic\小鲁班5.png` | 欢呼卖萌 | `Assets\luban-cute.png` |
| `pic\小鲁班6.png` | 眨眼点赞 | `Assets\luban-like.png` |
| `pic\小鲁班7.png` | 坐着吃饼干 | `Assets\luban-eat.png` |
| `pic\小鲁班8.png` | 挥手 | `Assets\luban-wave.png` |
| `pic\小鲁班9.png` | 托腮思考 | `Assets\luban-think.png` |

8 个点击动作分别拥有独立的待机桥接序列。哈欠、委屈、奔跑、卖萌、点赞和挥手使用两张连续关键姿势；吃饼干与思考使用一张即可自然衔接。每次点击固定只经过“待机 → 本动作桥接序列 → 本动作静止 5 秒 → 反向桥接序列 → 待机”，不会夹入其他动作的姿势。挥手全程保持同一只手，点赞保持同一只拇指和起身轨迹，枕头也会沿桥接姿势落到地面而不是突然换位。全部运行时资源均为 `900×1100 RGBA` 透明 PNG，人物中心与底部锚点统一，并使用高质量缩放和位图缓存。

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
