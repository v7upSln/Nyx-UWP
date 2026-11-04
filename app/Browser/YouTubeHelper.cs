using System;

namespace Nyx.Browser
{
    /// <summary>
    /// YouTube handling that works alongside the filter lists: it removes the "ad blockers are not allowed" wall, skips
    /// ads that still get through and hides ad containers. It ensures the main video seamlessly resumes playback without
    /// pausing when an ad is skipped.
    /// </summary>
    public static class YouTubeHelper
    {
        public static bool IsYouTube(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            var h = u.Host.ToLowerInvariant();
            return h == "youtube.com" || h.EndsWith(".youtube.com") || h == "youtu.be" || h == "youtube-nocookie.com" || h.EndsWith(".youtube-nocookie.com");
        }

        public const string Script = @"(function(){try{
if(!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)||window.__nyxYT)return;window.__nyxYT=true;
var css='ytd-enforcement-message-view-model,tp-yt-paper-dialog:has(ytd-enforcement-message-view-model),#masthead-ad,ytd-ad-slot-renderer,'+
'ytd-in-feed-ad-layout-renderer,ytd-banner-promo-renderer,ytd-statement-banner-renderer,ytd-display-ad-renderer,ytd-companion-slot-renderer,'+
'ytd-player-legacy-desktop-watch-ads-renderer,ytd-promoted-sparkles-web-renderer,.ytp-ad-overlay-container,.ytp-ad-text-overlay,#player-ads'+
'{display:none!important}';
function addStyle(){var s=document.createElement('style');s.id='nyx-yt';s.textContent=css;(document.head||document.documentElement).appendChild(s);}
function dropWall(){var w=document.querySelector('ytd-enforcement-message-view-model');if(!w)return;
var box=w.closest('tp-yt-paper-dialog')||w.closest('ytd-popup-container')||w;box.remove();
document.querySelectorAll('tp-yt-iron-overlay-backdrop').forEach(function(b){b.remove();});
document.documentElement.style.overflow='';if(document.body)document.body.style.overflow='';}
var inAd=false,rate=1,muted=false,hadAd=false;
function resume(v, pl){
  setTimeout(function(){
    try{
      if(v&&v.paused){var p=v.play();if(p&&p.catch)p.catch(function(){});}
      if(pl&&typeof pl.playVideo==='function'&&pl.getPlayerState&&pl.getPlayerState()===2){pl.playVideo();}
      var playBtn=document.querySelector('.ytp-play-button');
      if(playBtn&&playBtn.getAttribute('data-title-no-tooltip')==='Play'){playBtn.click();}
    }catch(e){}
  },150);
}
function adTick(){
  var pl=document.querySelector('.html5-video-player');if(!pl)return;var v=pl.querySelector('video');if(!v)return;
  var ad=pl.classList.contains('ad-showing')||pl.classList.contains('ad-interrupting');
  if(ad){
    if(!inAd){inAd=true;hadAd=true;rate=v.playbackRate>1.5?1:v.playbackRate;muted=v.muted;}
    v.muted=true;v.playbackRate=16;if(isFinite(v.duration)&&v.duration>0)v.currentTime=v.duration;
    var sk=document.querySelector('.ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern');if(sk)sk.click();
  }
  else if(inAd){
    inAd=false;v.playbackRate=rate;v.muted=muted;
    resume(v, pl);
  }
  else if(hadAd&&v.currentTime<2&&v.paused){
    hadAd=false;
    resume(v, pl);
  }
  document.querySelectorAll('.ytp-ad-overlay-close-button').forEach(function(b){b.click();});
}
function tick(){dropWall();adTick();}
function start(){addStyle();new MutationObserver(function(){dropWall();}).observe(document.documentElement,{childList:true,subtree:true});setInterval(tick,300);}
if(document.documentElement)start();else document.addEventListener('DOMContentLoaded',start);
}catch(e){}})();";
    }
}
