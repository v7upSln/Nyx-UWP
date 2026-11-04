// Sanity check for the bundled starter list: well-known ad / tracker / analytics requests must be blocked,
// while ordinary site resources must not be.
use nyx_adblock::*;

const STARTER: &str = include_str!("data/starter.txt");

const SHOULD_BLOCK: &[(&str, &str)] = &[
    ("https://securepubads.g.doubleclick.net/tag/js/gpt.js", "script"),
    ("https://pagead2.googlesyndication.com/pagead/js/adsbygoogle.js", "script"),
    ("https://googleads.g.doubleclick.net/pagead/id", "xmlhttprequest"),
    ("https://adservice.google.com/adsid/google/ui", "xmlhttprequest"),
    ("https://www.google-analytics.com/analytics.js", "script"),
    ("https://www.google-analytics.com/g/collect?v=2", "ping"),
    ("https://www.googletagmanager.com/gtag/js?id=G-XXXX", "script"),
    ("https://c.amazon-adsystem.com/aax2/apstag.js", "script"),
    ("https://ib.adnxs.com/ut/v3", "xmlhttprequest"),
    ("https://static.criteo.net/js/ld/publishertag.js", "script"),
    ("https://cdn.taboola.com/libtrc/impl.js", "script"),
    ("https://widgets.outbrain.com/outbrain.js", "script"),
    ("https://ads.pubmatic.com/AdServer/js/pwt.js", "script"),
    ("https://fastlane.rubiconproject.com/a/api/fastlane.json", "xmlhttprequest"),
    ("https://servedby.openx.net/w/1.0/arj", "script"),
    ("https://js-sec.indexww.com/ht/p/1.js", "script"),
    ("https://cdn.moatads.com/moatheader.js", "script"),
    ("https://pixel.advertising.com/ups/1/sync", "image"),
    ("https://match.adsrvr.org/track/cmf/generic", "image"),
    ("https://sb.scorecardresearch.com/beacon.js", "script"),
    ("https://pixel.quantserve.com/pixel", "image"),
    ("https://static.chartbeat.com/js/chartbeat.js", "script"),
    ("https://static.hotjar.com/c/hotjar-1.js", "script"),
    ("https://script.hotjar.com/modules.js", "script"),
    ("https://cdn.mxpnl.com/libs/mixpanel-2-latest.min.js", "script"),
    ("https://api.mixpanel.com/track", "xmlhttprequest"),
    ("https://cdn.segment.com/analytics.js/v1/x/analytics.min.js", "script"),
    ("https://cdn.amplitude.com/libs/amplitude-8.js", "script"),
    ("https://edge.fullstory.com/s/fs.js", "script"),
    ("https://cdn.mouseflow.com/projects/x.js", "script"),
    ("https://www.clarity.ms/tag/abc", "script"),
    ("https://connect.facebook.net/en_US/fbevents.js", "script"),
    ("https://www.facebook.com/tr?id=1&ev=PageView", "image"),
    ("https://analytics.twitter.com/i/adsct", "image"),
    ("https://static.ads-twitter.com/uwt.js", "script"),
    ("https://snap.licdn.com/li.lms-analytics/insight.min.js", "script"),
    ("https://px.ads.linkedin.com/collect", "image"),
    ("https://analytics.tiktok.com/i18n/pixel/events.js", "script"),
    ("https://sc-static.net/scevent.min.js", "script"),
    ("https://s.pinimg.com/ct/core.js", "script"),
    ("https://bat.bing.com/bat.js", "script"),
    ("https://ads.yahoo.com/pixel", "image"),
    ("https://cdn.optimizely.com/js/123.js", "script"),
    ("https://js.hs-analytics.net/analytics/1/1.js", "script"),
    ("https://static.cloudflareinsights.com/beacon.min.js", "script"),
    ("https://www.googleadservices.com/pagead/conversion.js", "script"),
    ("https://stats.g.doubleclick.net/j/collect", "xmlhttprequest"),
    ("https://cm.g.doubleclick.net/pixel", "image"),
    ("https://tpc.googlesyndication.com/simgad/1", "image"),
];

const SHOULD_ALLOW: &[(&str, &str)] = &[
    ("https://www.youtube.com/s/player/base.js", "script"),
    ("https://en.wikipedia.org/w/load.php", "stylesheet"),
    ("https://github.githubassets.com/assets/app.js", "script"),
    ("https://images.unsplash.com/photo-1", "image"),
    ("https://fonts.gstatic.com/s/roboto/v30/x.woff2", "font"),
    ("https://cdn.jsdelivr.net/npm/react@18.3.1/umd/react.production.min.js", "script"),
];

fn check(engine: *mut NyxEngine, url: &str, kind: &str, source: &str) -> bool {
    unsafe {
        let m = "GET";
        let r = nyx_engine_check(engine, url.as_ptr(), url.len(), source.as_ptr(), source.len(), kind.as_ptr(), kind.len(), m.as_ptr(), m.len());
        r >= 0 && (r & NYX_BLOCK) != 0
    }
}

#[test]
fn starter_list_blocks_common_trackers() {
    unsafe {
        let e = nyx_engine_new(STARTER.as_ptr(), STARTER.len());
        assert!(!e.is_null());
        let mut missed = Vec::new();
        for (u, k) in SHOULD_BLOCK {
            if !check(e, u, k, "https://example-news-site.com/") { missed.push(*u); }
        }
        let rate = 100 - missed.len() * 100 / SHOULD_BLOCK.len();
        println!("starter blocked {}% ({} of {}); missed: {:#?}", rate, SHOULD_BLOCK.len() - missed.len(), SHOULD_BLOCK.len(), missed);
        assert!(rate >= 85, "starter list coverage too low: {rate}%");

        for (u, k) in SHOULD_ALLOW {
            assert!(!check(e, u, k, "https://example-news-site.com/"), "must not block {u}");
        }
        nyx_engine_free(e);
    }
}
