# PDF Desk 开发约定

Windows 工程位于 windows/，macOS 工程位于 macos/。平台构建、启动与发布遵循对应子目录的 AGENTS.md。

修改或审查界面与交互时，读取并应用 [pdfdesk-design](skills/pdfdesk-design/SKILL.md)。危险操作采用中性底色及系统危险色图标、文字，取消操作保持中性。涉及用户文档时优先保留修改。

共享需求与测试记录位于 [docs](docs/)，跨端 PDF 验收样本位于 [tests/corpus](tests/corpus/)。
