/*
 * 猫爪弹幕影视源 v1.0（零依赖 CJS 单文件，无需构建）
 *
 * 内容：ffm3u8（非凡采集）MacCMS 协议 — 27 个分类约 10 万资源，播放地址直链 m3u8
 * 弹幕：播放时按「剧名 + 集数」自动匹配，多实例故障转移：
 *   1) 自建 danmu_api（LogVar 实例）  2) 公共实例 api.danmu.icu  3) 官方弹弹Play
 *   命中后经 extra.danmaku（播放结果）+ danmuPush（action）双路推给壳渲染
 *   匹配结果内存缓存 30 分钟，同一集重复播放不重复搜索
 * 配置全部内置 —— 源地址即配置，添加一次永久生效，无需每次进设置
 *
 * 猫爪源接口协议（lijunyi6259/CatPawOpen1）：
 *   GET  /config                          站点列表 + danmuSearchUrl
 *   POST /spider/dmhub/3/{init,home,category,detail,search,play}
 * 壳运行时注入全局：catServerFactory（建服务，壳据此发现端口）、
 *                   catDartServerPort（action 消息通道端口）
 */
'use strict';
const http = require('http');
const https = require('https');

// ─────────── 配置（烧进包内 = 永久保存） ───────────
const SITE_KEY = 'dmhub';
const SITE_NAME = '🎬弹幕影视';
const CONTENT_API = 'https://cj.ffzyapi.com/api.php/provide/vod/from/ffm3u8';
const DANMU_APIS = [
    'https://dm.11031.xyz/87654321', // 自建 danmu_api（LogVar）
    'https://api.danmu.icu', // 公共实例
    'https://api.dandanplay.net', // 官方弹弹Play
];
const REQ_TIMEOUT = 12000;
const CACHE_TTL = 30 * 60 * 1000;
const PLAY_UA = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1';

// ffm3u8 分类（type_id → type_name，2026-10 实测采集）
const CATEGORIES = [
    [4, '动漫片'], [6, '动作片'], [7, '喜剧片'], [8, '爱情片'], [9, '科幻片'],
    [10, '恐怖片'], [11, '剧情片'], [12, '战争片'], [13, '国产剧'], [14, '香港剧'],
    [15, '韩国剧'], [16, '欧美剧'], [20, '记录片'], [21, '台湾剧'], [22, '日本剧'],
    [23, '海外剧'], [24, '泰国剧'], [25, '大陆综艺'], [26, '港台综艺'], [27, '日韩综艺'],
    [28, '欧美综艺'], [29, '国产动漫'], [30, '日韩动漫'], [31, '欧美动漫'], [32, '港台动漫'],
    [33, '海外动漫'], [34, '伦理片'],
];

let httpServer = null;
let firstDanmuOk = false; // 首次匹配成功的提示只弹一次

// ─────────── HTTP 工具 ───────────
function httpRequest(url, opts) {
    opts = opts || {};
    return new Promise((resolve) => {
        let u;
        try {
            u = new URL(url);
        } catch (e) {
            resolve({ status: 0, error: 'bad url', body: '' });
            return;
        }
        const isTls = u.protocol === 'https:';
        const mod = isTls ? https : http;
        const req = mod.request(
            {
                hostname: u.hostname,
                port: u.port || (isTls ? 443 : 80),
                path: u.pathname + u.search,
                method: opts.method || 'GET',
                headers: Object.assign({ 'User-Agent': PLAY_UA, Accept: '*/*' }, opts.headers || {}),
                timeout: opts.timeout || REQ_TIMEOUT,
            },
            (res) => {
                const chunks = [];
                res.on('data', (c) => chunks.push(c));
                res.on('end', () =>
                    resolve({ status: res.statusCode, headers: res.headers, body: Buffer.concat(chunks).toString('utf8') })
                );
            }
        );
        req.on('timeout', () => req.destroy(new Error('timeout')));
        req.on('error', (e) => resolve({ status: 0, error: e.message, body: '' }));
        if (opts.body) req.write(opts.body);
        req.end();
    });
}

async function getJson(url) {
    const r = await httpRequest(url);
    if (r.status !== 200 || !r.body) return null;
    try {
        return JSON.parse(r.body);
    } catch (e) {
        return null;
    }
}

