/**
 * Renders the OAuth implicit-flow capture page.
 *
 * Served by GET /api/oauth/:provider/callback when the provider uses the
 * implicit grant (e.g. AniList). AniList redirects the browser here with the
 * access token in the URL fragment (e.g. #access_token=...&expires_in=...),
 * which never reaches the server.
 *
 * This page:
 *   1. Parses location.hash for access_token / expires_in.
 *   2. Resolves the session `state` from location.search (state was embedded in
 *      the redirect_uri query) , or via a postMessage request to the opener
 *      (the Rensaio frontend) as a fallback.
 *   3. POSTs { state, accessToken, expiresAt } to the proxy's
 *      POST /api/oauth/:provider/callback endpoint for persistence.
 *   4. Notifies the opener via window.opener.postMessage({type:'oauth-success'...}).
 */
export function renderImplicitCaptureHtml(
  providerName: string,
  provider: string,
  state: string
): string {
  const safeProviderName = escapeHtml(providerName);
  const safeProvider = escapeJsString(provider);
  const safeState = escapeJsString(state);

  return `<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Rensaiō &mdash; Complete</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;background:hsl(20,14.3%,4.1%);color:hsl(0,0%,95%)}
@media(prefers-color-scheme:light){body{background:hsl(0,0%,100%);color:hsl(240,10%,3.9%)}.card{background:hsl(180,8.2%,90.2%)}}
.card{background:hsl(24,9.8%,10%);border-radius:12px;padding:2.5rem 3rem;text-align:center;max-width:420px;box-shadow:0 4px 24px rgba(0,0,0,0.3)}
.logo{font-size:1.5rem;font-weight:700;letter-spacing:-0.02em;color:hsl(346.8,77.2%,49.8%);margin-bottom:1.25rem}
.logo span{color:hsl(0,0%,95%)}
@media(prefers-color-scheme:light){.logo span{color:hsl(240,10%,3.9%)}}
.spinner{width:48px;height:48px;border-radius:50%;border:3px solid hsl(346.8,77.2%,49.8%);border-top-color:transparent;display:inline-block;margin-bottom:1rem;animation:spin 1s linear infinite}
@keyframes spin{to{transform:rotate(360deg)}}
h1{font-size:1.125rem;font-weight:600;margin-bottom:0.5rem}
p{font-size:0.875rem;opacity:0.7;margin-bottom:1rem}
.ok{width:48px;height:48px;border-radius:50%;border:3px solid hsl(346.8,77.2%,49.8%);display:inline-flex;align-items:center;justify-content:center;margin-bottom:1rem}
.ok::after{content:'';display:block;width:14px;height:24px;border:solid hsl(346.8,77.2%,49.8%);border-width:0 3px 3px 0;transform:rotate(45deg) translateY(-2px)}
.err{width:48px;height:48px;border-radius:50%;border:3px solid hsl(0,84.2%,60.2%);display:inline-flex;align-items:center;justify-content:center;margin-bottom:1rem;color:hsl(0,84.2%,60.2%);font-size:1.75rem;font-weight:700;line-height:1}
.pill{display:inline-block;background:hsl(346.8,77.2%,49.8%);color:hsl(355.7,100%,97.3%);font-size:0.75rem;font-weight:600;padding:0.25rem 0.75rem;border-radius:999px;text-transform:uppercase;letter-spacing:0.04em}
.hint{font-size:0.75rem;opacity:0.4;margin-top:1.5rem}
</style></head><body>
<div class="card" id="card">
<div class="logo">Rensaiō</span></div>
<div class="spinner" id="mark"></div>
<h1>Completing connection&hellip;</h1>
<p>Your ${safeProviderName} account is being connected to Rensaiō.</p>
<p class="hint">You may close this window.</p>
</div>
<script>
(function () {
  var PROVIDER = '${safeProvider}';
  var INITIAL_STATE = '${safeState}';
  var card = document.getElementById('card');
  var mark = document.getElementById('mark');

  function paramsFromHash() {
    var p = {};
    var raw = location.hash; // e.g. #access_token=...&token_type=Bearer&expires_in=...
    if (raw && raw.indexOf('#') === 0) raw = raw.substring(1);
    if (!raw) return p;
    raw.split('&').forEach(function (kv) {
      var i = kv.indexOf('=');
      if (i < 0) return;
      p[decodeURIComponent(kv.substring(0, i))] = decodeURIComponent(kv.substring(i + 1));
    });
    return p;
  }

  function paramsFromSearch() {
    var p = {};
    var raw = location.search; // ?state=... — AniList preserves redirect_uri query
    if (raw && raw.indexOf('?') === 0) raw = raw.substring(1);
    if (!raw) return p;
    raw.split('&').forEach(function (kv) {
      var i = kv.indexOf('=');
      if (i < 0) return;
      p[decodeURIComponent(kv.substring(0, i))] = decodeURIComponent(kv.substring(i + 1));
    });
    return p;
  }

  function showOk() {
    mark.outerHTML = '<div class="ok"></div>';
    card.querySelector('h1').textContent = 'Authentication Complete';
    card.querySelector('p').textContent = 'Your ' + PROVIDER + ' account has been connected to Rensaiō.';
  }

  function showError(msg) {
    mark.outerHTML = '<div class="err">&#10005;</div>';
    card.querySelector('h1').textContent = 'Authentication Failed';
    card.querySelector('p').textContent = msg || 'Could not connect your ' + PROVIDER + ' account to Rensaiō.';
    var pill = document.createElement('div'); pill.className = 'pill'; pill.textContent = 'Failed';
    card.appendChild(pill);
  }

  function notifyOpener(type) {
    try { if (window.opener) window.opener.postMessage({ type: type, provider: PROVIDER, state: window.__state }, '*'); } catch (e) {}
  }

  function persist(state, token, expiresAt) {
    window.__state = state;
    fetch('/api/oauth/' + PROVIDER + '/callback', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ state: state, accessToken: token, expiresAt: expiresAt })
    })
      .then(function (r) {
        if (r.ok) {
          showOk();
          notifyOpener('oauth-success');
        } else {
          throw new Error('HTTP ' + r.status);
        }
      })
      .catch(function (e) {
        console.error('Implicit capture failed:', e);
        showError('Could not save your connection. Please try again.');
        notifyOpener('oauth-error');
      });
  }

  // 1. Extract token from fragment
  var hash = paramsFromHash();
  var token = hash['access_token'] || '';
  if (!token) {
    showError('No access token was returned by ' + PROVIDER + '.');
    notifyOpener('oauth-error');
    return;
  }

  var expiresIn = parseInt(hash['expires_in'] || '0', 10);
  var expiresAt = expiresIn > 0
    ? new Date(Date.now() + expiresIn * 1000).toISOString()
    : null;

  // 2. Resolve session state.
  //    AniList echoes state back INSIDE the URL fragment (e.g.
  //    #access_token=...&state=...), so prefer it from the hash. Fall back to
  //    the query string (some implicit providers), then the state the proxy
  //    embedded when serving this page, then a postMessage round-trip.
  var resolvedState = (hash['state'] || paramsFromSearch()['state'] || INITIAL_STATE || '').trim();
  if (resolvedState) {
    persist(resolvedState, token, expiresAt);
    return;
  }

  // 3. Fallback: ask the opener (frontend) for the state it already holds
  try {
    if (window.opener) {
      window.opener.postMessage(
        { type: 'oauth-state-request', provider: PROVIDER, requestId: 'state-' + Date.now() },
        '*'
      );
    }
  } catch (e) {}

  var waited = 0;
  var timer = setInterval(function () {
    waited += 250;
    if (window.__state) {
      clearInterval(timer);
      persist(window.__state, token, expiresAt);
    } else if (waited >= 5000) {
      clearInterval(timer);
      showError('Could not verify your connection session. Please try again.');
      notifyOpener('oauth-error');
    }
  }, 250);

  // Listener for the opener's reply
  window.addEventListener('message', function (ev) {
    var d = ev.data;
    if (d && (d.type === 'oauth-state-response') && d.state) {
      window.__state = d.state;
    }
  });
})();
</script>
</body></html>`;
}

