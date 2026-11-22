# PDF Desk 基础组件规范

下表中的名称对应现有 WinUI 资源或组件。路径从 `windows/src/PDFDesk.App/` 起算。尺寸单位为 DIP，截图里的物理像素随显示缩放变化。

## 布局与主题

| 用途 | 复用方式 |
|---|---|
| 间距与外形 | `Styles/Tokens.xaml` 集中定义新的公共尺寸。间距 `SpaceXS/S/M/L/XL` 分别为 4/8/12/16/24。它们是 Double，可用于 Spacing；Margin、Padding 等 Thickness 属性参照同场景资源类型。普通圆角按角色选 `RadiusSmall`（4）、`RadiusMedium`（8）、`FieldRadius`（6）。 |
| 阅读工具栏 | `HeaderHeight` 44、`IconButtonSize` 34、`IconGlyphSize` 16；组间用 `ReaderToolbarGroupGap` 10，组容器用 `ReaderToolbarRadius` 12。独立浮动操作参考 `TouchTargetMin` 40。紧凑工具键与独立触控目标分别取值。 |
| 表面与文字 | 导航用 `HomeNavigationBrush`，书架画布、内层和卡片对应 `HomeCanvasBrush`、`HomeShelfBrush`、`HomeCardBrush`；阅读工作区用 `ReaderWorkspaceBrush`。封面框查 `HomeCoverStyle`。随主题变化的画刷用 ThemeResource，样式和尺寸按原调用用 StaticResource。 |
| 字体与信息层级 | 标题沿用相邻区域的系统文本样式，书架标题查 `HomeHeadingStyle`；说明文字、辅助信息、输入错误分别查 `SecondaryTextStyle`、`TertiaryTextStyle`、`ValidationErrorTextStyle`。正文和标签继承 WinUI 字体。中英文文案来自 `UiText`，为长文件名、翻译和缩放留出空间。 |

## 按行为选控件

| 需要的行为 | 组件与约定 | 定位 |
|---|---|---|
| 执行一次命令 | 普通文字操作用 Button，主要提交用 AccentButtonStyle；书架打开入口用 HomeBrowseButtonStyle 和 HomeBrowseButtonContent。紧凑图标命令用 IconButtonStyle，图标用已有 Symbol、FontIcon 或项目图标模板。 | Styles/Home.xaml、Styles/Controls.xaml |
| 保持一种开关状态 | 用 ToggleButton，紧凑工具用 IconToggleButtonStyle，IsChecked 与实际工具状态同步。收藏用 ShelfFavoriteToggleStyle 并沿用既有卡片模板中的位置及普通切换语义。 | Styles/Controls.xaml、Styles/Home.xaml、MainWindow.xaml |
| 多个选项互斥 | 使用同组 RadioButton。书架最近/收藏用 ShelfSectionStyle；网格/列表用 ShelfViewSegmentStyle；页面布局用 PageLayoutSegmentStyle；侧栏页签用 SidebarTabStyle。保留各模板自己的选中反馈和 GroupName 分组。 | Styles/Home.xaml、Styles/Controls.xaml |
| 多选与文档标签 | CheckBox 默认继承 RoundedCheckBoxStyle，书架有 ShelfSelectionCheckBox；核对单击、进入多选、选中数量和取消选择。文档切换沿用 TabViewItem 的 DocumentTabTemplate，保留关闭入口与选中态边界。 | App.xaml、MainWindow.xaml、MainWindow.Shelf.cs |
| 工具组与浮动命令 | ReaderSideGroupStyle 用于侧组容器；侧组按钮用 ReaderSideToolButtonStyle；浮动缩放按钮用 ZoomControlButtonStyle。分隔使用 HeaderSeparatorStyle 或 FlyoutSeparatorStyle。局部工具栏按现有宿主合并 ReaderToolbarFeedback.xaml，让悬停、按下反馈只影响该区域。 | Styles/Controls.xaml、MainWindow.xaml、Controls/PageOrganizer.xaml |
| 文本输入 | 普通 TextBox 自动继承 AppTextFieldStyle。字段底色 FieldSurfaceBrush、边线 FieldOutlineBrush、圆角 FieldRadius；最小高度 40，内边距 10,6。名称或说明可见，错误贴近字段显示，输入有效后启用提交。PDF 页面内的文字编辑另查 PageTextEditorStyle。 | App.xaml、Controls/FolderNameDialog.xaml、Styles/Controls.xaml |
| 数值与选项输入 | 确切页码参照 PageBox 的 NumberBox 与范围处理；离散选项参照签名的 ComboBox；连续笔宽参照笔设置中的 Slider 及当前值读数。保持键盘输入和快捷键之间的焦点边界。 | MainWindow.xaml、MainWindow.Keyboard.cs、MainWindow.Tools.cs、Controls/SignaturePad.xaml |
| 菜单与就地面板 | 动作列表沿用 MenuFlyout/MenuFlyoutItem。需要选择颜色、笔宽等参数时沿用 PenPanelPresenterStyle、InkSwatchButtonStyle、InkWidthButtonStyle。快捷键提示可复用 ShortcutKeyChipStyle，显示的按键须与实际快捷键一致。 | MainWindow.xaml、MainWindow.Tools.cs、Styles/Controls.xaml |
| 对话框 | 需要明确决定时，以调用控件为 owner，通过 DialogHost.ShowAsync(owner, dialog) 显示。内置按钮读取 ContentDialogResult，自定义底栏读取该窗的明确提交标志。选型、布局和键盘约定见[弹窗设计规范](dialogs.md)。 | Services/DialogHost.cs、Controls/FolderNameDialog.xaml |
| 通知与进度 | 短暂结果、警告和可选后续动作用 NoticeHost，沿用其语义图标、关闭按钮与操作区。导出过程参照 ExportProgressCard；字段校验就地显示。 | Controls/NoticeHost.xaml、Controls/ExportProgressCard.xaml |
| 危险操作 | 删除文件、页面、批注、书签、签名、文件夹，清除内容和放弃修改均保留中性底色，图标与文字用系统危险色。保留原按钮或菜单布局。XAML 加附加属性，动态菜单调用 SetIsEnabled；ContentDialog 内置动作按钮用 DestructiveDialogButtonStyle。确认写明动作，取消保持中性，禁用态与高对比度采用系统配色。 | Styles/DestructiveAction.xaml、Styles/Controls.xaml、MainWindow.Favorites.cs |

