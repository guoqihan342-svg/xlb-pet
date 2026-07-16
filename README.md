# 小鲁班桌面宠物

一个轻量的 Windows 11 桌面宠物。启动后，小鲁班以睡觉待机状态显示在主屏幕右下角、任务栏上方。

## 使用方式

- 左键单击：播放一轮连贯动作动画，依次经过睡醒、犯困、委屈、吃饼干、起跑、挥手、点赞、欢呼、思考，再回到睡觉待机。
- `pic` 文件夹中的 9 张原图全部参与动画；另外补有 9 张符合身体运动逻辑的中间姿势，共形成 18 个连续状态。
- 状态之间采用 96ms 双图层交叉淡入，各帧间隔按动作含义设为 180–330ms，淡入完成后仍至少保留 84ms 的稳定画面；所有图片在启动时预加载，避免首次播放卡顿。
- 不点击时：保持睡觉待机，不会自动乱切状态。
- 动画播放期间再次点击：本轮结束后只追加一轮完整播放；不会重启当前帧或叠加多个计时器。
- 左键拖动：移动桌宠位置；拖动不会误触动画。
- 右键单击：在小鲁班左侧打开或收起白色对话气泡待办。
- 点击待办内部可以正常编辑；点击人物、窗口其他区域、桌面或其他应用会自动收起待办。
- 气泡收起时先裁剪、再移动窗口、最后恢复人物列布局，避免透明窗口中间态导致人物闪现或跳位。
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

关键姿势之间还使用 `idle-to-yawn`、`yawn-to-cry`、`cry-to-eat`、`eat-to-run`、`run-to-wave`、`wave-to-like`、`like-to-cute`、`cute-to-think`、`think-to-idle` 九张过渡资源。全部运行时资源均为 `900×1100 RGBA` 透明 PNG，人物底部锚点统一，并使用高质量缩放。

## 运行

已发布程序位于 `dist\LubanDesktopPet.exe`，适用于 Windows 11 x64，需要 .NET 8 Desktop Runtime。当前 EXE 未做商业代码签名，首次从网络下载运行时 Windows SmartScreen 可能会显示提示。

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
