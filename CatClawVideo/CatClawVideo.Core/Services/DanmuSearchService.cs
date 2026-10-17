using System.Globalization;
using System.Net;
using System.Text.Json;

namespace CatClawVideo.Core.Services;

/// <summary>
/// 一条弹幕线路（对位 uz影视 danMu 扩展 type:400 的 <c>danmuLines</c>）。
/// </summary>
public sealed class DanmuLine
{
    public required string Name { get; init; }

    /// <summary>
    /// 聚合线路模板（如 <c>https://api.danmu.icu/?ac=dm&amp;url=</c>），请求时在尾部拼接视频页地址。
    /// 为空表示非聚合线路。
    /// </summary>
    public string? UrlTemplate { get; init; }

    /// <summary>
    /// danmu_api 兼容服务端地址（对位 <c>danmu_api</c> v1.21.0 —— 自托管弹幕聚合服务，
    /// 聚合 douban/360/renren/hanjutv/bilibili/youku/tencent/iqiyi/mgtv/sohu/bahamut 等 23 个源；
    /// 请求路径与弹弹Play 一致：<c>{base}/api/v2/search/episodes</c> + <c>{base}/api/v2/comment/{id}</c>，
    /// 但<b>响应信封不同</b>：danmu_api 把 animes/comments 放根层，官方弹弹Play 包在 data 下，
    /// <see cref="Payload"/> 已做双向兼容）。
    /// 为空表示官方弹弹Play <c>api.dandanplay.net</c>。
    /// </summary>
    public string? ApiBase { get; init; }

    /// <summary>聚合线路（模板 + 视频页地址）。</summary>
    public bool IsAggregator => !string.IsNullOrEmpty(UrlTemplate);

    /// <summary>danmu_api 兼容服务线路（含官方弹弹Play：两者同为「按片名+集数」协议）。</summary>
    public bool IsEpisodeApi => string.IsNullOrEmpty(UrlTemplate);

    /// <summary>Picker 等控件直接绑对象时显示线路名（否则显示类型全名）。</summary>
    public override string ToString() => Name;
}

/// <summary>
/// 网络弹幕搜索（移植 uz影视 <c>danMu.js</c> / <c>danMu3.js</c> 的 <c>searchDanMu</c> 实现）。
///
/// <para><b>两条取数策略</b>（与 uz 一致）：</para>
/// <para>1. 弹弹Play：<c>api.dandanplay.net/api/v2/search/episodes</c> 按片名 + 集数搜到
/// <c>episodeId</c>，再取 <c>api/v2/comment/{episodeId}</c>。返回的 <c>p</c>/<c>m</c> 就是标准
/// bilibili 弹幕格式，直接交给 <see cref="DanmuParser.ToCue"/> 解析。</para>
/// <para>2. 聚合线路（虾米 / DouFun / 弹幕库 / 678弹幕）：模板后拼接<b>视频页地址</b>，
/// 视频页地址先用采集站（apple-cms API）按片名 + 集数反查；返回 XML 或 JSON 两种弹幕格式。</para>
///
/// <para><b>与 uz 的差异</b>（均为有意的稳健性修正，注释处标注）：</para>
/// <para>· 番剧匹配：uz 直接取 <c>animes[0].episodes[0]</c>，这里优先标题含片名、集数匹配的条目。</para>
/// <para>· 广告弹幕过滤：uz 的过滤条件写成「http/com/xyz 三者都不含才保留」（等价于只丢同时含三者的），
/// 这里按意图修正为「任一命中即丢」。</para>
/// <para>· 播放列表解析：uz 用会被内层循环遮蔽的下标做集数匹配，且 <c>$</c> 切分取错段，这里按
/// MacCMS 格式修正（跨线路全局集序，取最后一段 <c>$</c> 之后的地址）。</para>
/// </summary>
public static class DanmuSearchService
{
    private const string DandanplaySearchUrl = "https://api.dandanplay.net/api/v2/search/episodes";
    private const string DandanplayCommentUrl = "https://api.dandanplay.net/api/v2/comment/";

    /// <summary>
    /// danmu_api 的默认公共实例（该项目的 OTHER_SERVER 默认值）。
    /// 自建部署后（Docker / Node / Vercel / Cloudflare Worker）把这里换成自己的地址即可，
    /// 支持明文 http（iOS 需 ATS 例外，DanmuTest 的 workflow 已 patch）。
    /// </summary>
    public const string DefaultDanmuApiBase = "https://api.danmu.icu";

