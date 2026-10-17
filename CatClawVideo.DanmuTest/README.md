# 猫爪弹幕 · iOS 真机测试工具（CatClawVideo.DanmuTest）

把 **uz影视 弹幕插件**（`danMu.js` / `danMu3.js` 的 `searchDanMu`）移植成 C# 后的
**真机验证工具**：在 iPhone 上输入片名 + 集数、选线路，直接看搜索与解析结果。

> ⚠️ 这是**弹幕链路验证工具**，不是完整版的猫爪影视 iOS 应用。
> 完整应用（播放器 / 爬虫源 / BT 下载）目前只有 Android + Windows 目标，移植 iOS 是另一个量级的工程，见文末说明。

## 安装方式：TrollStore（巨魔）

本仓库产出的 IPA 是**未签名**的，专为 TrollStore 设计：

- 不需要 Apple 开发者账号、证书、描述文件、UDID
- 没有 7 天有效期，装一次永久有效
- 设备要求：iOS 14.0–16.6.1（或 16.7 RC / 17.0），且已装好 TrollStore

**装机三步**：

1. 拿到 IPA：Actions artifact 下载解压，或 AirDrop / 微信文件传输到手机（存到「文件」App）
2. 手机上：文件 App 里**长按 IPA → 共享 → TrollStore**（或 TrollStore → Apps → 右上 `+` → Install IPA File）
3. 点 Install → respring → 主屏出现「弹幕测试」

> 第一次用 TrollStore：先在**设置 → Install ldid**，否则装不了 IPA。

## 弹幕线路（7 条内置 + 自定义）

| 线路 | 类型 | 说明 |
|---|---|---|
| 智能 | 组合 | danmu_API → 弹弹Play → 聚合线路竞速 |
| 弹弹Play | 剧集协议 | 官方 `api.dandanplay.net` |
| **danmu_API** | 剧集协议 | **danmu_api 自建/公共服务，服务端聚合 23 个源**（见下节） |
| 虾米 / DouFun / 弹幕库 / 678弹幕 | 聚合模板 | 模板 + 视频页地址，XML/JSON 解析 |

界面还能自己填：

- **danmu_API 服务地址**：默认公共实例 `https://api.danmu.icu`，自建后填自己的地址
- **自定义线路**：`名称@地址模板;名称2@地址模板2`（`{videoUrl}` = 视频页地址占位）；
  danmu_api 服务用扩展语法 `名称@danmuapi:https://地址`

## danmu_api 融合说明（v1.21.0）

`danmu_api`（GitHub: huangxd-/danmu_api）是**自托管弹幕聚合服务端**，聚合 douban/360/renren/
hanjutv/bilibili/youku/tencent/iqiyi/mgtv/sohu/leshi/bahamut/gamer/hongguo/animeko 等 23 个源
（外加本地上传与 VOD 采集站）。它的**客户端协议完全兼容弹弹Play**：

```
GET  {base}/api/v2/search/episodes?anime=片名&episode=集数
GET  {base}/api/v2/comment/{episodeId}?withRelated=true&chConvert=1
```

所以融合方式不是移植它 6.1MB 的服务端代码，而是把本项目的 `DanmuSearchService` 里
弹弹Play 线路的 base URL 参数化（`DanmuLine.ApiBase`）——自建部署后填上地址即可。

**自建部署**（任选其一，详见该项目 README）：

```bash
# Docker（最省事）
docker run -d --name danmu-api -p 9321:9321 logvar/danmu-api:latest

# Node 本地
git clone https://github.com/huangxd-/danmu_api && cd danmu_api
npm i && npm run start   # 默认端口 9321
```

自建后 App 里「danmu_API 服务地址」填 `http://<局域网IP>:9321`（iOS ATS 例外已在
workflow 里 patch：`NSAllowsArbitraryLoads`，明文 HTTP 可用；公共实例是 https，无副作用）。

## 测什么

| 验证点 | 说明 |
|---|---|
| danmu_API 线路 | 一次请求聚合 23 个源（服务端完成匹配，客户端零逻辑） |
| 弹弹Play 线路 | `search/episodes` → `comment/{episodeId}` 两步请求，`p`/`m` 标准格式解析 |
| 聚合线路 | 虾米 / DouFun / 弹幕库 / 678弹幕：采集站反查视频页地址 → 线路模板请求 → XML/JSON 解析 |
| 智能线路 | danmu_API → 弹弹Play → 聚合线路竞速取首个非空 |
| 解析正确性 | 时间 / 模式（滚动·顶部·底部·左滚）/ 颜色 / 文本，与 bilibili 格式一一对应 |

界面会显示：命中条数、耗时、各模式构成条数、前 60 条弹幕明细。

## 构建 IPA（GitHub Actions，推荐）

仓库推上去后：**Actions → build-unsigned-ipa → Run workflow**，约 10~15 分钟
（含 .NET 10 SDK + MAUI 负载安装），跑完在 run 页面下载 artifact
`CatClawVideo-DanmuTest-unsigned-ipa`（zip 内就是 `.ipa`）。