// ─────────── 壳消息通道（复刻 CatPawOpen 标准实现） ───────────
async function messageToDart(data, inReq) {
    try {
        if (!data.prefix) data.prefix = inReq && inReq.server ? inReq.server.prefix : '';
        console.log('[danmu-src] messageToDart ->', JSON.stringify(data));
        const port = typeof catDartServerPort === 'function' ? catDartServerPort() : 0;
        if (!port) return null; // 壳不支持消息通道，静默
        const body = JSON.stringify(data);
        return await new Promise((resolve) => {
            const req = http.request(
                {
                    host: '127.0.0.1',
                    port: port,
                    path: '/msg',
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) },
                    timeout: 5000,
                },
                (res) => {
                    let chunks = '';
                    res.on('data', (c) => (chunks += c));
                    res.on('end', () => {
                        try {
                            resolve(JSON.parse(chunks));
                        } catch (e) {
                            resolve(chunks || null);
                        }
                    });
                }
            );
            req.on('timeout', () => {
                req.destroy(new Error('timeout'));
                resolve(null);
            });
            req.on('error', () => resolve(null));
            req.write(body);
            req.end();
        });
    } catch (e) {
        return null;
    }
}

// ─────────── 弹幕匹配 ───────────
const danmuCache = new Map(); // key: 剧名|集数 → { url, ts }

function cacheGet(key) {
    const hit = danmuCache.get(key);
    if (hit && Date.now() - hit.ts < CACHE_TTL) return hit.url;
    danmuCache.delete(key);
    return undefined;
}

// 剧名清洗：去季数、年份、分辨率、语种等干扰项，提高匹配率
function cleanAnimeName(name) {
    if (!name) return '';
    let s = String(name);
    s = s.replace(/第[0-9一二三四五六七八九十百]+季/gu, '');
    s = s.replace(/[（(][^）)]{0,12}[)）]/gu, '');
    s = s.replace(
        /(4k|8k|1080p|720p|HD|BD|超清|高清|臻彩|修复|未删减|无删减|完整版|国语|粤语|英语|日语|韩语|中字|字幕版|OVA|特别篇|电影版|剧场版)/giu,
        ''
    );
    s = s.replace(/[:：\s._-]+$/u, '').replace(/^[:：\s._-]+/u, '');
    return s.trim();
}

// 中文/阿拉伯数字季数集数转换
function cnNum(s) {
    const map = { 一: 1, 二: 2, 两: 2, 三: 3, 四: 4, 五: 5, 六: 6, 七: 7, 八: 8, 九: 9, 十: 10 };
    if (/^[0-9]+$/.test(s)) return parseInt(s, 10);
    if (s === '十') return 10;
    const m = s.match(/^([一二三四五六七八九])?十([一二三四五六七八九])?$/u);
    if (m) return (map[m[1] || '一'] || 1) * 10 + (m[2] ? map[m[2]] : 0);
    return map[s] || parseInt(s, 10) || 1;
}

// 从「第05集」「EP5」「05」提取集数
function parseEpisodeNum(text) {
    if (text === undefined || text === null) return 1;
    const s = String(text).trim();
    if (!s) return 1;
    let m = s.match(/第\s*([0-9一二三四五六七八九十百]+)\s*[集话話期]/u);
    if (m) return cnNum(m[1]);
    m = s.match(/(?:EP|ep|Ep|E)[\s._-]*([0-9]+)/u);
    if (m) return parseInt(m[1], 10);
    m = s.match(/^\s*([0-9]+)\s*$/u);
    if (m) return parseInt(m[1], 10);
    return 1;
}

// 标题匹配打分：优先裸标题完全相等（renren 等源标题带 (2026)【电视剧】from renren 后缀）
function animeScore(title, name) {
    const t = String(title || '');
    const bare = t
        .replace(/[(（][^)）]*[)）]/gu, '')
        .replace(/【[^】]*】/gu, '')
        .replace(/from\s+\S+\s*$/u, '')
        .trim();
    if (bare === name) return 100;
    if (bare.indexOf(name) >= 0) return 80;
    if (t.indexOf(name) >= 0) return 60;
    return 0;
}

// 核心：剧名+集数 → 弹幕 XML 直链（按实例顺序故障转移）
async function findDanmu(vodName, episodeText) {
    const name = cleanAnimeName(vodName);
    const ep = parseEpisodeNum(episodeText);
    if (!name) return null;
    const key = name + '|' + ep;
    const cached = cacheGet(key);
    if (cached !== undefined) return cached;

    for (const base of DANMU_APIS) {
        try {
            const searchUrl =
                base + '/api/v2/search/episodes?anime=' + encodeURIComponent(name) + '&episode=' + ep;
            const d = await getJson(searchUrl);
            if (!d) continue;
            // 兼容两种信封：danmu_api 根层 animes；官方弹弹Play data.animes
            const animes = d.animes || (d.data && d.data.animes) || [];
            if (!animes.length) continue;
            let best = null;
            let bestScore = 0;
            for (const a of animes) {
                const sc = animeScore(a.animeTitle, name);
                if (sc > bestScore) {
                    best = a;
                    bestScore = sc;
                }
            }
            if (!best) best = animes[0];
            const eps = best.episodes || [];
            if (!eps.length) continue;
            const epId = eps[0].episodeId;
            if (!epId) continue;
            const xml = base + '/api/v2/comment/' + epId + '?withRelated=true&chConvert=1&format=xml';
            console.log('[danmu-src] 命中:', name, '第' + ep + '集 ->', base, '#' + epId);
            danmuCache.set(key, { url: xml, ts: Date.now() });
            return xml;
        } catch (e) {
            console.log('[danmu-src] ' + base + ' 匹配失败:', e.message);
        }
    }
    console.log('[danmu-src] 未匹配到弹幕:', name, '第' + ep + '集');
    danmuCache.set(key, { url: null, ts: Date.now() });
    return null;
}