    /// <summary>内置线路（uz danMu3.js 的 danmuLines + 显式弹弹Play + danmu_API）。
    /// 「智能」= 先 danmu_API（服务端聚合 23 源）→ 弹弹Play → 聚合线路竞速。</summary>
    public static readonly DanmuLine[] BuiltInLines =
    [
        new() { Name = "智能" },
        new() { Name = "弹弹Play" },
        new() { Name = "danmu_API", ApiBase = DefaultDanmuApiBase },
        new() { Name = "虾米", UrlTemplate = "https://dmku.hls.one/?ac=dm&url=" },
        new() { Name = "DouFun", UrlTemplate = "https://danmu.56uxi.com/?ac=dm&url=" },
        new() { Name = "弹幕库", UrlTemplate = "https://api.danmu.icu/?ac=dm&url=" },
        new() { Name = "678弹幕", UrlTemplate = "https://se.678.ooo/?ac=dm&url=" },
    ];

    /// <summary>
    /// 聚合线路取不到弹幕时，用这些采集站（apple-cms API）按片名反查视频页地址
    /// （对位 uz danMu3.js 的 <c>caiJi</c>）。
    /// </summary>
    private static readonly string[] CollectSites =
    [
        "https://zy.xmm.hk/api.php/provide/vod",
        "https://www.69mu.cn/api.php/provide/vod",
    ];

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    // 结果缓存（DoubanService 同款策略：命中 30 分钟，失败 2 分钟，防止换集时反复超时）
    private static readonly Dictionary<string, (DateTime At, List<DanmuCue> Cues)> Cache = new(StringComparer.Ordinal);
    private static readonly TimeSpan OkTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 全部线路名（内置 + 用户自定义），供 UI 展示（对位 uz 的 <c>getLines</c>）。
    /// 用户自定义格式（uz 的「弹幕线路」环境变量）：<c>线路名称1@地址1;线路名称2@地址2</c>。
    /// 扩展语法：<c>名称@danmuapi:https://自建服务地址</c> = danmu_api 兼容服务线路。
    /// </summary>
    public static List<DanmuLine> GetLines(string? userLines = null, string? apiBase = null)
    {
        var resolvedBase = string.IsNullOrWhiteSpace(apiBase) ? DefaultDanmuApiBase : apiBase.TrimEnd('/');
        var lines = BuiltInLines.ToList();
        // 内置 danmu_API 线路指向当前配置的服务地址
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ApiBase is null) continue;
            lines[i] = new DanmuLine { Name = lines[i].Name, ApiBase = resolvedBase };
        }
        foreach (var custom in ParseUserLines(userLines))
        {
            // 去重保留后者（uz 同策略）
            lines.RemoveAll(l => l.UrlTemplate == custom.UrlTemplate && l.ApiBase == custom.ApiBase);
            // 重名的按序号追加后缀（uz 同策略）
            var name = custom.Name;
            var suffix = 2;
            while (lines.Any(l => l.Name == name)) name = $"{custom.Name}-{suffix++}";
            lines.Add(new DanmuLine { Name = name, UrlTemplate = custom.UrlTemplate, ApiBase = custom.ApiBase });
        }
        return lines;
    }

    /// <summary>解析用户自定义线路串；段内取第一个 <c>@</c> 之前为名称、其余为地址（uz 遇多余 @ 会整段丢弃，这里更宽容）。</summary>
    static List<DanmuLine> ParseUserLines(string? userLines)
    {
        var list = new List<DanmuLine>();
        if (string.IsNullOrWhiteSpace(userLines)) return list;
        foreach (var seg in userLines.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = seg.IndexOf('@');
            if (at <= 0 || at == seg.Length - 1) continue;
            var name = seg[..at].Trim();
            var url = seg[(at + 1)..].Trim();
            if (name.Length == 0 || url.Length == 0) continue;
            // danmu_api 兼容服务：名称@danmuapi:https://...
            if (url.StartsWith("danmuapi:", StringComparison.OrdinalIgnoreCase))
            {
                var baseUrl = url["danmuapi:".Length..].TrimEnd('/');
                if (baseUrl.Length > 0) list.Add(new DanmuLine { Name = name, ApiBase = baseUrl });
                continue;
            }
            list.Add(new DanmuLine { Name = name, UrlTemplate = url });
        }
        return list;
    }

    /// <summary>
    /// 搜索弹幕（对位 uz 的 <c>searchDanMu(item)</c>）。找不到返回空表，不抛。
    /// </summary>
    /// <param name="name">片名</param>
    /// <param name="episode">集数文本；空按电影处理（uz：isMovie 时传 'movie'）</param>
    /// <param name="lineName">线路名；空 / 「智能」= 先 danmu_API 再弹弹Play 再竞速聚合线路</param>
    /// <param name="userLines">用户自定义线路串（<c>名称@地址;…</c> 或 <c>名称@danmuapi:地址;…</c>），与内置线路合并后按名解析</param>
    /// <param name="apiBase">danmu_API 线路的服务地址（自建 danmu_api 实例）；空 = 默认公共实例</param>
    public static async Task<List<DanmuCue>> SearchAsync(
        string name, string? episode, string? lineName, string? userLines = null,
        string? apiBase = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];
        var ep = string.IsNullOrWhiteSpace(episode) ? "movie" : episode.Trim();
        var line = ResolveLine(lineName, userLines, apiBase);

        var key = $"{name}|{ep}|{line.Name}";
        if (Cache.TryGetValue(key, out var hit))
        {
            var ttl = hit.Cues.Count > 0 ? OkTtl : FailTtl;
            if (DateTime.UtcNow - hit.At < ttl) return hit.Cues;
            Cache.Remove(key);
        }

        var cues = await DispatchAsync(name, ep, line, userLines, apiBase, ct).ConfigureAwait(false);

        Cache[key] = (DateTime.UtcNow, cues);
        return cues;
    }

    /// <summary>按线路分发；「智能」= danmu_API → 弹弹Play → 聚合线路竞速。</summary>
    static async Task<List<DanmuCue>> DispatchAsync(
        string name, string ep, DanmuLine line, string? userLines, string? apiBase, CancellationToken ct)
    {
        // danmu_api 兼容服务（含自建实例）：服务端一次聚合 23 个源，优先用它
        if (!line.IsAggregator && line.ApiBase is not null)
            return await SearchDandanplayAsync(name, ep, line.ApiBase, ct).ConfigureAwait(false);

        if (line.IsAggregator)
            return await FetchAggregatorLineAsync(line, name, ep, ct).ConfigureAwait(false);

        // 智能：danmu_API（默认/自建服务）→ 弹弹Play → 聚合线路竞速
        var cues = await SearchDandanplayAsync(name, ep, apiBase, ct).ConfigureAwait(false);
        if (cues.Count > 0) return cues;
        cues = await SearchDandanplayAsync(name, ep, null, ct).ConfigureAwait(false);
        if (cues.Count > 0) return cues;

        // 聚合兜底：与 uz danMu3.js 一致，聚合线路需要先反查视频页地址
        var videoUrl = await ResolveVideoUrlAsync(name, ep, ct).ConfigureAwait(false);
        if (videoUrl is null) return [];
        return await FetchLinesRaceAsync(videoUrl, userLines, ct).ConfigureAwait(false);
    }

    /// <summary>按名称找线路（内置 + 用户自定义）；找不到（用户线路被删）回落「智能」。</summary>
    static DanmuLine ResolveLine(string? lineName, string? userLines, string? apiBase)
    {
        if (string.IsNullOrWhiteSpace(lineName)) return BuiltInLines[0];
        return GetLines(userLines, apiBase).FirstOrDefault(l => l.Name == lineName) ?? BuiltInLines[0];
    }

    // ─────────────────────── 策略一：弹弹Play ───────────────────────

    static async Task<List<DanmuCue>> SearchDandanplayAsync(string name, string episode, string? apiBase, CancellationToken ct)
    {
        var searchUrl = apiBase is null
            ? $"{DandanplaySearchUrl}?anime={Uri.EscapeDataString(name)}&episode={Uri.EscapeDataString(episode)}"
            : $"{apiBase.TrimEnd('/')}/api/v2/search/episodes?anime={Uri.EscapeDataString(name)}&episode={Uri.EscapeDataString(episode)}";
        using var searchDoc = await GetJsonAsync(searchUrl, ct).ConfigureAwait(false);
        if (searchDoc is null) return [];

        var episodeId = PickEpisodeId(searchDoc, name, episode);
        if (episodeId is null) return [];

        var commentBase = apiBase is null ? DandanplayCommentUrl : $"{apiBase.TrimEnd('/')}/api/v2/comment/";

        // chConvert：跟随 uz —— 简体中文地区传 1，其余传 2
        var convert = IsSimplifiedChineseLocale() ? 1 : 2;
        var commentUrl = $"{commentBase}{episodeId}?withRelated=true&chConvert={convert}";
        using var commentDoc = await GetJsonAsync(commentUrl, ct).ConfigureAwait(false);
        if (commentDoc is null) return [];

        return ParseDandanplayComments(commentDoc);
    }

    /// <summary>
    /// 从搜索结果里挑 episodeId：优先标题含片名的番剧，再优先集数匹配的分集
    /// （uz 直接取 animes[0].episodes[0]，同名番外/剧场版多时不够稳）。
    /// </summary>
    static long? PickEpisodeId(JsonDocument doc, string name, string episode)
    {
        try
        {
            // 信封兼容：官方弹弹Play = {data:{animes:[…]}}；danmu_api 及自建分支 = 根层 {errorCode:0,animes:[…]}
            var animes = Payload(doc, "animes");
            if (animes is null || animes.Value.ValueKind != JsonValueKind.Array
                || animes.Value.GetArrayLength() == 0) return null;

            var anime = animes.Value.EnumerateArray()
                .FirstOrDefault(a => TryStr(a, "animeTitle") is { } t && t.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (anime.ValueKind == JsonValueKind.Undefined) anime = animes.Value[0];

            var episodes = anime.GetProperty("episodes");
            if (episodes.GetArrayLength() == 0) return null;

            // episodeNumber 官方是字符串、danmu_api 源码里是数字（index+1），ElemStr 两者都吃
            var hit = episodes.EnumerateArray()
                .FirstOrDefault(e => e.TryGetProperty("episodeNumber", out var en)
                    && MatchEpisode(ElemStr(en), episode));
            // danmu_api 的 episodes 不带 episodeNumber（服务端已按 episode 参数预过滤），
            // 未命中时从「第05集」这类标题里提取集数再兜底一次
            if (hit.ValueKind == JsonValueKind.Undefined)
                hit = episodes.EnumerateArray()
                    .FirstOrDefault(e => MatchEpisodeTitleNumber(TryStr(e, "episodeTitle"), episode));
            if (hit.ValueKind == JsonValueKind.Undefined) hit = episodes[0];

            return hit.GetProperty("episodeId").GetInt64();
        }
        catch { return null; }
    }

    /// <summary>从「第05集」「EP5」这类标题提取集数与目标比较（danmu_api episodes 无 episodeNumber 字段）。</summary>
    static bool MatchEpisodeTitleNumber(string? title, string want)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(title, @"\d+"))
            if (long.TryParse(m.Value, out var n) && long.TryParse(want, out var w) && n == w) return true;
        return false;
    }

    static bool MatchEpisode(string? actual, string want)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var a = actual.Trim();
        if (a == want) return true;
        return long.TryParse(a, out var an) && long.TryParse(want, out var wn) && an == wn;
    }

    static List<DanmuCue> ParseDandanplayComments(JsonDocument doc)
    {
        var cues = new List<DanmuCue>();
        try
        {
            // 信封兼容：官方弹弹Play = {data:{comments:[…]}}；danmu_api = 根层 {count:N,comments:[…]}
            var comments = Payload(doc, "comments");
            if (comments is null || comments.Value.ValueKind != JsonValueKind.Array) return cues;
            foreach (var c in comments.Value.EnumerateArray())
            {
                var p = TryStr(c, "p") ?? "";
                var m = TryStr(c, "m") ?? "";
                if (m.Length == 0) continue;
                // p 即标准 bilibili 弹幕参数（时间,模式,字号,颜色,…），直接复用现有解析
                var cue = DanmuParser.ToCue(p, m) ?? MakeCue(p, m);
                if (cue is not null && !IsAdDanmu(cue.Text)) cues.Add(cue);
            }
        }
        catch { }
        return cues;
    }

    // ─────────────────────── 策略二：聚合线路 ───────────────────────

    /// <summary>聚合线路：先反查视频页地址，再请求该线路解析弹幕。</summary>
    static async Task<List<DanmuCue>> FetchAggregatorLineAsync(DanmuLine line, string name, string ep, CancellationToken ct)
    {
        var videoUrl = await ResolveVideoUrlAsync(name, ep, ct).ConfigureAwait(false);
        if (videoUrl is null) return [];
        return await FetchLineAsync(line, videoUrl, ct).ConfigureAwait(false);
    }

    /// <summary>竞速所有聚合线路（内置 + 用户自定义），返回第一个非空结果（对位 uz 的 Promise.race 取首个非空）。</summary>
    static async Task<List<DanmuCue>> FetchLinesRaceAsync(string videoUrl, string? userLines, CancellationToken ct)
    {
        var lines = GetLines(userLines).Where(l => l.IsAggregator).ToList();
        if (lines.Count == 0) return [];

        var pending = lines.Select(l => FetchLineAsync(l, videoUrl, ct)).ToList();
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(done);
            List<DanmuCue> cues;
            try { cues = await done.ConfigureAwait(false); }
            catch { continue; }
            if (cues.Count > 0) return cues;
        }
        return [];
    }

    /// <summary>请求一条聚合线路并解析（XML <c>&lt;d p="…"&gt;</c> 或 JSON <c>danmuku</c> 两种格式，与 uz parseDanmuData 一致）。</summary>
    static async Task<List<DanmuCue>> FetchLineAsync(DanmuLine line, string videoUrl, CancellationToken ct)
    {
        var text = await GetTextAsync(line.UrlTemplate + videoUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return [];

        var cues = new List<DanmuCue>();
        var data = text.Trim();
        if (data.Contains("<d p=", StringComparison.OrdinalIgnoreCase))
        {
            // XML：bilibili/ACFun 评论格式，交给现有解析器（含非法实体修复与正则兜底）
            cues.AddRange(DanmuParser.ParseXml(data));
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                cues.AddRange(ParseJsonDanmaku(doc));
            }
            catch { }
        }
        return cues.Where(c => !IsAdDanmu(c.Text)).ToList();
    }

    /// <summary>
    /// JSON 弹幕：<c>{ "danmuku": [ [时间, …, 内容], … ] }</c>，内容在下标 4。
    /// uz 跳过前 2 项（<c>slice(2)</c>，疑为头部元数据），保持一致。
    /// </summary>
    static List<DanmuCue> ParseJsonDanmaku(JsonDocument doc)
    {
        var cues = new List<DanmuCue>();
        try
        {
            if (!doc.RootElement.TryGetProperty("danmuku", out var arr)
                || arr.ValueKind != JsonValueKind.Array) return cues;

            for (var i = 2; i < arr.GetArrayLength(); i++)
            {
                var item = arr[i];
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() < 5) continue;
                var text = ElemStr(item[4]);
                if (text.Length == 0) continue;
                var cue = new DanmuCue
                {
                    TimeSeconds = ElemNum(item[0]),
                    Mode = 1,                       // 右滚（JSON 格式不带模式信息）
                    Size = 25,
                    Color = 0xFFFFFF,
                    Text = text,
                };
                cues.Add(cue);
            }
        }
        catch { }
        return cues;
    }

    /// <summary>
    /// 用采集站按片名 + 集数反查视频页地址（对位 uz danMu3.js 的 <c>getVideoUrl</c>）。
    /// 多站竞速，20 秒总超时（uz 同）。
    /// </summary>
    static async Task<string?> ResolveVideoUrlAsync(string name, string ep, CancellationToken ct)
    {
        var epNo = long.TryParse(ep, out var n) ? n : 1;   // 电影/非数字集数按第 1 集
        var tasks = CollectSites
            .Select(site => FetchEpisodeUrlFromSiteAsync(site, name, epNo, ct))
            .ToList();
        if (tasks.Count == 0) return null;

        var timeout = Task.Delay(TimeSpan.FromSeconds(20), ct);
        var pending = new List<Task<string?>>(tasks);
        while (pending.Count > 0)
        {
            // Append(timeout) 把 Task.Delay 的 Task 混进 IEnumerable<Task<string?>>，
            // WhenAny 因此返回 Task<Task>；先判等再转型，避免把超时任务当成站点任务 await
            Task finished = await Task.WhenAny(pending.Append(timeout)).ConfigureAwait(false);
            if (finished == timeout) break;
            var done = (Task<string?>)finished;
            pending.Remove(done);
            string? url;
            try { url = await done.ConfigureAwait(false); }
            catch { continue; }
            if (!string.IsNullOrEmpty(url)) return url;
        }
        return null;
    }

    static async Task<string?> FetchEpisodeUrlFromSiteAsync(string site, string name, long episodeNumber, CancellationToken ct)
    {
        try
        {
            var url = $"{site}/?ac=detail&wd={Uri.EscapeDataString(name)}&pg=1";
            using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
            if (doc is null) return null;
            if (!doc.RootElement.TryGetProperty("list", out var list)
                || list.ValueKind != JsonValueKind.Array) return null;

            foreach (var video in list.EnumerateArray())
            {
                var vodName = TryStr(video, "vod_name");
                if (vodName is null) continue;
                // uz 要求片名全等；这里放宽为互相包含（采集站片名常带「HD」「国语版」等后缀）
                if (!vodName.Contains(name, StringComparison.OrdinalIgnoreCase)
                    && !name.Contains(vodName, StringComparison.OrdinalIgnoreCase)) continue;

                var playUrl = TryStr(video, "vod_play_url");
                if (playUrl is null) continue;
                var epUrl = PickEpisodeUrl(playUrl, episodeNumber);
                if (epUrl is not null) return epUrl;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 从 MacCMS <c>vod_play_url</c>（<c>线路1名$第01集$url1#第02集$url2$$$线路2名$…</c>）里按全局集序取地址。
    /// </summary>
    static string? PickEpisodeUrl(string playUrl, long episodeNumber)
    {
        var n = 0L;
        foreach (var line in playUrl.Split("$$$", StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ep in line.Split('#', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = ep.LastIndexOf('$');
                if (idx < 0 || idx == ep.Length - 1) continue;   // 不是「名$地址」段
                n++;
                if (n == episodeNumber) return ep[(idx + 1)..].Trim();
            }
        }
        return null;
    }

    // ─────────────────────── 公共小工具 ───────────────────────

    /// <summary>
    /// 取响应里的业务节点，兼容两种信封：官方弹弹Play 包在 <c>data</c> 下
    /// （<c>{data:{animes:[…]}}</c>）；danmu_api 及其自建分支（LogVar 等）直接放根层
    /// （<c>{errorCode:0,success:true,animes:[…]}</c> / <c>{count:N,comments:[…]}</c>）。
    /// </summary>
    static JsonElement? Payload(JsonDocument doc, string prop)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty(prop, out var inner))
            return inner;
        return root.TryGetProperty(prop, out var outer) ? outer : null;
    }

    /// <summary>p 段不完整时按默认样式构造（右滚 / 25px / 白），只丢时间都解不出的。</summary>
    static DanmuCue? MakeCue(string p, string text)
    {
        var t = p.Split(',');
        if (t.Length == 0) return null;
        if (!float.TryParse(t[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sec)) return null;
        return new DanmuCue
        {
            TimeSeconds = sec,
            Mode = 1,
            Size = 25,
            Color = 0xFFFFFF,
            Text = text,
        };
    }

    /// <summary>广告弹幕过滤：URL / 域名类字样（uz 的 http/com/xyz 判据，条件按意图修正）。</summary>
    static bool IsAdDanmu(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("http", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".com", StringComparison.OrdinalIgnoreCase)
            || text.Contains(".xyz", StringComparison.OrdinalIgnoreCase)
            || text.Contains("www.", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsSimplifiedChineseLocale()
    {
        try
        {
            var name = CultureInfo.CurrentUICulture.Name;
            return name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("zh-SG", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        var text = await GetTextAsync(url, ct).ConfigureAwait(false);
        if (text is null) return null;
        try { return JsonDocument.Parse(text); }
        catch { return null; }
    }

    static async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        try
        {
            // 公开接口（无源级 UA 可用）：随机 UA，国内 CDN 常拦截默认 .NET UA
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgents.Random());
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static string? TryStr(JsonElement el, string prop)
    {
        try { return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
        catch { return null; }
    }

    static string ElemStr(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Number => el.ToString(),
        _ => "",
    };

    static float ElemNum(JsonElement el)
    {
        try
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out var f)) return f;
            if (el.ValueKind == JsonValueKind.String
                && float.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        }
        catch { }
        return 0f;
    }
}
