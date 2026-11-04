using System;
using System.Linq;

namespace Nyx.Browser
{
    public sealed class SearchEngine
    {
        public string Id, Name, Action, Param;
        public SearchEngine(string id, string name, string action, string param) { Id = id; Name = name; Action = action; Param = param; }
        public string Url(string query) => Action + "?" + Param + "=" + Uri.EscapeDataString(query);
    }

    public static class SearchEngines
    {
        public static readonly SearchEngine[] All =
        {
            new SearchEngine("google",     "Google",      "https://www.google.com/search", "q"),
            new SearchEngine("duckduckgo", "DuckDuckGo",  "https://duckduckgo.com/", "q"),
            new SearchEngine("bing",       "Bing",        "https://www.bing.com/search", "q"),
            new SearchEngine("brave",      "Brave Search","https://search.brave.com/search", "q"),
            new SearchEngine("startpage",  "Startpage",   "https://www.startpage.com/do/search", "q"),
            new SearchEngine("ecosia",     "Ecosia",      "https://www.ecosia.org/search", "q"),
            new SearchEngine("yahoo",      "Yahoo",       "https://search.yahoo.com/search", "p"),
            new SearchEngine("qwant",      "Qwant",       "https://www.qwant.com/", "q"),
        };

        public static SearchEngine Current => All.FirstOrDefault(e => e.Id == AppSettings.SearchEngine) ?? All[0];
    }
}
