# PDF Desk 开发约定

每次用户要求 build（构建）时，先将应用三段版本号的最后一位加 1，例如 `0.0.9 → 0.0.10`。同步更新 `PDFDesk.App.csproj` 的 `Version` 和 `Package.appxmanifest` 的 `Identity.Version`（如 `0.0.10.0`）。同一次请求中的重试、多架构构建和安装包共用这次的新版本号。

每次完成应用修改后，先成功编译与当前设备架构匹配的新版，再正常退出旧版 PDF Desk 并启动刚编译的程序。确认只有一个 PDF Desk 进程，且其可执行文件路径与本次构建一致。如果退出时提示有未保存的文档，让用户在窗口中处理，再继续重启。

Windows 安装包统一命名为 `PDFDesk-Setup-<版本号>-<架构>.exe`。使用待打包程序的三段版本号，架构填写 `x64` 或 `arm64`，例如 `PDFDesk-Setup-0.0.7-arm64.exe`。
