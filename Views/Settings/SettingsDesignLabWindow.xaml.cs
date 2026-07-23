using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using STool.Core;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using MediaBrush = System.Windows.Media.Brush;
using Path = System.Windows.Shapes.Path;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace STool.Views.Settings;

public partial class SettingsDesignLabWindow : Window
{
    private const double PreviewWindowWidth = 710;
    private const double PreviewWindowHeight = 560;
    private const double PreviewNavigationWidth = 172;
    private const double PreviewTitleBarHeight = 46;
    private const double BlendFieldLabelWidth = 126;

    private enum PreviewPage
    {
        General,
        Ocr,
        Translation
    }

    private PreviewPage _currentPage = PreviewPage.General;
    private bool _updatingControls;
    private bool _blendSurfaceDemo;

    public SettingsDesignLabWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyBlendPreset();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        SettingsLayout.ResetMetrics();
    }

    private void CompactPreset_Click(object sender, RoutedEventArgs e)
    {
        _blendSurfaceDemo = false;
        ApplyPreset(SettingsLayoutMetrics.Default.With(
            spacingMD: 8,
            inlineLabelWidth: 88,
            hotkeyLabelWidth: 88,
            inputHeight: 32,
            sectionPadding: 12,
            sectionSpacing: 10,
            sectionBorderThickness: 1,
            contentHorizontalMargin: 6,
            contentTopMargin: 6,
            navigationButtonVerticalPadding: 8));
    }

    private void BalancedPreset_Click(object sender, RoutedEventArgs e)
    {
        _blendSurfaceDemo = false;
        ApplyPreset(SettingsLayoutMetrics.Default);
    }

    private void LoosePreset_Click(object sender, RoutedEventArgs e)
    {
        _blendSurfaceDemo = false;
        ApplyPreset(SettingsLayoutMetrics.Default.With(
            spacingMD: 12,
            inlineLabelWidth: 100,
            hotkeyLabelWidth: 100,
            inputHeight: 34,
            sectionPadding: 16,
            sectionSpacing: 14,
            sectionBorderThickness: 1,
            contentHorizontalMargin: 20,
            contentTopMargin: 10,
            navigationButtonVerticalPadding: 10));
    }

    private void BlendPreset_Click(object sender, RoutedEventArgs e)
    {
        ApplyBlendPreset();
    }

    private void ApplyBlendPreset()
    {
        _blendSurfaceDemo = true;
        ApplyPreset(SettingsLayoutMetrics.Default.With(
            spacingMD: 8,
            inlineLabelWidth: BlendFieldLabelWidth,
            hotkeyLabelWidth: BlendFieldLabelWidth,
            inputHeight: 34,
            sectionPadding: 16,
            sectionSpacing: 10,
            sectionBorderThickness: 0,
            contentHorizontalMargin: 24,
            contentTopMargin: 12,
            navigationButtonVerticalPadding: 10,
            sectionShadow: false));
    }

    private void ApplyPreset(SettingsLayoutMetrics metrics)
    {
        _updatingControls = true;
        ShadowCheckBox.IsChecked = metrics.SectionShadow;
        SectionPaddingSlider.Value = metrics.SectionPadding;
        SectionSpacingSlider.Value = metrics.SectionSpacing;
        FieldSpacingSlider.Value = metrics.SpacingMD;
        LabelWidthSlider.Value = metrics.InlineLabelWidth;
        InputHeightSlider.Value = metrics.InputHeight;
        ContentMarginSlider.Value = metrics.ContentHorizontalMargin;
        ContentTopSlider.Value = metrics.ContentTopMargin;
        NavPaddingSlider.Value = metrics.NavigationButtonVerticalPadding;
        _updatingControls = false;

        ApplyControls();
    }

    private void ControlValueChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _updatingControls)
        {
            return;
        }

        ApplyControls();
    }

    private void ApplyControls()
    {
        var metrics = SettingsLayoutMetrics.Default.With(
            spacingMD: FieldSpacingSlider.Value,
            inlineLabelWidth: LabelWidthSlider.Value,
            hotkeyLabelWidth: LabelWidthSlider.Value,
            inputHeight: InputHeightSlider.Value,
            sectionPadding: SectionPaddingSlider.Value,
            sectionSpacing: SectionSpacingSlider.Value,
            sectionBorderThickness: _blendSurfaceDemo ? 0 : SettingsLayoutMetrics.Default.SectionBorderThickness,
            contentHorizontalMargin: ContentMarginSlider.Value,
            contentTopMargin: ContentTopSlider.Value,
            navigationButtonVerticalPadding: NavPaddingSlider.Value,
            sectionShadow: ShadowCheckBox.IsChecked == true);

        SettingsLayout.ApplyMetrics(metrics);
        UpdateValueLabels(metrics);
        PreviewHost.Content = BuildSettingsPreview(metrics);
    }

    private void UpdateValueLabels(SettingsLayoutMetrics metrics)
    {
        SectionPaddingValue.Text = $"分组内边距: {metrics.SectionPadding:0}px";
        SectionSpacingValue.Text = $"分组间距: {metrics.SectionSpacing:0}px";
        FieldSpacingValue.Text = $"表单行距: {metrics.SpacingMD:0}px";
        LabelWidthValue.Text = $"标签列宽: {metrics.InlineLabelWidth:0}px";
        InputHeightValue.Text = $"输入高度: {metrics.InputHeight:0}px";
        ContentMarginValue.Text = $"页面左右边距: {metrics.ContentHorizontalMargin:0}px";
        ContentTopValue.Text = $"页面顶部边距: {metrics.ContentTopMargin:0}px";
        var actualContentWidth = CalculatePreviewContentWidth(metrics);
        NavPaddingValue.Text = $"导航垂直 padding: {metrics.NavigationButtonVerticalPadding:0}px";

        MetricsText.Text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Mode = {(_blendSurfaceDemo ? "Blend / Borderless" : "Default")}\n")
            .Append(CultureInfo.InvariantCulture, $"PreviewWindowWidth = {PreviewWindowWidth:0}\n")
            .Append(CultureInfo.InvariantCulture, $"ActualContentWidth = {actualContentWidth:0}\n")
            .Append(CultureInfo.InvariantCulture, $"SectionPadding = {metrics.SectionPadding:0}\n")
            .Append(CultureInfo.InvariantCulture, $"SectionSpacing = {metrics.SectionSpacing:0}\n")
            .Append(CultureInfo.InvariantCulture, $"SectionBorder = {metrics.SectionBorderThickness:0}\n")
            .Append(CultureInfo.InvariantCulture, $"SpacingMD = {metrics.SpacingMD:0}\n")
            .Append(CultureInfo.InvariantCulture, $"InlineLabelWidth = {metrics.InlineLabelWidth:0}\n")
            .Append(CultureInfo.InvariantCulture, $"InputHeight = {metrics.InputHeight:0}\n")
            .Append(CultureInfo.InvariantCulture, $"ContentMargin = {metrics.ContentHorizontalMargin:0},{metrics.ContentTopMargin:0}")
            .ToString();
    }

    private Grid BuildSettingsPreview(SettingsLayoutMetrics metrics)
    {
        var root = new Grid
        {
            Width = PreviewWindowWidth,
            Height = PreviewWindowHeight,
            Background = (MediaBrush)FindResource("TransparentBrush")
        };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewNavigationWidth) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var nav = new Border
        {
            Background = _blendSurfaceDemo ? CreateLabBrush("#FEFEFF") : (MediaBrush)FindResource("SurfaceBrush"),
            Padding = new Thickness(10, 44, 10, 12)
        };
        var navStack = new StackPanel();
        navStack.Children.Add(CreateNavigationButton("通用", PreviewPage.General, metrics));
        navStack.Children.Add(CreateNavigationButton("OCR", PreviewPage.Ocr, metrics));
        navStack.Children.Add(CreateNavigationButton("翻译", PreviewPage.Translation, metrics));
        nav.Child = navStack;
        root.Children.Add(nav);

        var contentRoot = new Border
        {
            Background = _blendSurfaceDemo ? CreateLabBrush("#F4F5F7") : (MediaBrush)FindResource("SurfaceAltBrush")
        };
        Grid.SetColumn(contentRoot, 1);
        var scroll = new ScrollViewer
        {
            Margin = new Thickness(0, PreviewTitleBarHeight, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Background = (MediaBrush)FindResource("TransparentBrush"),
            FocusVisualStyle = null
        };
        var content = new Grid
        {
            Width = CalculatePreviewContentWidth(metrics),
            Margin = new Thickness(
                metrics.ContentHorizontalMargin,
                metrics.ContentTopMargin,
                metrics.ContentHorizontalMargin,
                18)
        };
        var page = BuildCurrentPage();
        if (_blendSurfaceDemo)
        {
            ApplyBlendSurfaceDemo(page);
        }

        content.Children.Add(page);
        scroll.Content = content;
        var contentLayer = new Grid();
        contentLayer.Children.Add(scroll);
        contentLayer.Children.Add(CreatePreviewScrollBar(scroll));
        contentRoot.Child = contentLayer;
        root.Children.Add(contentRoot);

        AddPreviewChrome(root);
        return root;
    }

    private WpfScrollBar CreatePreviewScrollBar(ScrollViewer scroll)
    {
        var scrollBar = new WpfScrollBar
        {
            Orientation = System.Windows.Controls.Orientation.Vertical,
            Width = 6,
            MinWidth = 6,
            Margin = new Thickness(0, PreviewTitleBarHeight + 2, 2, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Visibility = Visibility.Collapsed
        };

        var syncing = false;
        void SyncScrollBar()
        {
            var scrollableHeight = Math.Max(0, scroll.ScrollableHeight);
            scrollBar.Visibility = scrollableHeight > 0 ? Visibility.Visible : Visibility.Collapsed;
            scrollBar.Maximum = scrollableHeight;
            scrollBar.ViewportSize = Math.Max(0, scroll.ViewportHeight);
            scrollBar.LargeChange = Math.Max(16, scroll.ViewportHeight * 0.9);
            scrollBar.SmallChange = 32;

            syncing = true;
            scrollBar.Value = Math.Min(scroll.VerticalOffset, scrollableHeight);
            syncing = false;
        }

        scroll.ScrollChanged += (_, _) => SyncScrollBar();
        scroll.Loaded += (_, _) => SyncScrollBar();

        scrollBar.ValueChanged += (_, e) =>
        {
            if (!syncing)
            {
                scroll.ScrollToVerticalOffset(e.NewValue);
            }
        };

        return scrollBar;
    }

    private void AddPreviewChrome(Grid root)
    {
        var titleBar = new Border
        {
            Height = PreviewTitleBarHeight,
            Background = (MediaBrush)FindResource("TransparentBrush"),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false
        };
        Grid.SetColumnSpan(titleBar, 2);
        root.Children.Add(titleBar);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 14, 0),
            IsHitTestVisible = false
        };

        buttons.Children.Add(CreateCaptionGlyph("−"));
        buttons.Children.Add(CreateCaptionGlyph("□"));
        buttons.Children.Add(CreateCaptionGlyph("×"));
        Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);
    }

    private TextBlock CreateCaptionGlyph(string text)
    {
        return new TextBlock
        {
            Text = text,
            Width = 32,
            Height = 26,
            FontSize = (double)FindResource("FontSizeTitle"),
            FontWeight = FontWeights.Normal,
            Foreground = (MediaBrush)FindResource("TextPrimaryBrush"),
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static double CalculatePreviewContentWidth(SettingsLayoutMetrics metrics)
    {
        var viewportWidth = PreviewWindowWidth - PreviewNavigationWidth;
        var availableWidth = viewportWidth
            - metrics.ContentHorizontalMargin * 2;

        return Math.Max(0, availableWidth);
    }

    private Button CreateNavigationButton(string text, PreviewPage page, SettingsLayoutMetrics metrics)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("NavigationButton"),
            Padding = new Thickness(16, metrics.NavigationButtonVerticalPadding, 16, metrics.NavigationButtonVerticalPadding),
            Margin = page == PreviewPage.General ? new Thickness(0) : new Thickness(0, 2, 0, 0),
            Tag = page == _currentPage ? "Selected" : null
        };
        button.Click += (_, _) =>
        {
            _currentPage = page;
            ApplyControls();
        };
        return button;
    }

    private void ApplyBlendSurfaceDemo(DependencyObject root)
    {
        var sectionStyle = (Style)FindResource("SettingsFlatSection");
        var sectionBrush = CreateLabBrush("#F8F9FA");
        var sectionBorderBrush = CreateLabBrush("#ECEEF2");
        var inputBrush = CreateLabBrush("#F1F3F6");
        var transparentBrush = (MediaBrush)FindResource("TransparentBrush");

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is DependencyObject dependencyObject)
            {
                ApplyBlendSurfaceDemo(dependencyObject);
            }
        }

        if (root is Border border && ReferenceEquals(border.Style, sectionStyle))
        {
            border.Background = sectionBrush;
            border.BorderBrush = sectionBorderBrush;
            border.BorderThickness = new Thickness(0.75);
            border.CornerRadius = new CornerRadius(10);
            border.Padding = new Thickness(SettingsLayout.SectionPadding);
            border.Margin = new Thickness(0, 0, 0, SettingsLayout.SectionSpacing);
            border.Effect = null;
            return;
        }

        switch (root)
        {
            case TextBox textBox:
                textBox.Background = inputBrush;
                textBox.BorderBrush = transparentBrush;
                textBox.BorderThickness = new Thickness(0);
                break;
            case PasswordBox passwordBox:
                passwordBox.Background = inputBrush;
                passwordBox.BorderBrush = transparentBrush;
                passwordBox.BorderThickness = new Thickness(0);
                break;
            case ComboBox comboBox:
                comboBox.Background = inputBrush;
                comboBox.BorderBrush = transparentBrush;
                comboBox.BorderThickness = new Thickness(0);
                break;
        }
    }

    private void ApplyBlendListStructure(StackPanel page)
    {
        var sectionStyle = (Style)FindResource("SettingsFlatSection");
        var originalChildren = page.Children.Cast<UIElement>().ToList();
        page.Children.Clear();

        for (var i = 0; i < originalChildren.Count; i++)
        {
            var child = originalChildren[i];
            page.Children.Add(child);

            if (child is not Border border || !ReferenceEquals(border.Style, sectionStyle))
            {
                continue;
            }

            var hasLaterSection = originalChildren
                .Skip(i + 1)
                .OfType<Border>()
                .Any(next => ReferenceEquals(next.Style, sectionStyle));
            if (!hasLaterSection)
            {
                continue;
            }

            page.Children.Add(new Border
            {
                Height = 1,
                Background = CreateLabBrush("#ECEEF2"),
                Margin = new Thickness(0, 14, 0, 14)
            });
        }
    }

    private static SolidColorBrush CreateLabBrush(string color)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }

    private StackPanel BuildCurrentPage()
    {
        return _currentPage switch
        {
            PreviewPage.Ocr => BuildOcrPage(),
            PreviewPage.Translation => BuildTranslationPage(),
            _ => BuildGeneralPage()
        };
    }

    private StackPanel BuildGeneralPage()
    {
        var page = new StackPanel();

        var launch = CreateSectionContent("启动与托盘");
        launch.Children.Add(CreatePreviewSwitchRow("开机自动启动", "随 Windows 启动 STool", isChecked: true));
        launch.Children.Add(CreatePreviewSwitchRow("隐藏托盘图标", "隐藏后仍可用快捷键打开，重新显示可在这里关闭", isChecked: true, isLast: true));
        page.Children.Add(SettingsLayout.CreateSection(launch));

        var hotkeys = CreateSectionContent("快捷键设置");
        var hotkeyGrid = new Grid();
        hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SettingsLayout.HotkeyLabelWidth) });
        hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddHotkeyRow(hotkeyGrid, 0, "截图", "Alt+1");
        AddHotkeyRow(hotkeyGrid, 1, "翻译", "Alt+2");
        AddHotkeyRow(hotkeyGrid, 2, "剪贴板", "Alt+3");
        AddHotkeyRow(hotkeyGrid, 3, "设置", "Alt+4", isLast: true);
        hotkeys.Children.Add(hotkeyGrid);
        var hint = SettingsLayout.CreateHint("点击输入框后，直接按下快捷键组合（如 Ctrl+Alt+A）");
        hint.Margin = new Thickness(SettingsLayout.HotkeyLabelWidth, SettingsLayout.InputHintSpacing, 0, 0);
        hotkeys.Children.Add(hint);
        page.Children.Add(SettingsLayout.CreateSection(hotkeys));

        // 通用页已改为即时保存,无保存按钮
        return page;
    }

    private StackPanel BuildOcrPage()
    {
        var page = new StackPanel();

        var provider = CreateSectionContent("OCR 提供商");
        var providerCombo = SettingsLayout.CreateComboBox();
        providerCombo.Items.Add("Windows 本地 OCR");
        providerCombo.Items.Add("腾讯云 OCR");
        providerCombo.Items.Add("AI Vision OCR");
        providerCombo.SelectedIndex = 1;
        var providerField = CreatePreviewInlineField("当前引擎", providerCombo);
        provider.Children.Add(providerField);
        var fallbackPolicy = SettingsLayout.CreateComboBox();
        fallbackPolicy.Items.Add("使用 Windows 本地 OCR");
        fallbackPolicy.Items.Add("不自动处理");
        fallbackPolicy.SelectedIndex = 0;
        provider.Children.Add(CreatePreviewInlineField("失败时", fallbackPolicy, "云服务异常时执行的策略"));
        page.Children.Add(SettingsLayout.CreateSection(provider));

        var (tencentContent, tencentSection, _) = CreatePreviewCollapsibleSection("腾讯云设置", isExpanded: true);
        tencentContent.Children.Add(CreatePreviewInlineField("Secret ID", SettingsLayout.CreateTextBox()));
        tencentContent.Children.Add(CreatePreviewInlineField("Secret Key", SettingsLayout.CreatePasswordField()));
        page.Children.Add(tencentSection);

        var (aiContent, aiSection, _) = CreatePreviewCollapsibleSection("AI Vision 设置");
        aiContent.Children.Add(CreatePreviewInlineField("平台", CreateCombo("OpenAI", "Google AI Studio", "自定义")));
        aiContent.Children.Add(CreatePreviewInlineField("API URL", SettingsLayout.CreateTextBox(), "OpenAI 兼容 Chat Completions 地址，自定义接口需手动填写。"));
        aiContent.Children.Add(CreatePreviewInlineField("API Key", SettingsLayout.CreatePasswordField()));
        aiContent.Children.Add(CreatePreviewInlineField("模型", SettingsLayout.CreateEditableComboBox(), "可点击获取列表，也可以直接手动输入模型名。"));
        aiContent.Children.Add(CreateActionRow("获取模型"));
        page.Children.Add(aiSection);

        page.Children.Add(SettingsLayout.CreateSaveButton());
        return page;
    }

    private StackPanel BuildTranslationPage()
    {
        var page = new StackPanel();

        var provider = CreateSectionContent("翻译提供商");
        provider.Children.Add(CreatePreviewInlineField("当前引擎", CreateCombo("谷歌翻译", "腾讯云翻译", "AI 翻译")));
        page.Children.Add(SettingsLayout.CreateSection(provider));

        var strategy = CreateSectionContent("默认策略");
        strategy.Children.Add(CreatePreviewInlineField("翻译策略", CreateCombo("中文 ⇄ 英文", "自动 → 中文", "自动 → 英文")));
        strategy.Children.Add(CreatePreviewInlineField("截图翻译", CreateCombo("快速：本地规则识别", "智能：AI 识别并翻译"), "智能模式会额外使用 AI 翻译配置，失败时自动回退快速模式。"));
        page.Children.Add(SettingsLayout.CreateSection(strategy));

        var (tencentContent, tencentSection, _) = CreatePreviewCollapsibleSection("腾讯云设置");
        tencentContent.Children.Add(CreatePreviewInlineField("Secret ID", SettingsLayout.CreateTextBox()));
        tencentContent.Children.Add(CreatePreviewInlineField("Secret Key", SettingsLayout.CreatePasswordField()));
        page.Children.Add(tencentSection);

        var (aiContent, aiSection, _) = CreatePreviewCollapsibleSection("AI 翻译设置", isExpanded: true);
        aiContent.Children.Add(CreatePreviewInlineField("平台", CreateCombo("OpenAI", "Google AI Studio", "DeepSeek", "自定义")));
        aiContent.Children.Add(CreatePreviewInlineField("API URL", SettingsLayout.CreateTextBox(), "OpenAI 兼容 Chat Completions 地址，自定义接口需手动填写。"));
        aiContent.Children.Add(CreatePreviewInlineField("API Key", SettingsLayout.CreatePasswordField()));
        aiContent.Children.Add(CreatePreviewInlineField("模型", SettingsLayout.CreateEditableComboBox(), "可点击获取列表，也可以直接手动输入模型名。"));
        aiContent.Children.Add(CreateActionRow("获取模型", "测试"));
        page.Children.Add(aiSection);

        page.Children.Add(SettingsLayout.CreateSaveButton());
        return page;
    }

    private StackPanel CreateSectionContent(string title)
    {
        var section = new StackPanel();
        section.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SettingsGroupTitle")
        });
        return section;
    }

    private Border CreatePreviewSwitchRow(string title, string description, bool isChecked, bool isLast = false)
    {
        var switchBox = SettingsLayout.CreateSwitch();
        switchBox.IsChecked = isChecked;
        if (_blendSurfaceDemo)
        {
            return CreateBlendSwitchRow(title, description, switchBox, isLast);
        }

        return SettingsLayout.CreateSwitchRow(title, description, switchBox, isLast: isLast);
    }

    private UIElement CreatePreviewInlineField(string label, FrameworkElement input, string? hint = null)
    {
        if (_blendSurfaceDemo)
        {
            return CreateBlendFieldRow(label, input, hint);
        }

        return string.IsNullOrWhiteSpace(hint)
            ? SettingsLayout.CreateInlineField(label, input)
            : SettingsLayout.CreateInlineFieldWithHint(label, input, hint);
    }

    private Grid CreateBlendFieldRow(string label, FrameworkElement input, string? hint = null)
    {
        var grid = new Grid
        {
            Margin = SettingsLayout.FieldSpacing,
            MinHeight = SettingsLayout.InputHeight
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SettingsLayout.InlineLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!string.IsNullOrWhiteSpace(hint))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var labelBlock = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(labelBlock, 0);
        Grid.SetColumn(labelBlock, 0);
        grid.Children.Add(labelBlock);

        input.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        Grid.SetRow(input, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        if (!string.IsNullOrWhiteSpace(hint))
        {
            var hintBlock = SettingsLayout.CreateHint(hint);
            hintBlock.Margin = new Thickness(SettingsLayout.FormTextInset, SettingsLayout.InputHintSpacing, 0, 0);
            Grid.SetRow(hintBlock, 1);
            Grid.SetColumn(hintBlock, 1);
            grid.Children.Add(hintBlock);
        }

        return grid;
    }

    private Border CreateBlendSwitchRow(string title, string description, CheckBox switchBox, bool isLast)
    {
        var row = new Border
        {
            Background = (MediaBrush)FindResource("TransparentBrush"),
            BorderBrush = (MediaBrush)FindResource("TransparentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 48,
            Padding = new Thickness(0, 6, 0, 6),
            Margin = new Thickness(0, 0, 0, isLast ? 0 : SettingsLayout.SpacingMD),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = (Style)FindResource("ModernFocusVisual")
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var textStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 18, 0)
        };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = SettingsLayout.BodyFontSize,
            Foreground = (MediaBrush)FindResource("TextPrimaryBrush"),
            FontWeight = FontWeights.Normal
        });
        textStack.Children.Add(new TextBlock
        {
            Text = description,
            Style = (Style)FindResource("HintText"),
            FontSize = SettingsLayout.HintFontSize,
            Foreground = (MediaBrush)FindResource("TextSecondaryBrush"),
            Opacity = 0.78,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(textStack, 0);
        grid.Children.Add(textStack);

        Grid.SetColumn(switchBox, 1);
        grid.Children.Add(switchBox);
        row.Child = grid;

        var hoverBrush = CreateLabBrush("#F3F7FF");
        var transparentBrush = (MediaBrush)FindResource("TransparentBrush");
        var focusBrush = (MediaBrush)FindResource("PrimaryBrush");

        void Toggle() => switchBox.IsChecked = switchBox.IsChecked != true;

        System.Windows.Automation.AutomationProperties.SetName(row, title);
        System.Windows.Automation.AutomationProperties.SetHelpText(row, description);
        System.Windows.Automation.AutomationProperties.SetName(switchBox, title);
        row.MouseEnter += (_, _) => row.Background = hoverBrush;
        row.MouseLeave += (_, _) => row.Background = transparentBrush;
        row.GotKeyboardFocus += (_, _) => row.BorderBrush = focusBrush;
        row.LostKeyboardFocus += (_, _) => row.BorderBrush = transparentBrush;
        row.MouseLeftButtonUp += (_, _) => Toggle();
        row.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)
            {
                Toggle();
                e.Handled = true;
            }
        };

        return row;
    }

    private (StackPanel content, Border section, Expander? expander) CreatePreviewCollapsibleSection(string title, bool isExpanded = false)
    {
        if (!_blendSurfaceDemo)
        {
            return SettingsLayout.CreateCollapsibleSection(title, isExpanded);
        }

        var content = new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed
        };
        var header = CreateBlendExpanderHeader(title, content, isExpanded);
        var wrapper = new StackPanel();
        wrapper.Children.Add(header);
        wrapper.Children.Add(content);

        return (content, SettingsLayout.CreateSection(wrapper), null);
    }

    private Border CreateBlendExpanderHeader(string title, UIElement content, bool isExpanded)
    {
        var chevron = new Path
        {
            Data = Geometry.Parse("M5 8 L10 13 L15 8"),
            Stroke = (MediaBrush)FindResource("TextSecondaryBrush"),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
            RenderTransform = new RotateTransform(isExpanded ? 180 : 0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)FindResource("SettingsGroupTitle"),
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(chevron, 1);
        grid.Children.Add(chevron);

        var header = new Border
        {
            Background = (MediaBrush)FindResource("TransparentBrush"),
            BorderBrush = (MediaBrush)FindResource("TransparentBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            MinHeight = 42,
            Padding = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            FocusVisualStyle = (Style)FindResource("ModernFocusVisual"),
            Child = grid
        };

        var expanded = isExpanded;
        var hoverBrush = CreateLabBrush("#F3F7FF");
        var transparentBrush = (MediaBrush)FindResource("TransparentBrush");
        var focusBrush = (MediaBrush)FindResource("PrimaryBrush");

        void Toggle()
        {
            expanded = !expanded;
            content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            ((RotateTransform)chevron.RenderTransform).Angle = expanded ? 180 : 0;
        }

        System.Windows.Automation.AutomationProperties.SetName(header, title);
        header.MouseEnter += (_, _) => header.Background = hoverBrush;
        header.MouseLeave += (_, _) => header.Background = transparentBrush;
        header.GotKeyboardFocus += (_, _) => header.BorderBrush = focusBrush;
        header.LostKeyboardFocus += (_, _) => header.BorderBrush = transparentBrush;
        header.MouseLeftButtonUp += (_, _) => Toggle();
        header.KeyDown += (_, e) =>
        {
            if (e.Key is System.Windows.Input.Key.Space or System.Windows.Input.Key.Enter)
            {
                Toggle();
                e.Handled = true;
            }
        };

        return header;
    }

    private ComboBox CreateCombo(params string[] items)
    {
        var combo = SettingsLayout.CreateComboBox();
        foreach (var item in items)
        {
            combo.Items.Add(item);
        }
        combo.SelectedIndex = 0;
        return combo;
    }

    private StackPanel CreateActionRow(params string[] actions)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = SettingsLayout.ActionRowMargin
        };

        for (var i = 0; i < actions.Length; i++)
        {
            row.Children.Add(new Button
            {
                Content = actions[i],
                Style = (Style)FindResource("SecondaryButton"),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = i == 0 ? new Thickness(0) : new Thickness(SettingsLayout.SpacingSM, 0, 0, 0)
            });
        }

        return row;
    }

    private void AddHotkeyRow(Grid grid, int row, string label, string value, bool isLast = false)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bottomSpacing = isLast ? 0 : SettingsLayout.SpacingMD;

        var lbl = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("FieldLabel"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var labelHost = new Border
        {
            Height = SettingsLayout.InputHeight,
            Margin = new Thickness(0, 0, 0, bottomSpacing),
            Child = lbl
        };
        Grid.SetRow(labelHost, row);
        Grid.SetColumn(labelHost, 0);
        grid.Children.Add(labelHost);

        var box = new HotkeyBox
        {
            Style = (Style)FindResource("HotkeyTextBox"),
            Height = SettingsLayout.InputHeight,
            Text = value,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, bottomSpacing)
        };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
    }
}
