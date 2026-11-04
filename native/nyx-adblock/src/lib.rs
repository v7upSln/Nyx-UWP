//! Nyx native shield: C ABI around Brave's adblock-rust (adblock 0.13).
//!
//! ABI rules
//!  * Strings are UTF-8 as (ptr, len) pairs; no NUL terminator required.
//!  * Strings returned (`*mut c_char`) are freed with `nyx_string_free`.
//!  * Byte buffers returned (`nyx_engine_serialize`) are freed with `nyx_buffer_free`.
//!  * Engines are freed with `nyx_engine_free`. The engine is Send + Sync here
//!    (the `single-thread` feature is OFF), so calls may come from any thread.
//!  * panic = "abort": nothing unwinds across the boundary.

use adblock::lists::{FilterSet, ParseOptions};
use adblock::request::Request;
use adblock::resources::{PermissionMask, Resource};
use adblock::Engine;
use std::ffi::CString;
use std::os::raw::c_char;
use std::slice;

pub struct NyxEngine {
    engine: Engine,
}

// Bit flags returned by nyx_engine_check (>= 0) or a negative error code.
pub const NYX_BLOCK: i32 = 1; // engine says: block this request
pub const NYX_EXCEPTION: i32 = 2; // an @@ rule matched
pub const NYX_REDIRECT: i32 = 4; // a redirect resource applies
pub const NYX_REWRITE: i32 = 8; // $removeparam style rewrite applies
pub const NYX_BAD_REQUEST: i32 = -1;
pub const NYX_NULL_ARG: i32 = -2;

const VERSION: &str = concat!("nyx-adblock ", env!("CARGO_PKG_VERSION"), "\0");

unsafe fn utf8<'a>(ptr: *const u8, len: usize) -> Option<&'a str> {
    if ptr.is_null() {
        return None;
    }
    std::str::from_utf8(slice::from_raw_parts(ptr, len)).ok()
}

fn to_c(s: String) -> *mut c_char {
    let cleaned: Vec<u8> = s.into_bytes().into_iter().filter(|b| *b != 0).collect();
    CString::new(cleaned).map(CString::into_raw).unwrap_or(std::ptr::null_mut())
}

fn build_engine(list_text: String) -> Engine {
    let mut set = FilterSet::new(false);
    set.add_filter_list(list_text, ParseOptions::default());
    Engine::new_with_filter_set(set)
}

/// Static NUL-terminated version string. Do not free.
#[no_mangle]
pub extern "C" fn nyx_version() -> *const c_char {
    VERSION.as_ptr() as *const c_char
}

/// Compile newline-separated ABP/uBO rules into an engine. NULL on bad input.
#[no_mangle]
pub unsafe extern "C" fn nyx_engine_new(rules: *const u8, len: usize) -> *mut NyxEngine {
    let Some(text) = utf8(rules, len) else { return std::ptr::null_mut() };
    let engine = build_engine(text.to_owned());
    Box::into_raw(Box::new(NyxEngine { engine }))
}

/// Load an engine from bytes produced by `nyx_engine_serialize` (fast startup).
/// NULL if the data is invalid or from an incompatible version.
#[no_mangle]
pub unsafe extern "C" fn nyx_engine_deserialize(data: *const u8, len: usize) -> *mut NyxEngine {
    if data.is_null() {
        return std::ptr::null_mut();
    }
    let mut engine = Engine::default();
    match engine.deserialize(slice::from_raw_parts(data, len)) {
        Ok(()) => Box::into_raw(Box::new(NyxEngine { engine })),
        Err(_) => std::ptr::null_mut(),
    }
}

/// Serialize the compiled engine. Free the buffer with `nyx_buffer_free(ptr, *out_len)`.
#[no_mangle]
pub unsafe extern "C" fn nyx_engine_serialize(engine: *const NyxEngine, out_len: *mut usize) -> *mut u8 {
    if engine.is_null() || out_len.is_null() {
        return std::ptr::null_mut();
    }
    let boxed = (*engine).engine.serialize().into_boxed_slice();
    *out_len = boxed.len();
    Box::into_raw(boxed) as *mut u8
}

