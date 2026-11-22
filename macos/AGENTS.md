# PDF Desk macOS 开发约定

使用 Xcode 打开 `PDFDesk.xcodeproj`。应用入口在 `PDFDesk/PDFDeskApp.swift`，当前使用 SwiftUI 的文档窗口和 PDFKit 阅读视图。

在 macOS 上运行 `xcodebuild -project PDFDesk.xcodeproj -target PDFDesk -configuration Debug build CODE_SIGNING_ALLOWED=NO` 验证构建，再用 Xcode 运行应用并打开 [hello.pdf](../tests/corpus/hello.pdf)。扩展编辑与保存功能前，参照[跨端需求与文件格式](../docs/macOS版产品需求与实现规格.md)选择相应的往返验收用例。