/**
 * Renders the OAuth callback success HTML page.
 *
 * Maps 1:1 to the inline HTML in OAuthController.cs lines 80-106.
 * The page is displayed after a successful OAuth code exchange and
 * uses postMessage to notify the opener window (the Rensaio frontend).
 */
export function renderCallbackHtml(providerName: string, provider: string, state: string): string {
  const safeProviderName = escapeHtml(providerName);
  const safeProvider = escapeJsString(provider);
  const safeState = escapeJsString(state);

  return `<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Rensaiō &mdash; Complete</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;background:hsl(20,14.3%,4.1%);color:hsl(0,0%,95%)}
@media(prefers-color-scheme:light){body{background:hsl(0,0%,100%);color:hsl(240,10%,3.9%)}.card{background:hsl(180,8.2%,90.2%)}}
.card{background:hsl(24,9.8%,10%);border-radius:12px;padding:2.5rem 3rem;text-align:center;max-width:380px;box-shadow:0 4px 24px rgba(0,0,0,0.3)}
.logo{font-size:1.5rem;font-weight:700;letter-spacing:-0.02em;color:hsl(346.8,77.2%,49.8%);margin-bottom:1.25rem}
.logo span{color:hsl(0,0%,95%)}
@media(prefers-color-scheme:light){.logo span{color:hsl(240,10%,3.9%)}}
.mark{width:48px;height:48px;border-radius:50%;border:3px solid hsl(346.8,77.2%,49.8%);display:inline-flex;align-items:center;justify-content:center;margin-bottom:1rem}
.mark::after{content:'';display:block;width:14px;height:24px;border:solid hsl(346.8,77.2%,49.8%);border-width:0 3px 3px 0;transform:rotate(45deg) translateY(-2px)}
h1{font-size:1.125rem;font-weight:600;margin-bottom:0.5rem}
p{font-size:0.875rem;opacity:0.7;margin-bottom:1.5rem}
.pill{display:inline-block;background:hsl(346.8,77.2%,49.8%);color:hsl(355.7,100%,97.3%);font-size:0.75rem;font-weight:600;padding:0.25rem 0.75rem;border-radius:999px;text-transform:uppercase;letter-spacing:0.04em}
.hint{font-size:0.75rem;opacity:0.4;margin-top:1.5rem}
</style></head><body>
<div class="card">
<div class="logo">Rensaiō</span></div>
<div class="mark"></div>
<h1>Authentication Complete</h1>
<p>Your ${safeProviderName} account has been connected to Rensaiō.</p>
<div class="pill">Connected</div>
<p class="hint">You may close this window.</p></div>
<script>(function(){try{if(window.opener){window.opener.postMessage({type:'oauth-success',provider:'${safeProvider}',state:'${safeState}'},'*')}}catch(e){}})()</script>
</body></html>`;
}