## 直接套用

**普通命令与危险变体。** 下面是删除批注按钮的外观与语义片段。所在视图声明 `xmlns:styles="using:PDFDesk.Styles"`，将 Click 或 Command 接到该视图已有的删除入口。普通命令换成本地化名称及对应图标，并移除危险附加属性。

```xml
<Button Style="{StaticResource IconButtonStyle}"
        styles:DestructiveAction.IsEnabled="True"
        AutomationProperties.Name="{Binding DeleteNote, Source={StaticResource UiText}}"
        ToolTipService.ToolTip="{Binding DeleteNote, Source={StaticResource UiText}}">
    <FontIcon Glyph="&#xE74D;" FontSize="{StaticResource IconGlyphSize}" />
</Button>
```

**同一动作出现在动态菜单或确认框。** 复用原有菜单项，只为对应的危险动作启用语义。若危险动作位于对话框次按钮，将样式赋给 SecondaryButtonStyle。

```csharp
PDFDesk.Styles.DestructiveAction.SetIsEnabled(menuItem, true);
dialog.PrimaryButtonStyle =
    (Style)Application.Current.Resources["DestructiveDialogButtonStyle"];
```

**新增一个命名表单。** 从 Controls/FolderNameDialog.xaml 复用标题、编辑区、中性取消与主提交的排列。普通 TextBox 直接使用应用隐式样式。绑定输入名称、验证结果和提交可用性；打开、取消、确认沿用该对话框的事件处理和 DialogHost。

## 复用核对

定位样式时先看 App.xaml 的合并字典，再看宿主局部资源。若找不到某个键，在当前分支按同类控件的实际调用更新引用。抽取公共样式时保留 TargetType、原模板的可访问名称、工具提示和键盘焦点。按组件角色检查普通、悬停、按下、禁用，以及有状态控件的选中态；确认焦点和选中可以分别辨认。新增语义画刷时对照 Light、Dark、HighContrast 三套资源。