- runner：`xcode-27`（Xcode 27.0，.NET 10 iOS 27 包要求）；本机不需要有任何开发环境
- 关键命令：`dotnet publish -f net10.0-ios -c Release -p:RuntimeIdentifier=ios-arm64 -p:EnableCodeSigning=false`
  （跳过签名产出未签名 .app，workflow 内 zip 成 IPA 并 patch ATS）
- 公开仓库 macOS 构建**免费**；私有仓库 2000 分钟/月且 macOS 按 10 倍扣，建议 public
- workflow 会打印产物的 `MinimumOSVersion`（当前 **15.0**）。
  装机后打不开 / 装不上，先确认这个数字 ≤ 你设备的 iOS 版本；
  设备是 iOS 14 的话，把 csproj 里 `SupportedOSPlatformVersion` 改成 14.0

命令行下载 artifact（本地有 git/curl 即可）：

```bash
# <PAT> = token；<user>/<repo> = 仓库；<run_id> = run 页面 URL 里的数字
curl -sH "Authorization: token <PAT>" \
  "https://api.github.com/repos/<user>/<repo>/actions/runs/<run_id>/artifacts"
# 从返回里找 name 为 CatClawVideo-DanmuTest-unsigned-ipa 的 id，然后：
curl -L -H "Authorization: token <PAT>" \
  "https://api.github.com/repos/<user>/<repo>/actions/artifacts/<artifact_id>/zip" \
  -o ipa.zip
```

## 在本机 Mac 上构建（可选，不推 GitHub 的话）

前提：Mac 装 Xcode 27、.NET 10 SDK、`dotnet workload install maui`。

```bash
dotnet publish CatClawVideo.DanmuTest/CatClawVideo.DanmuTest.csproj \
  -f net10.0-ios -c Release \
  -p:RuntimeIdentifier=ios-arm64 \
  -p:EnableCodeSigning=false \
  -o out

mkdir -p Payload && cp -r out/*.app Payload/ && zip -qr danmutest.ipa Payload
```

然后把 `danmutest.ipa` 传到手机，按上面的巨魔三步装。

## 目录结构

本仓库是 **CI 出包专用仓**（与开发目录分离，推送即用）：

```
.
├── .github/workflows/build-unsigned-ipa.yml  # CI 构建未签名 IPA（macOS runner）
├── CatClawVideo.DanmuTest/                   # 测试 App 本体
│   ├── CatClawVideo.DanmuTest.csproj         # net10.0-ios（min iOS 15.0）
│   ├── MauiProgram.cs / App.xaml(.cs)        # MAUI 启动
│   ├── MainPage.xaml(.cs)                    # 测试界面
│   └── Platforms/iOS/                        # AppDelegate / Program
└── CatClawVideo/CatClawVideo.Core/Services/  # 弹幕三件套（主仓拷贝快照）
    ├── DanmuSearchService.cs                 # 搜索/分发/竞速（uz 插件移植 + danmu_api 融合）
    ├── DanmuParser.cs                        # XML/JSON 弹幕解析
    └── UserAgents.cs                         # 随机 UA
```

弹幕逻辑在主仓（kankejiang/CatClawVideo）开发，这里是**拷贝进来的快照**；
主仓更新后跑 `sync-from-main.ps1 -MainRepo <主仓路径>` 同步，再 commit & push。

## 测试建议

1. 默认参数（斗罗大陆 / 第 1 集 / 智能）跑一遍 —— 覆盖 danmu_API + 弹弹Play + 聚合兜底全链路。
2. 「智能」没命中时逐条切「danmu_API」「弹弹Play」「虾米」「DouFun」「弹幕库」「678弹幕」，
   即可定位是哪一端不通（danmu_api 挂了 vs 弹弹Play 挂了 vs 采集站反查挂了 vs 聚合站点挂了）。
3. 全部不通基本是网络问题：确认设备能访问 `api.danmu.icu`、`api.dandanplay.net` 等域名。
4. 同一集重复搜会命中 30 分钟缓存，耗时接近 0 ms —— 可用来验证缓存生效。

## 关于「完整版猫爪上 iOS」

源码仓（kankejiang/CatClawVideo）当前只有 `net11.0-android` + `net11.0-windows`
两个目标，从未构建过 iOS。完整移植需要先解决：

- **播放器**：iOS 无实现，要写 AVPlayer 封装并挂载弹幕覆盖层；
- **爬虫源**：spider jar（DexClassLoader）与迅雷 SDK 是 Android 运行时，
  iOS 上**架构上不可用**；只有 drpy2 JS 爬虫（Jint，纯 .NET）能直接活；
- **网络**：MacCMS 源大量 HTTP，iOS ATS 需开例外；
- **BT 下载 / 投屏 / QEMU 桥**等均需重新评估。

建议路径：先用本工具验证弹幕链路 → 确认弹幕稳定后再评估整应用移植的排期。