/**
 * Renders the OAuth callback error HTML page.
 *
 * Shown when the token exchange with the provider fails (e.g. provider API down,
 * invalid credentials, network error). Displays the underlying error so the user
 * understands why authentication failed.
 */
export function renderErrorHtml(providerName: string, errorMessage: string): string {
  const safeProvider = escapeHtml(providerName);
  const safeError = escapeHtml(errorMessage);

  return `<!DOCTYPE html>
<html lang="en">
<head><meta charset="utf-8"><title>Rensaiō &mdash; Authentication Failed</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;display:flex;align-items:center;justify-content:center;min-height:100vh;background:hsl(20,14.3%,4.1%);color:hsl(0,0%,95%)}
@media(prefers-color-scheme:light){body{background:hsl(0,0%,100%);color:hsl(240,10%,3.9%)}.card{background:hsl(180,8.2%,90.2%)}}
.card{background:hsl(24,9.8%,10%);border-radius:12px;padding:2.5rem 3rem;text-align:center;max-width:420px;box-shadow:0 4px 24px rgba(0,0,0,0.3)}
.logo{font-size:1.5rem;font-weight:700;letter-spacing:-0.02em;color:hsl(346.8,77.2%,49.8%);margin-bottom:1.25rem}
.logo span{color:hsl(0,0%,95%)}
@media(prefers-color-scheme:light){.logo span{color:hsl(240,10%,3.9%)}}
.cross{width:48px;height:48px;border-radius:50%;border:3px solid hsl(0,84.2%,60.2%);display:inline-flex;align-items:center;justify-content:center;margin-bottom:1rem;color:hsl(0,84.2%,60.2%);font-size:1.75rem;font-weight:700;line-height:1}
h1{font-size:1.125rem;font-weight:600;margin-bottom:0.5rem}
p{font-size:0.875rem;opacity:0.7;margin-bottom:0.75rem}
.error-detail{font-size:0.8rem;background:hsl(0,0%,15%);padding:0.75rem 1rem;border-radius:8px;text-align:left;word-break:break-word;font-family:monospace;color:hsl(0,84.2%,70.2%);margin-bottom:1.5rem}
@media(prefers-color-scheme:light){.error-detail{background:hsl(0,0%,93%);color:hsl(0,70%,40%)}}
.pill{display:inline-block;background:hsl(0,84.2%,60.2%);color:hsl(355.7,100%,97.3%);font-size:0.75rem;font-weight:600;padding:0.25rem 0.75rem;border-radius:999px;text-transform:uppercase;letter-spacing:0.04em}
.hint{font-size:0.75rem;opacity:0.4;margin-top:1.5rem}
</style></head><body>
<div class="card">
<div class="logo">Rensaiō</span></div>
<div class="cross">&#10005;</div>
<h1>Authentication Failed</h1>
<p>Could not connect your ${safeProvider} account to Rensaiō</p>
<div class="error-detail">${safeError}</div>
<div class="pill">Failed</div>
<p class="hint">Please try again. If the problem persists, contact support.</p></div>
<script>(function(){try{if(window.opener){window.opener.postMessage({type:'oauth-error',provider:'${escapeJsString(providerName)}'},'*')}}catch(e){}})()</script>
</body></html>`;
}

function escapeHtml(str: string): string {
  return str
    .replace(/[&]/g, '&')
    .replace(/[<]/g, '<')
    .replace(/[>]/g, '>')
    .replace(/["]/g, '"')
    .replace(/[']/g, '&#x27;');
}

function escapeJsString(str: string): string {
  return str
    .replace(/[\\]/g, '\\\\')
    .replace(/[']/g, "\\'")
    .replace(/["]/g, '\\"')
    .replace(/[\n]/g, '\\n')
    .replace(/[\r]/g, '\\r');
}