#[no_mangle]
pub unsafe extern "C" fn nyx_buffer_free(ptr: *mut u8, len: usize) {
    if !ptr.is_null() {
        drop(Box::from_raw(slice::from_raw_parts_mut(ptr, len)));
    }
}

#[no_mangle]
pub unsafe extern "C" fn nyx_engine_free(engine: *mut NyxEngine) {
    if !engine.is_null() {
        drop(Box::from_raw(engine));
    }
}

/// Check one network request. `request_type` uses adblock-rust names:
/// script, image, stylesheet, xmlhttprequest, sub_frame, main_frame, media,
/// font, ping, websocket, other. `method` is e.g. "GET".
#[no_mangle]
pub unsafe extern "C" fn nyx_engine_check(
    engine: *const NyxEngine,
    url: *const u8, url_len: usize,
    source_url: *const u8, source_len: usize,
    request_type: *const u8, type_len: usize,
    method: *const u8, method_len: usize,
) -> i32 {
    if engine.is_null() {
        return NYX_NULL_ARG;
    }
    let (Some(url), Some(source), Some(kind), Some(method)) = (
        utf8(url, url_len),
        utf8(source_url, source_len),
        utf8(request_type, type_len),
        utf8(method, method_len),
    ) else {
        return NYX_BAD_REQUEST;
    };
    let Ok(request) = Request::new(url, source, kind, method) else {
        return NYX_BAD_REQUEST;
    };
    let r = (*engine).engine.check_network_request(&request);
    let mut flags = 0;
    if r.should_block() {
        flags |= NYX_BLOCK;
    }
    if r.exception.is_some() {
        flags |= NYX_EXCEPTION;
    }
    if r.redirect.is_some() {
        flags |= NYX_REDIRECT;
    }
    if r.rewritten_url.is_some() {
        flags |= NYX_REWRITE;
    }
    flags
}

/// Cosmetic resources for a page URL as JSON:
/// {"css":"...","script":"...","generichide":false,"procedural":["..."]}
/// Free with `nyx_string_free`. NULL on error.
#[no_mangle]
pub unsafe extern "C" fn nyx_engine_cosmetic_json(
    engine: *const NyxEngine,
    url: *const u8,
    url_len: usize,
) -> *mut c_char {
    if engine.is_null() {
        return std::ptr::null_mut();
    }
    let Some(url) = utf8(url, url_len) else { return std::ptr::null_mut() };
    let res = (*engine).engine.url_cosmetic_resources(url);

    let mut sel: Vec<&str> = res.hide_selectors.iter().map(|s| s.as_str()).collect();
    sel.sort_unstable();
    let css = if sel.is_empty() {
        String::new()
    } else {
        format!("{}{{display:none!important}}", sel.join(",\n"))
    };
    let mut procedural: Vec<&str> = res.procedural_actions.iter().map(|s| s.as_str()).collect();
    procedural.sort_unstable();

    let json = serde_json::json!({
        "css": css,
        "script": res.injected_script,
        "generichide": res.generichide,
        "procedural": procedural,
    });
    to_c(json.to_string())
}

#[no_mangle]
pub unsafe extern "C" fn nyx_string_free(s: *mut c_char) {
    if !s.is_null() {
        drop(CString::from_raw(s));
    }
}


// ---------------------------------------------------------------------------------------------
// Multi-list builder with per-list permissions, plus scriptlet resources.
// ---------------------------------------------------------------------------------------------

/// Collects lists before compiling them. Each list can carry a permission mask; bit 1 lets a list
/// use "trusted" scriptlets (this is how Brave treats uBlock Origin's own lists).
pub struct NyxListSet {
    set: FilterSet,
}

