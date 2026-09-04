using extension.bridge.cef;

namespace Mihon.ExtensionsBridge.Core.Utilities
{
    /// <summary>
    /// Provides a C#-friendly wrapper for driving the CEF message pump
    /// from an external timer (e.g. Avalonia DispatcherTimer).
    /// This avoids the reverse-JNI crash that occurs when CEF native threads
    /// call GetEnv() on IKVM-unattached threads.
    /// </summary>
    public static class CefPumpBridge
    {
        private static readonly object _lock = new();
        private static org.cef.CefApp? _cachedApp;

        /// <summary>
        /// Returns true if JCEF has been initialized and the pump can be started.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                if (_cachedApp != null)
                    return true;

                lock (_lock)
                {
                    if (_cachedApp != null)
                        return true;

                    try
                    {
                        _cachedApp = CefAppBridge.INSTANCE.getSharedApp();
                    }
                    catch
                    {
                        return false;
                    }

                    return _cachedApp != null;
                }
            }
        }

        /// <summary>
        /// Performs one iteration of CEF message loop work.
        /// Must be called on a thread that is attached to IKVM (e.g. Avalonia UI thread).
        /// </summary>
        public static void PumpWork()
        {
            var app = _cachedApp;
            if (app == null)
            {
                // Try to resolve once more
                lock (_lock)
                {
                    if (_cachedApp == null)
                    {
                        try
                        {
                            _cachedApp = CefAppBridge.INSTANCE.getSharedApp();
                        }
                        catch
                        {
                            // JCEF not loaded
                            return;
                        }
                    }
                    app = _cachedApp;
                }

                if (app == null)
                    return;
            }

            CefMessageLoopBridge.INSTANCE.pumpWork(app, 0L);
        }

        /// <summary>
        /// Returns the current adaptive pump cadence in milliseconds.
        /// While browsers are alive this is the active interval (10 ms default); when no
        /// renderer is alive it backs off to the idle interval (500 ms default). The desktop
        /// DispatcherTimer uses this to match the internal pump cadence so an idle process
        /// stops burning ~11% of a core on a permanent 100 Hz poll (issue #87).
        /// </summary>
        public static long CurrentIntervalMs()
        {
            try
            {
                return CefMessageLoopBridge.INSTANCE.currentIntervalMs();
            }
            catch
            {
                return 500L;
            }
        }

        /// <summary>
        /// Invalidates the cached CefApp reference (e.g. during shutdown).
        /// </summary>
        public static void Invalidate()
        {
            lock (_lock)
            {
                _cachedApp = null;
            }
        }
    }
}