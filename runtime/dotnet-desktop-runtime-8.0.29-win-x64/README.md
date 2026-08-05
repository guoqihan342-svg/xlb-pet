# .NET 8 Desktop Runtime 版本说明

小鲁班桌面宠物的框架依赖发布版需要 x64 `.NET 8 Desktop Runtime`。项目请求 `Microsoft.WindowsDesktop.App 8.0.0` 并使用 .NET 的标准兼容补丁滚动；`8.0.29` 是当前已验证和推荐的安装包，不表示只能安装这一精确补丁版本。

为避免把约 56 MiB 的微软安装包重复提交到 GitHub，仓库只跟踪本说明，不跟踪安装包 EXE。请按需从微软官方地址下载。

- 版本：`.NET Desktop Runtime 8.0.29`
- 文件：`windowsdesktop-runtime-8.0.29-win-x64.exe`
- 架构：Windows x64
- 大小：58,699,856 字节（约 55.98 MiB）
- 官方来源：<https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.29/windowsdesktop-runtime-8.0.29-win-x64.exe>
- SHA-512：`02d272ee678f5bc8be522b0b8adaf2a3c9d35d1044d7737ea48e477928a839ad5344724b242b210afdc64775d8cf43655db58396fcf53dd185e23ba2b888ec44`
- Windows 数字签名：有效；签名者为 Microsoft Corporation（`.NET`）

下载后双击安装包并按照提示完成安装，然后运行从目标 GitHub Release 下载的 `LubanDesktopPet.exe`。开发者本地的版本化输出位于 `dist\v<版本>\LubanDesktopPet.exe`；根目录 `dist\LubanDesktopPet.exe` 可能因旧进程仍在运行而暂时保留上一版本。

确认系统已经安装 Windows Desktop Runtime：

```powershell
dotnet --list-runtimes | Select-String 'Microsoft.WindowsDesktop.App 8\.'
```

如果 `dotnet` 命令不存在，也可以直接运行桌宠 EXE；Windows 的缺少框架提示会给出运行时安装入口。请只从微软官方站点或本文件记录的官方地址获取安装包。

如需重新校验文件：

```powershell
Get-FileHash .\windowsdesktop-runtime-8.0.29-win-x64.exe -Algorithm SHA512
Get-AuthenticodeSignature .\windowsdesktop-runtime-8.0.29-win-x64.exe
```

返回 [项目首页](../../README.md) 或查看 [用户安装说明](../../docs/USER_GUIDE.md)。
