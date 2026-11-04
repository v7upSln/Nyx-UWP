using System;

namespace Nyx.Browser
{
    /// <summary>
    /// Compatibility helpers for video streaming and dynamic web applications.
    /// Ensures video players mount smoothly, bypasses ad-splash gates, suppresses popups, and prevents CSRF / stale session drops.
    /// </summary>
    public static class SiteCompatHelper
    {
        public static bool IsStreamingOrMediaUrl(string url, string pageUrl)
        {
            if (string.IsNullOrEmpty(url)) return false;

            // Video player / embed hosting domains used across streaming platforms
            if (url.IndexOf("share4max.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("mp4upload.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("videa.hu", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("4shared.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("mega.nz", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("hgcloud.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("workupload.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("gofile.io", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("mediafire.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("dood", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("streamtape", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("filemoon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("vidmoly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("streamwish", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("uqload", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("soraplay", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // Internal streaming API endpoints, manifests, gates, or AJAX on video platforms
            if (url.IndexOf("/watch/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("/sources", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("stream-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("download-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("admin-ajax.php", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("livewire", StringComparison.OrdinalIgnoreCase) >= 0 ||
                url.IndexOf("/build/assets/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        public static readonly string Script =
            "(function(){\n" +
            "  try{\n" +
            "    if(window.__nyxCompatLoaded)return;\n" +
            "    window.__nyxCompatLoaded=true;\n" +
            "\n" +
            "    // Suppress popups, popunders and clickjacking window.open calls\n" +
            "    var origOpen=window.open;\n" +
            "    window.open=function(url,target,features){\n" +
            "      if(typeof url==='string'){\n" +
            "        var u=url.toLowerCase();\n" +
            "        if(u==='about:blank'||u===''||\n" +
            "           u.indexOf('popads')!==-1||u.indexOf('popcash')!==-1||\n" +
            "           u.indexOf('onclick')!==-1||u.indexOf('syndication')!==-1||\n" +
            "           u.indexOf('adsterra')!==-1||u.indexOf('monetag')!==-1||\n" +
            "           u.indexOf('bet')!==-1||u.indexOf('traffic')!==-1||\n" +
            "           u.indexOf('agl006')!==-1||u.indexOf('lureful')!==-1||\n" +
            "           u.indexOf('wingsmob')!==-1||u.indexOf('dtscout')!==-1||\n" +
            "           u.indexOf('redirect')!==-1){\n" +
            "          return null;\n" +
            "        }\n" +
            "      }\n" +
            "      return origOpen.apply(this,arguments);\n" +
            "    };\n" +
            "\n" +
            "    function getMetaCsrf(){\n" +
            "      var m=document.querySelector('meta[name=\"csrf-token\"]');\n" +
            "      return m?m.getAttribute('content'):'';\n" +
            "    }\n" +
            "\n" +
            "    function setMetaCsrf(token){\n" +
            "      var m=document.querySelector('meta[name=\"csrf-token\"]');\n" +
            "      if(m) m.setAttribute('content',token);\n" +
            "    }\n" +
            "\n" +
            "    async function refreshCsrfToken(){\n" +
            "      try{\n" +
            "        var resp=await (window.__nyxOrigFetch||window.fetch)(window.location.href,{cache:'no-store'});\n" +
            "        var html=await resp.text();\n" +
            "        var match=html.match(/<meta[^>]+name=[\"']csrf-token[\"'][^>]+content=[\"']([^\"']+)[\"']/i) ||\n" +
            "                  html.match(/<meta[^>]+content=[\"']([^\"']+)[\"'][^>]+name=[\"']csrf-token[\"']/i);\n" +
            "        if(match&&match[1]){\n" +
            "          setMetaCsrf(match[1]);\n" +
            "          return match[1];\n" +
            "        }\n" +
            "      }catch(e){}\n" +
            "      return null;\n" +
            "    }\n" +
            "\n" +
            "    var origFetch=window.fetch;\n" +
            "    window.__nyxOrigFetch=origFetch;\n" +
            "\n" +
            "    if(typeof origFetch==='function'){\n" +
            "      window.fetch=async function(url,options){\n" +
            "        var urlStr=typeof url==='string'?url:(url&&url.url?url.url:'');\n" +
            "        var isStreamApi=urlStr.indexOf('/sources')!==-1||\n" +
            "                        urlStr.indexOf('stream-')!==-1||\n" +
            "                        urlStr.indexOf('download-')!==-1||\n" +
            "                        urlStr.indexOf('admin-ajax.php')!==-1||\n" +
            "                        urlStr.indexOf('livewire')!==-1;\n" +
            "\n" +
            "        if(isStreamApi){\n" +
            "          var token=getMetaCsrf();\n" +
            "          var newOpts=Object.assign({},options||{});\n" +
            "          var hdrs=newOpts.headers;\n" +
            "          if(!hdrs) hdrs={};\n" +
            "          if(typeof Headers!=='undefined'&&hdrs instanceof Headers){\n" +
            "            if(token&&!hdrs.has('X-CSRF-TOKEN')) hdrs.set('X-CSRF-TOKEN',token);\n" +
            "            if(!hdrs.has('X-Requested-With')) hdrs.set('X-Requested-With','XMLHttpRequest');\n" +
            "          } else if(typeof hdrs==='object'){\n" +
            "            if(token&&!hdrs['X-CSRF-TOKEN']) hdrs['X-CSRF-TOKEN']=token;\n" +
            "            if(!hdrs['X-Requested-With']) hdrs['X-Requested-With']='XMLHttpRequest';\n" +
            "          }\n" +
            "          newOpts.headers=hdrs;\n" +
            "          options=newOpts;\n" +
            "        }\n" +
            "\n" +
            "        var res=await origFetch.apply(this,[url,options]);\n" +
            "\n" +
            "        if(isStreamApi&&(res.status===419||res.status===404)){\n" +
            "          try{\n" +
            "            var freshToken=await refreshCsrfToken();\n" +
            "            if(freshToken){\n" +
            "              var retryOpts=Object.assign({},options||{});\n" +
            "              var retryHdrs=retryOpts.headers||{};\n" +
            "              if(typeof Headers!=='undefined'&&retryHdrs instanceof Headers){\n" +
            "                retryHdrs.set('X-CSRF-TOKEN',freshToken);\n" +
            "                retryHdrs.set('X-Requested-With','XMLHttpRequest');\n" +
            "              } else if(typeof retryHdrs==='object'){\n" +
            "                retryHdrs['X-CSRF-TOKEN']=freshToken;\n" +
            "                retryHdrs['X-Requested-With']='XMLHttpRequest';\n" +
            "              }\n" +
            "              retryOpts.headers=retryHdrs;\n" +
            "              res=await origFetch.apply(this,[url,retryOpts]);\n" +
            "            }\n" +
            "          }catch(e){}\n" +
            "        }\n" +
            "        return res;\n" +
            "      };\n" +
            "    }\n" +
            "\n" +
            "    function fixSplashGate(){\n" +
            "      var splash=document.getElementById('watch-splash');\n" +
            "      if(splash&&!splash.classList.contains('wa-splash-dismissed')){\n" +
            "        var playBtn=splash.querySelector('.sz-play-button')||splash;\n" +
            "        playBtn.click();\n" +
            "      }\n" +
            "    }\n" +
            "\n" +
            "    function fixPlayerStart(){\n" +
            "      var startBtn=document.querySelector('[data-testid=\"player-start\"], [data-frame-action=\"play\"]');\n" +
            "      if(startBtn&&startBtn.offsetParent!==null&&!startBtn.disabled){\n" +
            "        startBtn.click();\n" +
            "      }\n" +
            "    }\n" +
            "\n" +
            "    function fixStaleOverlay(){\n" +
            "      var btn=document.querySelector('[data-frame-action=\"refresh-sources\"], [data-frame-action=\"retry-sources\"], #wa-watch-retry');\n" +
            "      if(btn&&btn.offsetParent!==null){\n" +
            "        btn.click();\n" +
            "      }\n" +
            "    }\n" +
            "\n" +
            "    function clearInvisibleAdOverlays(){\n" +
            "      var elements=document.querySelectorAll('div, a');\n" +
            "      for(var i=0;i<elements.length;i++){\n" +
            "        var el=elements[i];\n" +
            "        var z=parseInt(window.getComputedStyle(el).zIndex,10);\n" +
            "        if(z>999&&el.offsetWidth>window.innerWidth*0.7&&el.offsetHeight>window.innerHeight*0.7){\n" +
            "          var op=parseFloat(window.getComputedStyle(el).opacity);\n" +
            "          if(op<0.1){\n" +
            "            el.remove();\n" +
            "          }\n" +
            "        }\n" +
            "      }\n" +
            "    }\n" +
            "\n" +
            "    function runCompatChecks(){\n" +
            "      fixSplashGate();\n" +
            "      fixPlayerStart();\n" +
            "      fixStaleOverlay();\n" +
            "      clearInvisibleAdOverlays();\n" +
            "    }\n" +
            "\n" +
            "    if(document.readyState==='loading'){\n" +
            "      document.addEventListener('DOMContentLoaded',function(){\n" +
            "        runCompatChecks();\n" +
            "        setTimeout(runCompatChecks,600);\n" +
            "        setTimeout(runCompatChecks,1500);\n" +
            "        setTimeout(runCompatChecks,3000);\n" +
            "      });\n" +
            "    } else {\n" +
            "      runCompatChecks();\n" +
            "      setTimeout(runCompatChecks,600);\n" +
            "      setTimeout(runCompatChecks,1500);\n" +
            "      setTimeout(runCompatChecks,3000);\n" +
            "    }\n" +
            "\n" +
            "    if(document.documentElement){\n" +
            "      new MutationObserver(function(){\n" +
            "        runCompatChecks();\n" +
            "      }).observe(document.documentElement,{childList:true,subtree:true});\n" +
            "    }\n" +
            "  }catch(e){}\n" +
            "})();";
    }
}
