/*
 * 猫爪弹幕探针蜘蛛 v1（零依赖 CJS 单文件，无需构建）
 *
 * 目的：验证 iOS 猫爪 2.0.3 壳的弹幕钩子哪些真的实现了。一次播放同时测三条路：
 *   路 A（播放结果级）：/play 返回 extra.danmaku = B站标准 XML 直链
 *   路 B（action 通道）：messageToDart 发 toast / getPlayInfo / danmuPush
 *   路 C（配置级搜索）：/config 的 danmuSearchUrl，壳若支持会自动来请求
 *       （可在 LogVar 面板日志里看到搜索记录）
 *
 * 测试片：复仇者联盟4（用户 LogVar 实例已验证直出 12.6MB B站 XML / 13 万条弹幕）
 * 视频：Apple 公开 HLS 测试流（media_kit 直接播）
 *
 * 判读：
 *   - 弹出 toast「消息通道测试」  → 路 B 通（getPlayInfo/danmuPush 可用）
 *   - 画出弹幕                    → 路 A 或 B 通（看弹幕内容来源）
 *   - LogVar 面板出现搜索日志     → 路 C 通
 *   - 全无                        → 壳无弹幕钩子，需回退 MAUI iOS 方案
 */
'use strict';
const http = require('http');

// ─── 探针配置 ───
const SITE_KEY = 'dmprobe';
const SITE_NAME = '🧪弹幕探针';
// 已验证：用户 LogVar 实例直出 B站标准 XML（application/xml, <i><d p="...">）
const DANMU_XML = 'https://dm.11031.xyz/87654321/api/v2/comment/761699?withRelated=true&chConvert=1&format=xml';
// Apple 公开 HLS 测试流
const TEST_VIDEO = 'https://devstreaming-cdn.apple.com/videos/streaming/examples/img_bipbop_adv_example_fmp4/master.m3u8';
// 配置级弹幕搜索接口（FongMi DanmakuApi 契约：URL 含 {name}/{episode} → GET）
const DANMU_SEARCH_URL = 'https://dm.11031.xyz/87654321/api/v2/search/episodes?anime={name}&episode={episode}';

const TEST_VOD = {
    vod_id: 'avengers4',
    vod_name: '复仇者联盟4：终局之战（弹幕测试片）',
    vod_pic: 'https://img1.doubanio.com/view/photo/l_ratio_poster/public/p2547747606.jpg',
    vod_remarks: '弹幕探针',
    vod_content: '播放后观察三件事：① 是否弹出 toast ② 画面是否有弹幕 ③ LogVar 面板是否出现搜索日志。',
    vod_play_from: '探针线路',
    vod_play_url: '正片$avengers4',
};

let httpServer = null;

// ─── 壳消息通道（复刻 CatPawOpen src/index.js 的标准实现）───
async function messageToDart(data, inReq) {
    try {
        if (!data.prefix) data.prefix = inReq && inReq.server ? inReq.server.prefix : '';
        console.log('[probe] messageToDart ->', JSON.stringify(data));
        const port = typeof catDartServerPort === 'function' ? catDartServerPort() : 0;
        if (!port) {
            console.log('[probe] catDartServerPort=0 或未注入：壳不支持消息通道');
            return null;
        }
        const body = JSON.stringify(data);
        return await new Promise((resolve) => {
            const req = http.request({
                host: '127.0.0.1',
                port: port,
                path: '/msg',
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) },
            }, (res) => {
                let chunks = '';
                res.on('data', (c) => { chunks += c; });
                res.on('end', () => {
                    try { resolve(JSON.parse(chunks)); } catch (e) { resolve(chunks || null); }
                });
            });
            req.on('error', (e) => { console.log('[probe] /msg post error:', e.message); resolve(null); });
            req.write(body);
            req.end();
        });
    } catch (e) {
        console.log('[probe] messageToDart error:', e.message);
        return null;
    }
}

const server = {
    stop: false,
    config: {},
    prefix: '',
    messageToDart: messageToDart,
    address() {
        const a = httpServer.address();
        return {
            address: a.address, port: a.port, family: a.family,
            url: 'http://' + a.address + ':' + a.port,
            dynamic: 'js2p://_WEB_',
        };
    },
    close() { if (httpServer) httpServer.close(); },
};

