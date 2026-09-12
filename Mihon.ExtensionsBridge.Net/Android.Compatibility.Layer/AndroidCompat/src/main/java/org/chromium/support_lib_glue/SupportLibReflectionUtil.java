package org.chromium.support_lib_glue;

/**
 * Compatibility stub for the Chromium support-library glue class that Android's
 * WebViewGlueBridge probes via reflection when checking feature support
 * (e.g. `isFeatureSupported("USER_AGENT_METADATA")` -> `Class.forName(
 * "org.chromium.support_lib_glue.SupportLibReflectionUtil")`).
 *
 * The real class lives in the Chromium support-library jar that ships with a full
 * Android WebView implementation. This emulated AndroidCompat layer does not embed
 * that library (we provide our own CEF-backed WebView), but the glue probe still
 * needs the type to be loadable so `isFeatureSupported` returns false instead of
 * throwing ClassNotFoundException. The WebView feature set is not available in this
 * environment; callers therefore see a clean "not supported" result.
 */
public class SupportLibReflectionUtil {
}