// ─────────── 内容 API 适配 ───────────
function stripHtml(s) {
    return String(s || '')
        .replace(/<br\s*\/?>/gi, '\n')
        .replace(/<\/p>/gi, '\n')
        .replace(/<[^>]+>/g, '')
        .replace(/&nbsp;/g, ' ')
        .replace(/&amp;/g, '&')
        .replace(/&lt;/g, '<')
        .replace(/&gt;/g, '>')
        .replace(/&quot;/g, '"')
        .trim();
}

function vodItem(v) {
    return {
        vod_id: String(v.vod_id),
        vod_name: String(v.vod_name || ''),
        vod_pic: v.vod_pic || '',
        vod_remarks: v.vod_remarks || '',
    };
}

// 详情：把「第01集$url」编码成「第01集$url@@@剧名@@@集数」，
// /play 时解出剧名和集数供弹幕匹配（选集展示不受影响）
function detailItem(v) {
    const name = String(v.vod_name || '');
    const lines = String(v.vod_play_url || '').split('$$$');
    const froms = String(v.vod_play_from || '').split('$$$');
    const newUrls = [];
    const newFroms = [];
    for (let i = 0; i < lines.length; i++) {
        const eps = lines[i].split('#').filter(Boolean);
        const arr = [];
        for (const e of eps) {
            const idx = e.indexOf('$');
            if (idx < 0) continue;
            const epName = e.slice(0, idx);
            const url = e.slice(idx + 1);
            if (!url) continue;
            arr.push(epName + '$' + url + '@@@' + name + '@@@' + parseEpisodeNum(epName));
        }
        if (arr.length) {
            newUrls.push(arr.join('#'));
            newFroms.push((froms[i] || '').trim() || '线路' + (i + 1));
        }
    }
    return {
        vod_id: String(v.vod_id),
        vod_name: name,
        vod_pic: v.vod_pic || '',
        vod_year: v.vod_year || '',
        vod_area: v.vod_area || '',
        vod_remarks: v.vod_remarks || '',
        vod_actor: v.vod_actor || '',
        vod_director: v.vod_director || '',
        vod_content: stripHtml(v.vod_content),
        vod_play_from: newFroms.join('$$$'),
        vod_play_url: newUrls.join('$$$'),
    };
}

