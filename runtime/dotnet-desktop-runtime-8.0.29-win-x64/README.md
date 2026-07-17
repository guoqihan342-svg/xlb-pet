# .NET 8 Desktop Runtime 安装包

本目录保存小鲁班桌面宠物所需的微软官方 Windows x64 安装包，文件未经修改。

- 版本：`.NET Desktop Runtime 8.0.29`
- 文件：`windowsdesktop-runtime-8.0.29-win-x64.exe`
- 架构：Windows x64
- 大小：58,699,856 字节（约 55.98 MiB）
- 官方来源：<https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.29/windowsdesktop-runtime-8.0.29-win-x64.exe>
- SHA-512：`02d272ee678f5bc8be522b0b8adaf2a3c9d35d1044d7737ea48e477928a839ad5344724b242b210afdc64775d8cf43655db58396fcf53dd185e23ba2b888ec44`
- Windows 数字签名：有效；签名者为 Microsoft Corporation（`.NET`）

双击安装包并按照提示完成安装，然后运行项目根目录 `dist\LubanDesktopPet.exe`。

如需重新校验文件：

```powershell
Get-FileHash .\windowsdesktop-runtime-8.0.29-win-x64.exe -Algorithm SHA512
Get-AuthenticodeSignature .\windowsdesktop-runtime-8.0.29-win-x64.exe
```