#[no_mangle]
pub extern "C" fn nyx_set_new() -> *mut NyxListSet {
    Box::into_raw(Box::new(NyxListSet { set: FilterSet::new(false) }))
}

/// Add one list (UTF-8 text). Returns 0 on success, negative on error.
#[no_mangle]
pub unsafe extern "C" fn nyx_set_add(set: *mut NyxListSet, rules: *const u8, len: usize, permission: u8) -> i32 {
    if set.is_null() {
        return NYX_NULL_ARG;
    }
    let Some(text) = utf8(rules, len) else { return NYX_BAD_REQUEST };
    let opts = ParseOptions { permissions: PermissionMask::from_bits(permission), ..ParseOptions::default() };
    (*set).set.add_filter_list(text.to_owned(), opts);
    0
}

/// Compile the collected lists into an engine. Consumes (frees) the set. NULL on error.
#[no_mangle]
pub unsafe extern "C" fn nyx_set_build(set: *mut NyxListSet) -> *mut NyxEngine {
    if set.is_null() {
        return std::ptr::null_mut();
    }
    let boxed = Box::from_raw(set);
    let engine = Engine::new_with_filter_set(boxed.set);
    Box::into_raw(Box::new(NyxEngine { engine }))
}

#[no_mangle]
pub unsafe extern "C" fn nyx_set_free(set: *mut NyxListSet) {
    if !set.is_null() {
        drop(Box::from_raw(set));
    }
}

/// Load scriptlet / redirect resources (JSON array, see tools/build-resources).
/// Not serialized with the engine: call this after every nyx_engine_new / nyx_engine_deserialize /
/// nyx_set_build. Returns the number of resources loaded, or a negative error code.
#[no_mangle]
pub unsafe extern "C" fn nyx_engine_use_resources(engine: *mut NyxEngine, json: *const u8, len: usize) -> i32 {
    if engine.is_null() {
        return NYX_NULL_ARG;
    }
    let Some(text) = utf8(json, len) else { return NYX_BAD_REQUEST };
    let Ok(resources) = serde_json::from_str::<Vec<Resource>>(text) else { return NYX_BAD_REQUEST };
    let n = resources.len() as i32;
    (*engine).engine.use_resources(resources);
    n
}

const SELFTEST_RULES: &str = "\
||ads.example.com^
||doubleclick.net^
||tracker.test^$third-party
@@||ads.example.com/allowed^
example.com##.ad-banner
";