// ─────────── 站点接口 ───────────
const routes = {
    check: async () => ({ run: !server.stop }),

    config: async () => ({
        video: {
            sites: [
                {
                    key: 'nodejs_' + SITE_KEY,
                    name: SITE_NAME,
                    type: 3,
                    api: '/spider/' + SITE_KEY + '/3',
                    searchable: 1,
                    quickSearch: 1,
                },
            ],
            danmuSearchUrl: DANMU_APIS[0] + '/api/v2/search/episodes?anime={name}&episode={episode}',
        },
    }),

    init: async () => ({}),

    home: async () => ({
        class: CATEGORIES.map(([id, n]) => ({ type_id: String(id), type_name: n })),
    }),

    category: async (body) => {
        const tid = body.id || CATEGORIES[0][0];
        const pg = parseInt(body.page, 10) || 1;
        const d = await getJson(CONTENT_API + '?ac=detail&t=' + encodeURIComponent(tid) + '&pg=' + pg);
        const list = (d && d.list) || [];
        return {
            page: d && d.page ? parseInt(d.page, 10) || pg : pg,
            pagecount: d && d.pagecount ? parseInt(d.pagecount, 10) || 1 : 1,
            total: d && d.total ? parseInt(d.total, 10) || list.length : list.length,
            list: list.map(vodItem),
        };
    },

    search: async (body) => {
        const wd = (body.wd || '').trim();
        if (!wd) return { page: 1, pagecount: 1, total: 0, list: [] };
        const d = await getJson(CONTENT_API + '?ac=detail&wd=' + encodeURIComponent(wd));
        const list = (d && d.list) || [];
        return {
            page: 1,
            pagecount: 1,
            total: d && d.total ? parseInt(d.total, 10) || list.length : list.length,
            list: list.map(vodItem),
        };
    },

    detail: async (body) => {
        const ids = Array.isArray(body.id) ? body.id : [body.id];
        const out = [];
        for (const id of ids) {
            const d = await getJson(CONTENT_API + '?ac=detail&ids=' + encodeURIComponent(id));
            const v = d && d.list && d.list[0];
            if (v) out.push(detailItem(v));
        }
        return { list: out };
    },

    // 主战场：解析播放地址 + 自动匹配弹幕
    play: async (body, inReq) => {
        const raw = String(body.id || '');
        const parts = raw.split('@@@');
        let url = raw;
        let name = '';
        let ep = '1';
        if (parts.length >= 3) {
            url = parts[0];
            name = parts[1];
            ep = parts[2];
        }
        if (!url) return { parse: 0, url: '' };

        let danmuUrl = null;
        try {
            danmuUrl = await findDanmu(name, ep);
        } catch (e) {
            console.log('[danmu-src] play 匹配异常:', e.message);
        }

        const result = { parse: 0, url: url, header: { 'User-Agent': PLAY_UA } };

        if (danmuUrl) {
            // 双路推送：播放结果 extra.danmaku + action danmuPush（壳二选一）
            result.extra = { danmaku: danmuUrl };
            try {
                await messageToDart({ action: 'danmuPush', opt: { url: danmuUrl } }, inReq);
            } catch (e) {
                /* 壳不支持则静默 */
            }
            if (!firstDanmuOk) {
                firstDanmuOk = true;
                try {
                    await messageToDart(
                        {
                            action: 'toast',
                            opt: { message: '✅ 弹幕已匹配：' + cleanAnimeName(name) + '，如画面未显示请告诉我', duration: 5 },
                        },
                        inReq
                    );
                } catch (e) {
                    /* ignore */
                }
            }
        } else if (name) {
            // 未匹配到时提示一声便于排查（不影响播放）
            try {
                await messageToDart(
                    {
                        action: 'toast',
                        opt: {
                            message: '未匹配到弹幕：' + cleanAnimeName(name) + (ep && ep !== '1' ? ' 第' + ep + '集' : ''),
                            duration: 3,
                        },
                    },
                    inReq
                );
            } catch (e) {
                /* ignore */
            }
        }
        return result;
    },
};

const PREFIX = '/spider/' + SITE_KEY + '/3';

const server = {
    stop: false,
    config: {},
    prefix: '',
    messageToDart: messageToDart,
    address() {
        const a = httpServer.address();
        return {
            address: a.address,
            port: a.port,
            family: a.family,
            url: 'http://' + a.address + ':' + a.port,
            dynamic: 'js2p://_WEB_',
        };
    },
    close() {
        if (httpServer) httpServer.close();
    },
};

function sendJson(res, obj) {
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(obj));
}

function readBody(req) {
    return new Promise((resolve) => {
        let chunks = '';
        req.on('data', (c) => (chunks += c));
        req.on('end', () => {
            try {
                resolve(JSON.parse(chunks || '{}'));
            } catch (e) {
                resolve({});
            }
        });
    });
}

async function start(config) {
    server.config = config || {};
    const handler = async (req, res) => {
        try {
            const u = new URL(req.url, 'http://127.0.0.1');
            const path = u.pathname;
            // 根级 GET：/check /config
            if (req.method === 'GET' && routes[path.slice(1)]) {
                const inReq = { server: server, body: {} };
                return sendJson(res, await routes[path.slice(1)](inReq));
            }
            // 站点 POST：/spider/dmhub/3/{init,home,...}
            if (req.method === 'POST' && path.indexOf(PREFIX + '/') === 0) {
                const key = path.slice(PREFIX.length + 1);
                if (routes[key]) {
                    const body = await readBody(req);
                    const inReq = { server: server, body: body };
                    return sendJson(res, await routes[key](body, inReq));
                }
            }
            res.writeHead(404, { 'Content-Type': 'text/plain' });
            res.end('danmu-src: not found');
        } catch (e) {
            console.log('[danmu-src] handler error:', e);
            res.writeHead(500, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: e.message }));
        }
    };
    // 端口发现：优先壳注入的 catServerFactory（壳持工厂实例即知端口），
    // 拿不到退回原生 http.createServer（DEV_HTTP_PORT 或系统随机端口）
    if (typeof catServerFactory === 'function') {
        try {
            httpServer = catServerFactory(handler);
            console.log('[danmu-src] server created via catServerFactory');
        } catch (e) {
            console.log('[danmu-src] catServerFactory failed, fallback:', e.message);
            httpServer = null;
        }
    }
    if (!httpServer) httpServer = http.createServer(handler);
    httpServer.listen({ port: process.env.DEV_HTTP_PORT || 0, host: '127.0.0.1' });
    console.log('[danmu-src] server started, site = ' + SITE_NAME);
}

async function stop() {
    server.stop = true;
    if (httpServer) httpServer.close();
    httpServer = null;
}

module.exports = { start: start, stop: stop };
