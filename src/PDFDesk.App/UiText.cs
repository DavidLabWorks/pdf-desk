using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;

namespace PDFDesk;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class UiText : INotifyPropertyChanged
{
    public static UiText Current => (UiText)Application.Current.Resources["UiText"];
    public string LanguageTag { get; private set; } = "en";
    private bool Chinese => LanguageTag == "zh-CN";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetLanguage(string preference)
    {
        var language = preference == "System"
            ? Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? "en"
            : preference;
        LanguageTag = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(LanguageTag);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public string LibraryStorage => Chinese ? "文件存储" : "File storage";
    public string FavoriteFoldersLabel => Chinese ? "收藏文件夹" : "Favorite folders";
    public string FolderNameExample => Chinese ? "例如：阅读资料" : "e.g. Reading list";
    public string AllFavorites => Chinese ? "全部收藏" : "All favorites";
    public string UnfiledFavorites => Chinese ? "未归类" : "Unfiled";
    public string NewFavoriteFolder => Chinese ? "新建文件夹" : "New folder";
    public string FavoriteOptions => Chinese ? "收藏选项" : "Favorite options";
    public string RenameFolder => Chinese ? "重命名文件夹" : "Rename folder";
    public string DeleteFolder => Chinese ? "删除文件夹" : "Delete folder";
    public string FolderName => Chinese ? "文件夹名称" : "Folder name";
    public string FolderNameTaken => Chinese ? "已有同名文件夹，请换一个名称。" : "A folder already has this name. Choose another.";
    public string SaveToFolder => Chinese ? "收藏到文件夹" : "Save to folder";
    public string MoveFavorite => Chinese ? "移动收藏" : "Move favorite";
    public string FavoriteDirectly => Chinese ? "直接收藏" : "Save without a folder";
    public string EmptyFavoriteFolder => Chinese ? "这里还没有收藏" : "No favorites here yet";
    public string EmptyFolderHint => Chinese ? "点击 PDF 的星标，即可将它收藏到这里。" : "Select the star on a PDF to save it here.";
    public string ConfirmDeleteFolder(string name) => Chinese
        ? $"删除「{name}」后，其中的收藏将移至「未归类」。"
        : $"Favorites in “{name}” will move to Unfiled when you delete this folder.";
    public string LibraryFolderLabel => Chinese ? "书架文件夹" : "Library folder";
    public string LibraryFolderHint => Chinese ? "新建或复制到书架的 PDF 将保存在这里。" : "Blank PDFs and files copied to the library are saved here.";
    public string ChangeFolder => Chinese ? "更改文件夹" : "Change folder";
    public string ResetFolder => Chinese ? "恢复默认位置" : "Restore default location";
    public string LibraryFolderError => Chinese ? "无法使用此文件夹" : "Could not use this folder";

    public string ActualSize => Chinese ? "实际大小" : "Actual size";
    public string Add => Chinese ? "添加" : "Add";
    public string AddNote => Chinese ? "添加便签" : "Add note";
    public string AddToFavorites => Chinese ? "添加到收藏" : "Add to favorites";
    public string Appearance => Chinese ? "外观" : "Appearance";
    public string Apply => Chinese ? "应用" : "Apply";
    public string Back => Chinese ? "返回" : "Back";
    public string Black => Chinese ? "黑色" : "Black";
    public string BlackInk => Chinese ? "黑色墨水" : "Black ink";
    public string Blue => Chinese ? "蓝色" : "Blue";
    public string BlueInk => Chinese ? "蓝色墨水" : "Blue ink";
    public string BookmarkThisPage => Chinese ? "为此页添加书签" : "Bookmark this page";
    public string Bookmarks => Chinese ? "书签" : "Bookmarks";
    public string Cancel => Chinese ? "取消" : "Cancel";
    public string Chapters => Chinese ? "章节" : "Chapters";
    public string Clear => Chinese ? "清除" : "Clear";
    public string Close => Chinese ? "关闭" : "Close";
    public string CloseEsc => Chinese ? "关闭 (Esc)" : "Close (Esc)";
    public string CloseFindBar => Chinese ? "关闭查找栏" : "Close find bar";
    public string CloseTab => Chinese ? "关闭标签页" : "Close tab";
    public string Colour => Chinese ? "颜色" : "Colour";
    public string CopyFilePath => Chinese ? "复制文件路径" : "Copy file path";
    public string FilePathCopied => Chinese ? "已复制文件路径" : "File path copied";
    public string CopyToLibrary => Chinese ? "复制到书架" : "Copy to library";
    public string CopyingToLibrary => Chinese ? "正在复制到书架…" : "Copying to library…";
    public string CreateBlankPdf => Chinese ? "创建空白 PDF" : "Create blank PDF";
    public string Create => Chinese ? "创建" : "Create";
    public string DocumentName => Chinese ? "文档名称" : "Document name";
    public string SaveInLibrary => Chinese ? "保存到书架文件夹" : "Save to the library";
    public string InvalidDocumentName => Chinese ? "请输入有效的文件名，去掉特殊字符或缩短名称。" : "Enter a valid file name. Remove special characters or shorten it.";
    public string BlankPdfCreationFailed(string message) => Chinese ? $"创建失败。{message}" : $"Could not create the PDF. {message}";
    public string Dark => Chinese ? "深色" : "Dark";
    public string Delete => Chinese ? "删除" : "Delete";
    public string CompressExport => Chinese ? "压缩导出" : "Export compressed PDF";
    public string DocumentProperties => Chinese ? "文档属性" : "Document properties";
    public string DiscardChanges => Chinese ? "放弃修改" : "Discard";
    public string SaveAllChanges => Chinese ? "全部保存" : "Save all";
    public string DragAFileHereToOpenIt => Chinese ? "可将 PDF 拖到这里打开。" : "Drag a PDF here to open it.";
    public string SignatureMethod => Chinese ? "签名方式" : "Signature method";
    public string SignatureDraw => Chinese ? "手写" : "Draw";
    public string SignatureType => Chinese ? "输入姓名" : "Type name";
    public string SignatureImage => Chinese ? "图片" : "Image";
    public string ChooseSignatureImage => Chinese ? "选择图片" : "Choose image";
    public string SignHere => Chinese ? "在此签名" : "Sign here";
    public string DrawTogglePen => Chinese ? "切换画笔" : "Draw (toggle pen)";
    public string DrawYourSignatureBelowTypeItOrImportAPhotoOfOne => Chinese ? "在下方手写签名，也可以输入姓名或导入签名图片。" : "Draw your signature below, type it, or import a photo of one.";
    public string EditTheFileInPlace => Chinese ? "直接编辑原位置的文件" : "Edit the file in place";
    public string Eraser => Chinese ? "橡皮擦" : "Eraser";
    public string EraserHint => Chinese ? "沿笔迹拖动以擦除" : "Drag over ink to erase";
    public string EraserWidth => Chinese ? "擦除宽度" : "Width";
    public string EraserTool => Chinese ? "橡皮擦工具" : "Eraser tool";
    public string ExtractSelectedPagesToANewFile => Chinese ? "将选中页面提取为新文件…" : "Extract selected pages to a new file…";
    public string Favorites => Chinese ? "收藏" : "Favorites";
    public string ListView => Chinese ? "列表视图" : "List view";
    public string CardView => Chinese ? "卡片视图" : "Card view";
    public string PageLayout => Chinese ? "页面布局" : "Page layout";
    public string ReaderDisplayOptions => Chinese ? "页面显示" : "Page display";
    public string ReaderReadingOptions => Chinese ? "阅读辅助" : "Reading aids";
    public string SingleColumnLabel => Chinese ? "单列" : "Single";
    public string DoubleColumnLabel => Chinese ? "双列" : "Double";
    public string TripleColumnLabel => Chinese ? "三列" : "Triple";
    public string PageBookmarkLabel => Chinese ? "本页书签" : "Bookmark";
    public string ViewOptions => Chinese ? "查看选项" : "View options";
    public string ViewActionsLabel => Chinese ? "查看" : "View";
    public string CollapseQuickTools => Chinese ? "折叠阅读工具" : "Collapse reading tools";
    public string ExpandQuickTools => Chinese ? "展开阅读工具" : "Expand reading tools";
    public string DocumentActions => Chinese ? "文档操作" : "Document actions";
    public string DocumentActionsLabel => Chinese ? "文档" : "Document";
    public string Help => Chinese ? "帮助" : "Help";
    public string DocumentInformation => Chinese ? "文档信息" : "Document information";
    public string FileMovedOrRenamed => Chinese ? "文件已移动或重命名" : "File moved or renamed";
    public string FindACommand => Chinese ? "查找命令…" : "Find a command…";
    public string FindInDocument => Chinese ? "在文档中查找" : "Find in document";
    public string FindInDocumentCtrlF => Chinese ? "在文档中查找 (Ctrl+F)" : "Find in document (Ctrl+F)";
    public string FirstPage => Chinese ? "第一页" : "First page";
    public string FitPage => Chinese ? "适合页面" : "Fit page";
    public string FitPageCtrl0 => Chinese ? "适合页面 (Ctrl+0)" : "Fit page (Ctrl+0)";
    public string FitWidth => Chinese ? "适合宽度" : "Fit width";
    public string FitWidthCtrl2 => Chinese ? "适合宽度 (Ctrl+2)" : "Fit width (Ctrl+2)";
    public string Flatten => Chinese ? "合并" : "Flatten";
    public string FlattenAnnotations => Chinese ? "合并批注到页面…" : "Flatten annotations…";
    public string FlattenAnnotationsAction => Chinese ? "将批注合并到页面？" : "Flatten annotations?";
    public string FollowSystem => Chinese ? "跟随系统" : "Follow system";
    public string GoToPage => Chinese ? "跳转到页码" : "Go to page";
    public string GoToPageShortcut => $"{GoToPage} (Ctrl+G)";
    public string HandwritingStyle => Chinese ? "手写样式" : "Handwriting style";
    public string HighlightSelection => Chinese ? "高亮选中文本" : "Highlight selection";
    public string Highlighter => Chinese ? "荧光笔" : "Highlighter";
    public string HighlighterDragOverTextCtrlH => Chinese ? "荧光笔（Ctrl+H）：拖选要标记的文字" : "Highlighter (Ctrl+H): Drag across the text you want to mark.";
    public string HighlighterTool => Chinese ? "荧光笔工具" : "Highlighter tool";
    public string NewWindow => Chinese ? "新建窗口" : "New window";
    public string FinishExportBeforeClosing => Chinese ? "请等待导出完成或取消任务后再关闭窗口。" : "Finish or cancel the export before closing this window.";
    public string SourceChangedDuringMove => Chinese ? "移动期间原标签发生了编辑，请重试。" : "The source changed during the move. Please try again.";
    public string OpenInNewWindow => Chinese ? "在新窗口中打开" : "Open in new window";
    public string MoveTabToWindow => Chinese ? "移至新窗口" : "Move to new window";
    public string ExitPresentation => Chinese ? "退出演示" : "Exit presentation";
    public string Home => Chinese ? "主页" : "Home";
    public string EnterFocusReading => Chinese ? "进入专注阅读" : "Enter focus reading";
    public string ExitFocusReading => Chinese ? "退出专注阅读 (Esc)" : "Exit focus reading (Esc)";
    public string ImportImage => Chinese ? "导入图片…" : "Import image…";
    public string KeepASeparateCopy => Chinese ? "在书架中保留独立副本" : "Keep a separate copy";
    public string KeyboardShortcuts => Chinese ? "键盘快捷键" : "Keyboard shortcuts";
    public string Language => Chinese ? "界面语言" : "Language";
    public string LastPage => Chinese ? "最后一页" : "Last page";
    public string LastOpened => Chinese ? "最近打开时间" : "Last opened";
    public string MoreFileActions => Chinese ? "更多文件操作" : "More file actions";
    public string Startup => Chinese ? "启动和关闭" : "Startup and closing";
    public string About => Chinese ? "关于" : "About";
    public string AppVersionLabel => Chinese ? "版本" : "Version";
    public string CloseBehavior => Chinese ? "关闭窗口时" : "When closing the window";
    public string CloseToTray => Chinese ? "关闭到系统托盘" : "Close to system tray";
    public string ExitPdfDesk => Chinese ? "退出 PDF Desk" : "Exit PDF Desk";
    public string TrayOpenWindow => Chinese ? "打开主面板" : "Open main window";
    public string TrayOpenPdf => Chinese ? "打开 PDF…" : "Open PDF…";
    public string TrayExit => Chinese ? "退出" : "Exit";
    public string DefaultPdfAppLabel => Chinese ? "PDF 打开方式" : "Opening PDFs";
    public string DefaultPdfAppStatus => Chinese ? "PDF Desk 已是默认 PDF 应用。" : "PDF Desk is your default PDF app.";
    public string SetDefaultPdfApp => Chinese ? "设为默认 PDF 应用" : "Set as default PDF app";
    public string DefaultPdfAppHint => Chinese ? "在 Windows 默认应用设置中，将 .pdf 文件关联到 PDF Desk。" : "Choose PDF Desk for .pdf files in Windows Default apps settings.";
    public string DefaultPdfAppPromptTitle => Chinese ? "默认 PDF 阅读器" : "Default PDF reader";
    public string DefaultPdfAppPromptBody => Chinese ? "想用 PDF Desk 打开所有 PDF？在 Windows 设置中将 .pdf 的默认应用改为 PDF Desk。" : "To open PDFs in PDF Desk by default, choose PDF Desk for .pdf files in Windows Settings.";
    public string OpenWindowsSettings => Chinese ? "打开系统设置" : "Open Windows Settings";
    public string DontRemindAgain => Chinese ? "不再提醒" : "Don't remind me again";
    public string RemindLater => Chinese ? "稍后" : "Later";
    public string DefaultAppsSettingsFailed => Chinese ? "无法打开系统设置。可以在 Windows 的「设置 → 应用 → 默认应用」中手动选择 PDF Desk。" : "Could not open Windows Settings. Go to Settings → Apps → Default apps to select PDFDesk.";
    public string Light => Chinese ? "浅色" : "Light";
    public string LoadingPDF => Chinese ? "正在加载 PDF" : "Loading PDF";
    public string LoadingPDFAction => Chinese ? "正在加载 PDF…" : "Loading PDF…";
    public string LocatePDF => Chinese ? "定位 PDF" : "Locate PDF";
    public string MakeThePaperTransparentSoOnlyTheInkLandsOnThePage => Chinese ? "将纸张背景变为透明，仅保留笔迹" : "Make the paper transparent so only the ink lands on the page";
    public string MatchCase => Chinese ? "区分大小写" : "Match case";
    public string Maximize => Chinese ? "最大化" : "Maximize";
    public string Menu => Chinese ? "菜单" : "Menu";
    public string Minimize => Chinese ? "最小化" : "Minimize";
    public string NextEnter => Chinese ? "下一个 (Enter)" : "Next (Enter)";
    public string NextMatch => Chinese ? "下一个匹配项" : "Next match";
    public string NextPage => Chinese ? "下一页" : "Next page";
    public string NightMode => Chinese ? "夜间阅读" : "Night mode";
    public string NightModeInvertPageColorsCtrlI => Chinese ? "夜间阅读，反转页面颜色 (Ctrl+I)" : "Night mode — invert page colors (Ctrl+I)";
    public string NoBookmarksYet => Chinese ? "暂无书签" : "No bookmarks yet";
    public string NoFavoritesYet => Chinese ? "暂无收藏" : "No favorites yet";
    public string NoRecentFiles => Chinese ? "暂无最近文件" : "No recent files";
    public string NoResults => Chinese ? "无结果" : "No results";
    public string NoTableOfContents => Chinese ? "此文档没有目录" : "No table of contents";
    public string NoteClickToAddAStickyNote => Chinese ? "点击页面添加便签" : "Note — click to add a sticky note";
    public string DeleteNote => Chinese ? "删除便签" : "Delete note";
    public string EmptyNote => Chinese ? "这条便签没有内容。" : "This note is empty.";
    public string NoteText => Chinese ? "输入便签内容…" : "Note text…";
    public string Open => Chinese ? "打开…" : "Open…";
    public string OpenAPDFThenStarItInRecent => Chinese ? "打开 PDF 后，可在最近文件中点击星标收藏。" : "Open a PDF, then star it in Recent.";
    public string OpenCopyLocation => Chinese ? "打开副本位置" : "Open copy location";
    public string OpenFileLocation => Chinese ? "打开文件位置" : "Open file location";
    public string OpenInBrowser => Chinese ? "在浏览器中打开" : "Open in browser";
    public string OpenIssueTracker => Chinese ? "前往 GitHub 反馈" : "Report on GitHub";
    public string Feedback => Chinese ? "问题反馈" : "Feedback";
    public string OpenLink => Chinese ? "打开链接？" : "Open link?";
    public string OpenOriginal => Chinese ? "打开原文件" : "Open original";
    public string OpenOrCreatePdf => Chinese ? "打开或新建 PDF" : "Open or create PDF";
    public string OpenOrCreatePdfShortcut => $"{OpenOrCreatePdf} (Ctrl+O)";
    public string OrTypeYourName => Chinese ? "或输入姓名" : "Or type your name";
    public string PageCount => Chinese ? "共 {0} 页" : "of {0}";
    public string PageNumber => Chinese ? "页码" : "Page number";
    public string SidebarPages => Chinese ? "页面" : "Pages";
    public string PageThumbnails => Chinese ? "页面缩略图" : "Page thumbnails";
    public string Pen => Chinese ? "画笔" : "Pen";
    public string PenDrawFreehandCtrlE => Chinese ? "自由绘制 (Ctrl+E)" : "Pen — draw freehand (Ctrl+E)";
    public string Picture => Chinese ? "图片" : "Picture";
    public string PicturePlaceAnImageOnThePageCtrlWheelResizesIt => Chinese ? "插入图片（Ctrl+滚轮调整大小）" : "Picture — place an image on the page (Ctrl+wheel resizes it)";
    public string PlaceAPicture => Chinese ? "插入图片…" : "Place a picture…";
    public string Presentation => Chinese ? "演示" : "Presentation";
    public string PresentationShortcut => $"{Presentation} (F5)";
    public string PresentationMode => Chinese ? "演示模式" : "Presentation mode";
    public string PressCtrlBToBookmarkThisPage => Chinese ? "按 Ctrl+B 为此页添加书签" : "Press Ctrl+B to bookmark this page";
    public string PreviousMatch => Chinese ? "上一个匹配项" : "Previous match";
    public string PreviousPage => Chinese ? "上一页" : "Previous page";
    public string PreviousShiftEnter => Chinese ? "上一个 (Shift+Enter)" : "Previous (Shift+Enter)";
    public string Print => Chinese ? "打印…" : "Print…";
    public string PrintAction => Chinese ? "打印" : "Print";
    public string Recent => Chinese ? "最近" : "Recent";
    public string Redo => Chinese ? "重做" : "Redo";
    public string RemoveBackground => Chinese ? "移除背景" : "Remove background";
    public string RemoveFromFavorites => Chinese ? "取消收藏" : "Remove from favorites";
    public string Unfavorite => Chinese ? "取消收藏" : "Unfavorite";
    public string RemoveFromList => Chinese ? "从列表中移除" : "Remove from list";
    public string RemoveThePaperBackground => Chinese ? "移除纸张背景" : "Remove the paper background";
    public string Rename => Chinese ? "重命名" : "Rename";
    public string ReopenTabsAtStartup => Chinese ? "启动时恢复上次打开的标签页" : "Reopen tabs at startup";
    public string ReportAProblem => Chinese ? "报告问题…" : "Report a problem…";
    public string ReportAProblemAction => Chinese ? "报告问题" : "Report a problem";
    public string Restore => Chinese ? "还原" : "Restore";
    public string RotateClockwise => Chinese ? "顺时针旋转" : "Rotate clockwise";
    public string RotateLeft => Chinese ? "向左旋转" : "Rotate left";
    public string RotateLeftCtrlShiftR => Chinese ? "向左旋转 (Ctrl+Shift+R)" : "Rotate left (Ctrl+Shift+R)";
    public string RotateRight => Chinese ? "向右旋转" : "Rotate right";
    public string RotateRightCtrlR => Chinese ? "向右旋转 (Ctrl+R)" : "Rotate right (Ctrl+R)";
    public string Save => Chinese ? "保存" : "Save";
    public string SaveAs => Chinese ? "另存为…" : "Save as…";
    public string SaveAsAction => Chinese ? "另存为…" : "Save As…";
    public string SelectTheStarOnAPDFInRecent => Chinese ? "点击最近文件右上角的星标即可收藏。" : "Select the star on a PDF in Recent.";
    public string Settings => Chinese ? "设置" : "Settings";
    public string CopyPdfFile => Chinese ? "复制 PDF 文件" : "Copy PDF File";
    public string PdfFileCopied => Chinese ? "PDF 已复制" : "PDF copied";
    public string PdfFileCopiedMessage => Chinese ? "可粘贴为附件。" : "You can paste it as an attachment.";
    public string ShowSidebar => Chinese ? "显示侧栏" : "Show sidebar";
    public string ShowSidebarF9 => Chinese ? "显示侧栏 (F9)" : "Show sidebar (F9)";
    public string Sign => Chinese ? "签名" : "Sign";
    public string SignPlaceYourSignature => Chinese ? "在页面上放置签名" : "Sign — place your signature";
    public string SavedSignatures => Chinese ? "已保存的签名" : "Saved signatures";
    public string NewSignatureAction => Chinese ? "新建签名" : "New signature";
    public string ImportSignatureAction => Chinese ? "导入图片" : "Import image";
    public string SignaturePlacementHint => Chinese
        ? "点击放置签名，或拖出放置区域。按住 Ctrl 滚动可调整大小。"
        : "Click to place, or drag to set the size. Use Ctrl+wheel to resize.";
    public string SignatureDetails => Chinese ? "签名详情…" : "Signature details…";
    public string SinglePageView => Chinese ? "单页视图" : "Single-page view";
    public string Size => Chinese ? "大小" : "Size";
    public string StickyNote => Chinese ? "便签" : "Sticky note";
    public string Text => Chinese ? "文本" : "Text";
    public string TextAppearance => Chinese ? "文字外观" : "Text appearance";
    public string TextCommitFailed => Chinese ? "无法将这段文字写入 PDF。输入内容已保留在文本框中。" : "Could not write the text to the PDF. Your text is still in the editor.";
    public string TextClickAnywhereOnThePageAndTypeCtrlT => Chinese ? "点击页面输入文字 (Ctrl+T)" : "Text — click anywhere on the page and type (Ctrl+T)";
    public string SaveBeforeClosing => Chinese ? "关闭前保存修改？" : "Save changes before closing?";
    public string DiscardChangesHint => Chinese ? "放弃后，本次未保存的修改将丢失。" : "Discarding will remove your unsaved changes.";
    public string Theme => Chinese ? "主题" : "Theme";
    public string Thumbnails => Chinese ? "缩略图" : "Thumbnails";
    public string ToggleNightMode => Chinese ? "切换夜间阅读" : "Toggle night mode";
    public string ToggleSidebar => Chinese ? "切换侧栏" : "Toggle sidebar";
    public string ToggleSidebarF9 => $"{ToggleSidebar} (F9)";
    public string SaveCtrlS => $"{Save} (Ctrl+S)";
    public string TwoPageView => Chinese ? "双页视图" : "Two-page view";
    public string ThreePageView => Chinese ? "三列视图" : "Three-column view";
    public string TypeACommandOrPageNumber => Chinese ? "输入命令或页码…" : "Type a command or page number…";
    public string TypeOnThePage => Chinese ? "在页面上输入文字" : "Type on the page";
    public string Undo => Chinese ? "撤销" : "Undo";
    public string ZoomIn => Chinese ? "放大" : "Zoom in";
    public string ZoomInCtrl => Chinese ? "放大 (Ctrl++)" : "Zoom in (Ctrl++)";
    public string ZoomLevel => Chinese ? "缩放比例" : "Zoom level";
    public string ZoomOut => Chinese ? "缩小" : "Zoom out";
    public string ZoomOutCtrl => Chinese ? "缩小 (Ctrl+-)" : "Zoom out (Ctrl+-)";

    public string EnvironmentDetails => Chinese ? "运行环境" : "Environment";
    public string CopyDetails => Chinese ? "复制信息" : "Copy details";
    public string OpenLogFolder => Chinese ? "打开日志文件夹" : "Open log folder";

    public string ReportProblemInstructions => Chinese
        ? "在 GitHub 描述问题和复现步骤，可附上以下信息。诊断数据仅保存在本机。"
        : "Describe the issue and how to reproduce it on GitHub. You can include the details below. Diagnostics stay on this device.";

    public string FlattenAnnotationsExplanation => Chinese
        ? "高亮、便签、笔迹和已填写的表单字段将成为页面内容。合并后无法编辑、移动或删除，也无法撤销。\n\n保存文档后，改动才会写入文件。"
        : "Highlights, notes, ink and filled form fields become part of the page. "
            + "They can no longer be edited, moved or deleted, and this cannot be undone.\n\n"
            + "The change is not written to disk until you save.";

    public string SignatureVerificationExplanation => Chinese
        ? "这些信息来自文档本身。PDF Desk 不验证签名，也未检查证书、签署者身份和吊销状态。请使用 Adobe Acrobat 或专用验证工具确认签名是否有效。"
        : "PDF Desk reports what the document claims. It does not verify signatures — "
            + "the certificate, the signer's identity and revocation status are all unchecked. "
            + "Use Adobe Acrobat or a dedicated validator to confirm a signature is genuine.";

    public string SignatureBackgroundUnchanged => Chinese
        ? "未在照片中识别出笔迹，已保留原背景。请尝试使用光线更亮、拍得更平整的照片。"
        : "Nothing in that photo looks like ink, so the background was left alone. "
            + "A brighter, flatter shot usually fixes it.";

    // Runtime labels from the command catalog, PDF property groups and native flyouts.
    public static string Translate(string label) => Current.Chinese ? label switch
    {
        "Shortcut category" => "快捷键分类",
        "Highlighter / highlight selection" => "切换荧光笔 / 高亮选区",
        "Bold / italic" => "粗体 / 斜体",
        "Leave form field" => "退出表单编辑",
        "Text box" => "文本框",
        "Form field" => "表单字段",
        "MSIX package" => "安装包",
        "Development build" => "开发版",
        "Build ID" => "构建标识",
        "App / system" => "程序 / 系统架构",
        "Copied" => "已复制",
        "Retry copy" => "重新复制",
        "Attach errors.log when reporting a crash." => "反馈崩溃时，可附上 errors.log。",
        "No error log yet." => "暂无错误日志。",
        "Edit text" => "编辑文字",
        "Navigation" => "导航",
        "Scroll" => "滚动",
        "Previous / next page" => "上一页 / 下一页",
        "Screen up / down" => "向上 / 向下翻屏",
        "Screen down / up" => "向下 / 向上翻屏",
        "First / last page" => "首页 / 末页",
        "Back / forward" => "后退 / 前进",
        "Go to page" => "跳转到页码",
        "Bookmark current page" => "为当前页添加书签",
        "View" => "视图",
        "Zoom in / out" => "放大 / 缩小",
        "Page scaling" => "页面缩放",
        "Fit oversized pages" => "适合可打印区域（仅缩小）",
        "Actual size" => "实际大小",
        "Fit width / fit page" => "适合宽度 / 适合页面",
        "Rotate right / left" => "向右 / 向左旋转",
        "Night mode" => "夜间阅读",
        "Sidebar" => "侧栏",
        "Presentation" => "演示",
        "Find" => "查找",
        "Find in document" => "在文档中查找",
        "Next / previous match" => "下一个 / 上一个匹配项",
        "Annotate" => "批注",
        "Highlight selection" => "高亮选中文本",
        "Draw with the pen" => "用画笔绘制",
        "Type on the page" => "在页面上输入文字",
        "Resize a picture before dropping it" => "放置前调整图片大小",
        "Delete what you placed" => "删除已放置的对象",
        "Put the tool away" => "收起工具",
        "Copy selected text" => "复制选中文本",
        "Bold / italic / underline" => "粗体 / 斜体 / 下划线",
        "Bigger / smaller" => "增大 / 减小字号",
        "Left / centre / right / justify" => "左对齐 / 居中 / 右对齐 / 两端对齐",
        "Finish the box and free the shortcuts" => "结束编辑",
        "File & window" => "文件与窗口",
        "Open" => "打开",
        "Save / save as" => "保存 / 另存为",
        "Print" => "打印",
        "Document properties" => "文档属性",
        "Close tab" => "关闭标签页",
        "Command palette" => "命令面板",
        "Keyboard shortcuts" => "键盘快捷键",
        "Pages (thumbnail sidebar)" => "页面（缩略图侧栏）",
        "Select multiple pages" => "多选页面",
        "Ctrl+click / Shift+click" => "Ctrl+点击 / Shift+点击",
        "Copy / cut pages" => "复制 / 剪切页面",
        "Paste pages (works across tabs)" => "粘贴页面（支持跨标签页）",
        "Delete pages" => "删除页面",
        "Undo / redo" => "撤销 / 重做",
        "Ctrl+K, type a number" => "Ctrl+K，然后输入页码",
        "Ctrl+wheel" => "Ctrl+滚轮",
        "Description" => "描述",
        "Title" => "标题",
        "Author" => "作者",
        "Subject" => "主题",
        "Keywords" => "关键词",
        "Origin" => "来源",
        "Created" => "创建时间",
        "Modified" => "修改时间",
        "Created with" => "创建工具",
        "Produced by" => "生成工具",
        "Pages" => "页面",
        "Security" => "安全",
        "Features" => "功能",
        "Tagged" => "标签化 PDF",
        "Form" => "表单",
        "Attachments" => "附件",
        "File" => "文件",
        "Count" => "页数",
        "Note" => "备注",
        "Encrypted" => "加密",
        "Permissions" => "权限",
        "Name" => "名称",
        "Path" => "路径",
        "Size" => "大小",
        "Fonts" => "字体",
        "Font" => "字体",
        "Font size" => "字号",
        "PDF version" => "PDF 版本",
        "Location" => "位置",
        "Signature" => "签名",
        "Style" => "样式",
        "Opacity" => "不透明度",
        "Done" => "完成",
        "Highlight" => "高亮",
        "Underline" => "下划线",
        "Strikeout" => "删除线",
        "New signature…" => "新建签名…",
        "Add a signature" => "添加签名",
        "No saved signatures yet." => "暂无已保存的签名。",
        "Nothing was drawn, so there was no signature to save." => "请先绘制签名再保存。",
        "Import a signature image" => "导入签名图片",
        "Use this signature" => "使用此签名",
        "Delete this signature" => "删除此签名",
        "Rename bookmark" => "重命名书签",
        "That image could not be read." => "无法读取这张图片。",
        "Use a photo or scan of your signature; the paper is removed for you" => "使用签名照片或扫描件，自动移除纸张背景",
        "Show the log" => "查看日志",
        "Architecture" => "体系结构",
        "Insert PDF here…" => "在此处插入 PDF…",
        "Paste after" => "粘贴到所选页之后",
        "Copy {0}" => "复制{0}",
        "Cut {0}" => "剪切{0}",
        "Delete {0}" => "删除{0}",
        "Extract {0} to a new file…" => "将{0}提取为新文件…",
        "Paste {0} pages after" => "在所选页后粘贴 {0} 页",
        "page" => "页面",
        "{0} pages" => "{0} 页",
        "Something went wrong: {0}" => "操作失败：{0}",
        "Save failed: {0}" => "保存失败：{0}",
        "The PDF changed on disk. Save a copy to keep your edits." => "磁盘上的 PDF 已被修改。请使用「另存为」保留当前编辑。",
        "File not found: {0}" => "找不到文件：{0}",
        "Could not copy the file path: {0}" => "无法复制文件路径：{0}",
        "Could not open the file location: {0}" => "无法打开文件位置：{0}",
        "File Explorer could not open this folder." => "文件资源管理器无法打开此文件夹。",
        "That PDF already has a shelf entry. Choose the original document." => "书架中已有此 PDF，请选择原文档。",
        "Printing is not supported on this device." => "此设备不支持打印。",
        "Could not open the file picker: {0}" => "无法打开文件选择器：{0}",
        "Printing failed: {0}" => "打印失败：{0}",
        "Could not copy the PDF file: {0}" => "无法复制 PDF 文件：{0}",
        "Save As failed: {0}" => "另存为失败：{0}",
        "Could not save the signature: {0}" => "无法保存签名：{0}",
        "Flatten failed: {0}" => "合并批注失败：{0}",
        "Could not read signatures: {0}" => "无法读取签名：{0}",
        "Startup problem: {0}" => "启动时出现问题：{0}",
        "Couldn't close cleanly: {0}" => "未能正常关闭：{0}",
        "Page {0}" => "第 {0} 页",
        "Save changes to {0}?" => "保存对「{0}」的修改？",
        "Locate {0} to keep its reading position and bookmarks." => "请找到「{0}」，以保留阅读位置和书签。",
        "Errors are logged to {0}. Attach it if the problem was a crash." => "错误日志保存在 {0}。如果遇到崩溃，请在报告中附上此文件。",
        "Nothing has been logged yet. If PDF Desk misbehaves, errors appear in {0}." => "目前还没有错误日志。如果 PDF Desk 出现问题，日志会保存在 {0}。",
        "Store" => "商店版",
        "portable" => "便携版",
        "PDF document" => "PDF 文档",
        "PDF documents" => "PDF 文档",
        "Looking…" => "正在查找…",
        "Could not be read." => "无法读取。",
        "This document draws no text." => "此文档未绘制文本。",
        "Embedded" => "已嵌入",
        "Not embedded" => "未嵌入",
        "Found in the first {0} pages." => "在前 {0} 页中找到。",
        "monospace" => "等宽",
        "serif" => "衬线",
        "script" => "手写体",
        "symbolic" => "符号字体",
        "italic" => "斜体",
        "bold" => "粗体",
        "Folder" => "文件夹",
        "Last written" => "最后写入时间",
        "Allowed" => "允许的操作",
        "Yes" => "是",
        "No" => "否",
        "None" => "无",
        "Nothing" => "无",
        "Everything allowed" => "允许所有操作",
        "AcroForm (fillable)" => "AcroForm（可填写）",
        "XFA (not fillable in PDF Desk)" => "XFA（PDF Desk 无法填写）",
        "The file is not a valid PDF or is corrupted." => "文件格式无效，或 PDF 已损坏。",
        "The file could not be opened or read." => "无法打开或读取此文件。",
        "The PDF is password-protected." => "此 PDF 受密码保护。",
        "The PDF has an unsupported security scheme." => "此 PDF 使用了不支持的安全机制。",
        "The requested page was not found or is corrupted." => "找不到指定页面，或页面已损坏。",
        "PDFium reported an unknown error." => "PDF 引擎遇到了未知错误。",
        "Yes (revision {0})" => "是（修订版本 {0}）",
        "Pages are not all the same size" => "页面尺寸不完全相同",
        "Pages are not all the same size (checked the first {0})" => "页面尺寸不完全相同（已检查前 {0} 页）",
        "print" => "打印",
        "print at high resolution" => "高分辨率打印",
        "change the content" => "修改内容",
        "copy text" => "复制文本",
        "annotate" => "添加或修改批注",
        "fill in forms" => "填写表单",
        "copy for accessibility" => "为辅助功能复制内容",
        "reorder pages" => "调整页面顺序",
        "Signature details" => "签名详情",
        "Signature details ({0})" => "签名详情（{0} 个）",
        "Signature {0}" => "签名 {0}",
        "Reason" => "签名原因",
        "Signed" => "签署时间",
        "Format" => "格式",
        "Coverage" => "签名覆盖范围",
        "Certified" => "认证",
        "Signed byte range covers the whole file" => "签名的字节范围覆盖整个文件",
        "Part of this file is outside the signed range — it was changed or added after signing" => "文件有部分内容不在签名覆盖范围内，可能在签署后被修改或添加",
        "Could not read the signed byte range" => "无法读取签名的字节范围",
        "Author signature, DocMDP level {0}" => "作者签名，DocMDP 级别 {0}",
        "XFA form" => "XFA 表单",
        "This document uses an XFA form, which PDF Desk cannot fill. Adobe Acrobat can open it." => "此文档使用 XFA 表单，PDF Desk 无法填写。可用 Adobe Acrobat 打开。",
        "Digitally signed document" => "文档含数字签名",
        "{0} digital signatures" => "{0} 个数字签名",
        "PDF Desk has not verified the signature. Open Document information for signature details." => "PDF Desk 未验证签名的有效性。你可以在「文档信息」中查看签名详情。",
        "Nothing to flatten." => "没有可合并的批注。",
        "Flattened {0} page. Save to write the change to disk." => "已合并 {0} 页的批注。请保存以写入文件。",
        "Flattened {0} pages. Save to write the change to disk." => "已合并 {0} 页的批注。请保存以写入文件。",
        "Copy" => "复制",
        "Auto" => "自动",
        "Black" => "黑色",
        "Blue" => "蓝色",
        "Red" => "红色",
        "Green" => "绿色",
        "Delete text box" => "删除文本框",
        "Delete picture" => "删除图片",
        "Handwritten" => "手写体",
        "Printed" => "印刷体",
        "Formal" => "正式体",
        "Add note here" => "在此添加便签",
        "Text appearance…" => "文字外观…",
        "Delete annotation" => "删除批注",
        "Delete note{0}" => "删除便签{0}",
        "Could not copy {0}: {1}" => "无法复制 {0}：{1}",
        "This field takes its text style from the document, so it cannot be changed here." => "此字段沿用文档的文字样式，无法在这里修改。",
        "Select pages in the thumbnail sidebar first, then extract them." => "请先在缩略图侧栏选择要提取的页面。",
        "Pick a different file: extracting over the open document would overwrite it." => "请选择其他文件，以免覆盖当前打开的文档。",
        "Extracted {0} page to {1}." => "已提取 {0} 页到「{1}」。",
        "Extracted {0} pages to {1}." => "已提取 {0} 页到「{1}」。",
        "Could not extract pages: {0}" => "无法提取页面：{0}",
        "Cannot delete every page of a document." => "不能删除文档的所有页面。",
        "Cannot cut every page of a document." => "不能剪切文档的所有页面。",
        "Windows could not open the file picker. Please try that again." => "Windows 无法打开文件选择器，请重试。",
        "That image couldn't be read. Try a PNG or a JPEG." => "无法读取此图片。请尝试使用 PNG 或 JPEG 文件。",
        "This PC has no handwriting font installed, so typing will not look handwritten. Draw or import instead." => "此电脑未安装手写字体。请改用手绘或导入图片。",
        "That image already has a transparent background." => "这张图片已有透明背景。",
        "Open recent: {0}" => "打开最近文件：{0}",
        "Go to page {0}" => "跳转到第 {0} 页",
        "Remove from list (discards {0} bookmarks)" => "从列表移除（同时删除 {0} 个书签）",
        _ => label,
    } : label;

    public static string Format(string format, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, Translate(format), values);

    internal static string PropertyLabel(string name) =>
        name.StartsWith("Page ", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan("Page ".Length), out int page)
                ? Format("Page {0}", page)
                : Translate(name);

    internal static string TranslateList(string value) => Current.Chinese
        ? string.Join("、", value.Split(", ").Select(Translate))
        : value;

    // Translate the values PDFDesk generates and preserve document-supplied metadata.
    internal static string PropertyValue(string section, string name, string value)
    {
        if (!Current.Chinese) return value;

        const string revisionPrefix = "Yes (revision ";
        const string sampledPrefix = "Pages are not all the same size (checked the first ";
        return (section, name) switch
        {
            ("Security", "Encrypted") when value.StartsWith(revisionPrefix, StringComparison.Ordinal) && value.EndsWith(')')
                => Format("Yes (revision {0})", value[revisionPrefix.Length..^1]),
            ("Security", "Encrypted" or "Permissions") or ("Features", "Tagged" or "Form") => Translate(value),
            ("Security", "Allowed") => TranslateList(value),
            ("Pages", "Note") when value.StartsWith(sampledPrefix, StringComparison.Ordinal) && value.EndsWith(')')
                => Format("Pages are not all the same size (checked the first {0})", value[sampledPrefix.Length..^1]),
            ("Pages", "Note") => Translate(value),
            ("Pages", _) => value.Replace(", landscape", "（横向）", StringComparison.Ordinal),
            _ => value,
        };
    }
}
