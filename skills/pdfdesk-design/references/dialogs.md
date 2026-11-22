# 弹窗设计规范

从用户打开弹窗后要完成的动作选择模板。以下路径相对 `windows/src/PDFDesk.App/`。必须先作决定才能继续的任务用 ContentDialog；笔宽、颜色等就地参数沿用 Flyout，短暂操作结果沿用 NoticeHost。

## 选择现有模板

| 场景 | 标题、正文和操作 | 项目参照 |
|---|---|---|
| 查看信息 | 标题点明对象；正文承载可阅读、可复制的详情，底部提供「关闭」。仅在有实际用途时增加复制等辅助命令。 | Controls/DocumentPropertiesDialog.cs、Controls/KeyboardShortcutsDialog.cs |
| 新建或重命名 | 标题写明任务，打开时聚焦输入框，重命名可全选原文。错误贴近字段，输入有效才启用「创建」或「保存」并响应 Enter。取消时保留原值。 | Controls/FolderNameDialog.xaml 及其代码后置 |
| 选择收藏位置 | 标题下显示当前文档，中间列表独立滚动，底部操作保持可见。新建文件夹的输入区在当前弹窗内展开。主要操作为「保存」，可逆的「取消收藏」用普通次要按钮。 | Controls/FavoritePickerDialog.xaml |
| 删除或移至回收站 | 简单确认在正文点名对象和后果；批量确认显示数量，并用可滚动列表列出文件名与目录。按钮写「删除」或「移至回收站」。内置按钮将默认项设为 Close；自定义底栏打开时聚焦「取消」。 | MainWindow.Favorites.cs、Controls/RecycleFilesDialog.xaml 及其代码后置 |
| 关闭未保存的文档 | 显示受影响的文件。「保存」「全部保存」或「保存副本」为默认主操作，「放弃修改」为危险次操作，「取消」返回文档。原路径失效时沿用项目的保存副本流程。 | MainWindow.Documents.cs 中的 UnsavedDocumentsTemplate 调用 |
| 带预览的任务 | 按文件、配置、预计结果、目标位置组织内容。计算状态和进度放在结果附近，提交写具体任务名称。长路径可截断，保留查看或选择目标的入口。 | Controls/CompressionDialog.xaml |

## 外观和尺寸

- 简单确认优先用原生 Title、Content 和内置按钮行。专用窗沿用 DefaultContentDialogStyle；有自定义标题和底栏时，在该窗局部将 ContentDialogPadding 设为 0，由内容根布局提供一层 24 DIP 内边距。
- 自定义标题沿用 SubtitleTextBlockStyle 和 HeadingLevel=Level2。标题下的文件名、路径或解释使用次要文字画刷。常规正文直接排在弹窗表面，文件清单、计算结果等需要独立分组时再用项目现有的容器样式。
- 自定义窗常用 SpaceL（16 DIP）分隔标题、内容和操作区，SpaceS（8 DIP）分隔按钮。命名窗和收藏选择器使用 ReaderToolbarRadius（12 DIP）；压缩窗现有圆角为 8 DIP。沿用同类窗的尺寸资源。命名表单参考 FolderNameFormWidth（420 DIP），收藏选择器参考 FavoritePickerWidth（520 DIP）和 FavoritePickerMaxHeight（496 DIP）。这些尺寸分别服务于单字段输入与列表选择。
- 自定义底栏的「取消」与主要动作靠右排列，「取消」在前；独立辅助动作可放左侧。较长的正文或列表在中间区域滚动。检查小窗口、显示缩放、中英文长文案下按钮和校验信息是否仍可见。

## 操作、焦点与返回值

- 内置普通主按钮沿用系统强调样式；自定义表单用 AccentButtonStyle。危险按钮保留中性底色，图标和文字用系统危险色。按危险动作所在位置设置 PrimaryButtonStyle 或 SecondaryButtonStyle 为 DestructiveDialogButtonStyle；自定义 Button 用 styles:DestructiveAction.IsEnabled。取消保持中性。主题变化、禁用和高对比度沿用项目资源。
- 在 UI 线程通过 DialogHost.ShowAsync(owner, dialog) 或窗口已有的 ShowDialogAsync 包装显示。宿主负责正确的 XamlRoot、当前窗口主题及同一根窗口的弹窗互斥。需要第二个步骤时，在当前内容中展开，或等待第一个弹窗关闭再显示。
- 内置按钮根据 ContentDialogResult 分支，只有对应的明确选择才执行动作。**自定义底栏要读取专用结果**：FolderNameDialog 读取 Submitted，RecycleFilesDialog 读取 Confirmed。两者都用 Hide() 关闭，单凭宿主返回的 None 无法区分点击了哪个自定义按钮。标志初始为 false，仅明确提交时设为 true。
- 键盘核验从打开弹窗开始：输入类落在字段，危险确认落在取消；Tab 与 Shift+Tab 能到达操作区。确认 Enter 只触发当前合法动作，Esc 或取消不会提交。使用自定义标题时给弹窗设置可访问名称；动态状态参考压缩窗的 AutomationProperties.LiveSetting。
