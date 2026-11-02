using System;
using System.Collections.Generic;
using System.Linq;

namespace MihomoLauncher.Services
{
    // Guesses the availability-check URL for well-known services from the app's name
    // or process/exe name (claude.exe -> claude.ai, ChatGPT.exe -> chatgpt.com, …).
    public static class KnownServices
    {
        // key (substring, lower-case) -> check URL. Order matters: first match wins.
        private static readonly (string key, string url)[] Map =
        {
            ("chatgpt",    "https://chatgpt.com/"),
            ("openai",     "https://chatgpt.com/"),
            ("claude",     "https://claude.ai/"),
            ("anthropic",  "https://claude.ai/"),
            ("gemini",     "https://gemini.google.com/"),
            ("copilot",    "https://copilot.microsoft.com/"),
            ("perplexity", "https://www.perplexity.ai/"),
            ("deepseek",   "https://chat.deepseek.com/"),
            ("qwen",       "https://chat.qwen.ai/"),
            ("grok",       "https://grok.com/"),
            ("mistral",    "https://chat.mistral.ai/"),
            ("instagram",  "https://www.instagram.com/"),
            ("youtube",    "https://www.youtube.com/"),
            ("telegram",   "https://web.telegram.org/"),
            ("whatsapp",   "https://web.whatsapp.com/"),
            ("discord",    "https://discord.com/app"),
            ("spotify",    "https://open.spotify.com/"),
            ("facebook",   "https://www.facebook.com/"),
            ("twitter",    "https://x.com/"),
            ("tiktok",     "https://www.tiktok.com/"),
            ("reddit",     "https://www.reddit.com/"),
            ("notion",     "https://www.notion.so/"),
            ("twitch",     "https://www.twitch.tv/"),
        };

        // Best-effort check URL from the app name and/or path (exe name or URL).
        // Returns "" if nothing recognised.
        public static string GuessCheckUrl(string name, string path)
        {
            var hay = new List<string>();
            if (!string.IsNullOrWhiteSpace(name)) hay.Add(name.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        hay.Add(path.ToLowerInvariant());
                    else
                        hay.Add(System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant());
                }
                catch { }
            }
            if (hay.Count == 0) return "";

            foreach (var entry in Map)
                if (hay.Any(h => h.IndexOf(entry.key, StringComparison.Ordinal) >= 0))
                    return entry.url;
            return "";
        }
    }
}
