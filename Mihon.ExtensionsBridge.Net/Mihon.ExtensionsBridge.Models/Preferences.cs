using System;
using System.Collections.Generic;
using System.Text;

namespace Mihon.ExtensionsBridge.Models
{
    public class FlareSolverrPreferences
    {
        public bool Enabled { get; set; } = false;
        public string Url { get; set; } = "http://localhost:8191";
        public int Timeout { get; set; } = 60;
        public string SessionName { get; set; } = "extension.bridge";
        public int SessionTtl { get; set; } = 15;
        public bool AsResponseFallback { get; set; } = false;
    }
    public class SocksProxyPreferences
    {
        public bool Enabled { get; set; } = false;
        public int Version { get; set; } = 5;
        public string Host { get; set; } = "";
        public int Port { get; set; } = 0;
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
    public class CefPreferences
    {
        /// <summary>
        /// Maximum number of concurrently alive CEF renderer processes (jcef_helper --type=renderer)
        /// across ALL extensions. Bounded by RendererLeaseGate and the WebViewPool.
        /// </summary>
        public int MaxRenderers { get; set; } = 4;

        /// <summary>
        /// Idle timeout (ms) before an unused pooled WebView's browser is destroyed.
        /// </summary>
        public long IdleTimeoutMs { get; set; } = 300_000;

        /// <summary>
        /// Whether WebView pooling/reuse across requests is enabled.
        /// </summary>
        public bool WebViewPoolEnabled { get; set; } = true;

        /// <summary>
        /// Master switch for the embedded JCEF/CEF browser (boot-time). When disabled, JCEF is
        /// never initialized (no ~512 MiB baseline, no CEF CPU overhead) and WebView-requiring
        /// sources fall back to the direct network chain / FlareSolverr. Requires restart.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// CEF message-pump interval (ms) while at least one renderer is alive. Default 10 ms.
        /// </summary>
        public int PumpActiveIntervalMs { get; set; } = 10;

        /// <summary>
        /// CEF message-pump interval (ms) when no renderer is alive. Default 500 ms - this is the
        /// idle backoff that eliminates the perpetual 100 Hz busy-poll CPU burn (issue #87).
        /// </summary>
        public int PumpIdleIntervalMs { get; set; } = 500;
    }

    public class Preferences
    {
        public FlareSolverrPreferences FlareSolverr { get; set; } = new FlareSolverrPreferences();

        public SocksProxyPreferences SocksProxy { get; set; } = new SocksProxyPreferences();

        public CefPreferences Cef { get; set; } = new CefPreferences();

        public Dictionary<string, Dictionary<string, string>> Interceptors = [];
    }
}