/// Built-in self test: 0 = OK, otherwise the number of the first failing check.
#[no_mangle]
pub extern "C" fn nyx_selftest() -> i32 {
    let engine = build_engine(SELFTEST_RULES.to_owned());
    let block = |url: &str, src: &str, kind: &str| -> Option<(bool, bool)> {
        let r = Request::new(url, src, kind, "GET").ok()?;
        let res = engine.check_network_request(&r);
        Some((res.should_block(), res.exception.is_some()))
    };
    if block("https://ads.example.com/x.js", "https://news.test/", "script") != Some((true, false)) { return 1; }
    if block("https://news.test/app.js", "https://news.test/", "script") != Some((false, false)) { return 2; }
    if block("https://ads.example.com/allowed/a.js", "https://news.test/", "script") != Some((false, true)) { return 3; }
    if block("https://tracker.test/p.gif", "https://other.test/", "image") != Some((true, false)) { return 4; }
    if block("https://tracker.test/p.gif", "https://tracker.test/", "image") != Some((false, false)) { return 5; }
    if !engine.url_cosmetic_resources("https://example.com/p").hide_selectors.contains(".ad-banner") { return 6; }
    if engine.url_cosmetic_resources("https://unrelated.test/").hide_selectors.contains(".ad-banner") { return 7; }
    0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn selftest_passes() {
        assert_eq!(nyx_selftest(), 0);
    }

    #[test]
    fn ffi_roundtrip_and_serialize() {
        unsafe {
            let rules = "||ads.example.com^\nexample.com##.ad-banner";
            let e = nyx_engine_new(rules.as_ptr(), rules.len());
            assert!(!e.is_null());

            // serialize -> deserialize -> same behaviour
            let mut n = 0usize;
            let buf = nyx_engine_serialize(e, &mut n);
            assert!(!buf.is_null() && n > 0);
            let e2 = nyx_engine_deserialize(buf, n);
            nyx_buffer_free(buf, n);
            nyx_engine_free(e);
            assert!(!e2.is_null());

            let (u, s, k, m) = ("https://ads.example.com/a.js", "https://site.test/", "script", "GET");
            let r = nyx_engine_check(e2, u.as_ptr(), u.len(), s.as_ptr(), s.len(), k.as_ptr(), k.len(), m.as_ptr(), m.len());
            assert_eq!(r & NYX_BLOCK, NYX_BLOCK);

            let page = "https://example.com/";
            let j = nyx_engine_cosmetic_json(e2, page.as_ptr(), page.len());
            let text = std::ffi::CStr::from_ptr(j).to_str().unwrap().to_owned();
            nyx_string_free(j);
            assert!(text.contains(".ad-banner"), "{text}");
            nyx_engine_free(e2);
        }
    }


    const RESOURCES: &str = include_str!("../tests/data/resources.json");
    const YT_RULES: &str = "\
youtube.com##+js(set-constant, ytInitialPlayerResponse.adPlacements, undefined)
youtube.com##+js(json-prune, playerResponse.adPlacements playerResponse.playerAds adPlacements playerAds)
www.youtube.com##+js(trusted-replace-fetch-response, /\"adPlacements.*?\"\\}\\}\\}\\]\\,/, , player?)
";

    fn script_for(permission: u8, page: &str) -> String {
        unsafe {
            let set = nyx_set_new();
            assert_eq!(nyx_set_add(set, YT_RULES.as_ptr(), YT_RULES.len(), permission), 0);
            let e = nyx_set_build(set);
            assert!(!e.is_null());
            let n = nyx_engine_use_resources(e, RESOURCES.as_ptr(), RESOURCES.len());
            assert!(n > 100, "resources loaded: {n}");
            let j = nyx_engine_cosmetic_json(e, page.as_ptr(), page.len());
            let text = std::ffi::CStr::from_ptr(j).to_str().unwrap().to_owned();
            nyx_string_free(j);
            nyx_engine_free(e);
            let v: serde_json::Value = serde_json::from_str(&text).unwrap();
            v["script"].as_str().unwrap().to_owned()
        }
    }

    #[test]
    fn youtube_scriptlets_are_generated() {
        let script = script_for(0, "https://www.youtube.com/watch?v=x");
        assert!(script.contains("function setConstant"), "set-constant missing");
        assert!(script.contains("function jsonPrune"), "json-prune missing");
        assert!(script.contains("scriptletGlobals"), "globals shim missing");
        assert!(!script.contains("trustedReplaceFetchResponse"), "trusted scriptlet must NOT run for an untrusted list");
        std::fs::write(std::env::temp_dir().join("nyx_yt_script_untrusted.js"), &script).unwrap();

        let trusted = script_for(1, "https://www.youtube.com/watch?v=x");
        assert!(trusted.contains("trustedReplaceFetchResponse"), "trusted scriptlet missing for trusted list");
        std::fs::write(std::env::temp_dir().join("nyx_yt_script.js"), &trusted).unwrap();
        assert!(script_for(0, "https://example.org/").is_empty(), "no scriptlets on other sites");
    }

    #[test]
    fn bad_input_is_rejected() {
        unsafe {
            assert!(nyx_engine_new(std::ptr::null(), 0).is_null());
            assert_eq!(nyx_engine_check(std::ptr::null(), std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null(), 0, std::ptr::null(), 0), NYX_NULL_ARG);
        }
    }
}