// ─── HTTP 工具 ───
function sendJson(res, obj) {
    const body = JSON.stringify(obj);
    res.writeHead(200, { 'Content-Type': 'application/json; charset=utf-8' });
    res.end(body);
}

function readBody(req) {
    return new Promise((resolve) => {
        let chunks = '';
        req.on('data', (c) => { chunks += c; });
        req.on('end', () => {
            try { resolve(JSON.parse(chunks || '{}')); } catch (e) { resolve({}); }
        });
    });
}

// ─── 站点接口（协议见《猫爪源接口协议文档》）───
const routes = {
    check: async () => ({ run: !server.stop }),

    config: async () => ({
        video: {
            sites: [{
                key: 'nodejs_' + SITE_KEY,
                name: SITE_NAME,
                type: 3,
                api: '/spider/' + SITE_KEY + '/3',
            }],
            danmuSearchUrl: DANMU_SEARCH_URL,
        },
    }),

    init: async () => ({}),

    home: async () => ({ class: [{ type_id: 'test', type_name: '弹幕测试' }] }),

    category: async () => ({ page: 1, pagecount: 1, total: 1, list: [TEST_VOD] }),

    detail: async () => ({ list: [TEST_VOD] }),

    search: async () => ({ page: 1, pagecount: 1, total: 1, list: [TEST_VOD] }),

    // 探针主战场
    play: async (body, inReq) => {
        const id = body.id || 'avengers4';
        console.log('[probe] /play id =', id);

        // 探针 B1：toast —— 验证消息通道是否通
        await messageToDart({
            action: 'toast',
            opt: { message: '🧪[弹幕探针] 看到这条=消息通道正常', duration: 6 },
        }, inReq);

        // 探针 B2：getPlayInfo —— 弹幕自动推送的剧集信息来源
        let info = null;
        try { info = await messageToDart({ action: 'getPlayInfo', opt: {} }, inReq); } catch (e) {}
        await messageToDart({
            action: 'toast',
            opt: { message: '🧪[弹幕探针] getPlayInfo=' + JSON.stringify(info), duration: 10 },
        }, inReq);

        // 探针 B3：danmuPush —— 推 B站 XML 给壳渲染（与 extra.danmaku 同内容，壳二选一）
        await messageToDart({ action: 'danmuPush', opt: { url: DANMU_XML } }, inReq);

        // 探针 A：播放结果 extra.danmaku（B站 XML 直链）
        return {
            parse: 0,
            url: TEST_VIDEO,
            extra: { danmaku: DANMU_XML },
        };
    },
};

const PREFIX = '/spider/' + SITE_KEY + '/3';

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
            // 站点 POST：/spider/dmprobe/3/{init,home,...}
            if (req.method === 'POST' && path.indexOf(PREFIX + '/') === 0) {
                const key = path.slice(PREFIX.length + 1);
                if (routes[key]) {
                    const body = await readBody(req);
                    const inReq = { server: server, body: body };
                    return sendJson(res, await routes[key](body, inReq));
                }
            }
            res.writeHead(404, { 'Content-Type': 'text/plain' });
            res.end('probe: not found');
        } catch (e) {
            console.log('[probe] handler error:', e);
            res.writeHead(500, { 'Content-Type': 'application/json' });
            res.end(JSON.stringify({ error: e.message }));
        }
    };
    // 端口发现：官方 bundle（CatPawOpen）用壳注入的 catServerFactory 建服务，
    // 壳通过工厂持有实例从而知道端口。探针优先走同一条路，拿不到再退回原生
    // http.createServer（DEV_HTTP_PORT 或系统随机端口）。
    if (typeof catServerFactory === 'function') {
        try {
            httpServer = catServerFactory(handler);
            console.log('[probe] server created via catServerFactory');
        } catch (e) {
            console.log('[probe] catServerFactory failed, fallback to http:', e.message);
            httpServer = null;
        }
    }
    if (!httpServer) httpServer = http.createServer(handler);
    httpServer.listen({ port: process.env.DEV_HTTP_PORT || 0, host: '127.0.0.1' });
    console.log('[probe] server started, site = ' + SITE_NAME);
}

async function stop() {
    server.stop = true;
    if (httpServer) httpServer.close();
    httpServer = null;
}

module.exports = { start: start, stop: stop };
