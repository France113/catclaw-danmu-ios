using CatClawVideo.Core.Services;

namespace CatClawVideo.DanmuTest;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();
        // 线路列表 = 内置 7 条（智能 / 弹弹Play / danmu_API / 虾米 / DouFun / 弹幕库 / 678弹幕）+ 自定义线路
        UserLinesEntry.TextChanged += (_, _) => RefreshLines();
        ApiBaseEntry.TextChanged += (_, _) => RefreshLines();
        RefreshLines();
    }

    /// <summary>按当前自定义线路串 / danmu_API 地址重建线路下拉；保持已选 index。</summary>
    void RefreshLines()
    {
        var sel = LinePicker.SelectedIndex;
        LinePicker.ItemsSource = DanmuSearchService.GetLines(UserLinesEntry.Text, ApiBaseEntry.Text);
        LinePicker.SelectedIndex = sel < 0 || sel >= LinePicker.Items.Count ? 0 : sel;
    }

    private async void OnSearchClicked(object sender, EventArgs e)
    {
        var name = NameEntry.Text?.Trim() ?? "";
        if (name.Length == 0)
        {
            StatusLabel.Text = "⚠️ 请先输入片名";
            return;
        }

        var ep = EpEntry.Text?.Trim();
        var line = (LinePicker.SelectedItem as DanmuLine)?.Name;
        var userLines = UserLinesEntry.Text?.Trim();
        var apiBase = ApiBaseEntry.Text?.Trim();

        SearchButton.IsEnabled = false;
        Spinner.IsRunning = true;
        Spinner.IsVisible = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // 与猫爪主应用同一次调用：SearchAsync 内部含缓存 / 分发 / 解析全链路
            var cues = await DanmuSearchService.SearchAsync(name, ep, line, userLines, apiBase);
            sw.Stop();

            StatusLabel.Text = cues.Count > 0
                ? $"✅ 命中 {cues.Count} 条 · 线路「{line}」· {sw.ElapsedMilliseconds} ms"
                : $"⚠️ 未命中（线路「{line}」无结果）· {sw.ElapsedMilliseconds} ms";
            RenderCues(cues);
        }
        catch (Exception ex)
        {
            sw.Stop();
            StatusLabel.Text = $"❌ 异常 {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            Spinner.IsRunning = false;
            Spinner.IsVisible = false;
            SearchButton.IsEnabled = true;
        }
    }

    void RenderCues(List<DanmuCue> cues)
    {
        ResultLayout.Children.Clear();
        if (cues.Count == 0)
        {
            ResultLayout.Children.Add(new Label { Text = "（无弹幕）", TextColor = Colors.Gray });
            return;
        }

        var stats = cues.GroupBy(c => c.Mode)
            .OrderBy(g => g.Key)
            .Select(g => $"{ModeName(g.Key)}×{g.Count()}");
        ResultLayout.Children.Add(new Label
        {
            Text = "构成：" + string.Join("  ·  ", stats),
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
        });

        foreach (var c in cues.Take(60))
        {
            var ts = TimeSpan.FromSeconds(c.TimeSeconds);
            ResultLayout.Children.Add(new Label
            {
                Text = $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 100} " +
                       $"[{ModeName(c.Mode)}] #{c.Color:X6} {c.Text}",
                FontSize = 13,
            });
        }

        if (cues.Count > 60)
            ResultLayout.Children.Add(new Label
            {
                Text = $"… 其余 {cues.Count - 60} 条省略",
                TextColor = Colors.Gray,
                FontSize = 12,
            });
    }

    static string ModeName(int mode) => mode switch
    {
        1 => "滚动",
        4 => "底部",
        5 => "顶部",
        6 => "左滚",
        _ => $"M{mode}",
    };
}
