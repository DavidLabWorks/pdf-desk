# 组合组件与交互规范

项目路径从 `windows/src/PDFDesk.App/` 起算。每节给出当前实现入口和复用时应保留的行为。组件内部的具体尺寸以对应 XAML 为准。

## 设置行

参照 `MainWindow.xaml` 的 SettingsPage 和 `MainWindow.Settings.cs`。沿用现有分组和两列排列：左侧名称与必要说明，右侧放当前值或动作。开／关用 ToggleSwitch（RestoreChoice），离散选项用 ComboBox（ThemeChoice），目录选择使用路径和浏览操作。控件提供对应的可访问名称；主题与语言支持「跟随系统」。

沿用当前设置页的即时保存。载入初值时用现有同步标志隔离变更事件，避免打开页面就写入设置。更换目录在验证成功后保存；返回时恢复仍存在的原标签，将焦点交还设置入口。

## 书架集合、封面占位与空态

参照 `MainWindow.xaml` 的 EmptyState、FavoriteEmptyTitle、ShowPlaceholder 绑定，以及 `Controls/ShelfGridView.cs`、`MainWindow.Shelf.cs`。有文件但封面未就绪时，在原卡片封面区域使用项目占位，保留文件名与可用操作。整个最近列表为空时，显示打开或拖入文件的入口；收藏为空时说明当前收藏范围。先确认当前列表和加载状态，再选择提示。

书架网格由 ShelfGridView 按可用宽度、滚动条和显示缩放决定列数。沿用其换列缓冲，调整窗口时检查最后一列与滚动条。进入批量选择后，操作针对所选文件；收藏按钮、选择框和打开文档各自有独立语义。

## 文内查找与命令面板

| 场景 | 外观、状态与键盘行为 | 复用入口 |
|---|---|---|
| 查 PDF 正文 | 在文档上显示紧凑横条。输入、命中计数、匹配大小写、前后命中、关闭依次排列；独立按钮使用 TouchTargetMin。打开时可用所选文字首行预填并全选。Enter / Shift+Enter 遍历，Esc 关闭。新查询取代旧查询，关闭时清掉高亮；搜索完成且无命中后才显示无结果。 | Controls/DocumentFindBar.xaml 及其代码后置 |
| 找命令或跳页 | 在窗口上方居中显示输入框和单选结果列表，命令名靠左，快捷键靠右。用 PaletteCommand 提供标签、按键和执行动作；纯数字交给页码命令工厂处理。上下键切换，Enter 执行，Esc 收起。新增快捷键同步核对实际按键处理和快捷键清单。 | Controls/CommandPalette.xaml、Controls/CommandPalette.xaml.cs、MainWindow.Keyboard.cs、Controls/KeyboardShortcutsDialog.cs |

## 阅读侧栏与页面整理

侧栏用于**定位**，沿用 `Controls/DocumentView.xaml` 的三类内容：页面显示按纸张比例生成的缩略图，章节用 TreeView 表示层级，书签列表支持跳转和管理。书签为空时显示项目现有的添加提示。保留系统焦点、当前项反馈和虚拟化缩略图加载。检查无章节、无书签、横向页面及长文档。

整理器用于**编辑一组页面**，复用 `Controls/PageOrganizer.xaml` 及其代码后置。GridView 显式多选；拖动时用叠页预览表示所选页，插入线表示将落在目标页之前或之后。拖离、取消、完成时清理拖放反馈。提交走已有编辑路径，同步页序、选区和书签映射。验证非连续选页、首尾落点与取消拖动。

## 短通知与持续任务

- **短通知**：复用 `Controls/NoticeHost.xaml` 及其代码后置。严重程度决定图标和语义色，正文说明结果，操作按钮仅在有可执行后续动作时出现。沿用宽度收窄时的纵向按钮布局，以及悬停或焦点位于通知内时暂停计时的行为。
- **持续任务**：复用 `Controls/ExportProgressCard.xaml`、`Controls/ExportProgressCard.xaml.cs` 与 `MainWindow.Compression.cs`。卡片保留文件名、完整路径提示、阶段和取消入口。准备阶段用不定进度，有进度上报后显示百分比，随后显示校验与保存。点击取消立即停用按钮、显示「正在取消」，由任务的最终结果决定后续反馈。

## 签名预览与放映

- **签名编辑区**：参照 `Controls/SignaturePad.xaml` 及其代码后置。手绘、键入、图片共用一个纸面预览区；根据当前模式显示笔色与清除、字体选择或图片去背景选项。提示文字避开指针命中。纸面预览与外围主题控件分别检查，清除笔迹沿用危险动作语义。
- **放映画布**：参照 `Controls/PresentationView.xaml` 及其代码后置。进入时从当前页开始，页面随窗口尺寸适配。保留右下退出入口以及现有指针翻页路径。检查首页、末页、窗口改变尺寸及退出后阅读位置的衔接。
