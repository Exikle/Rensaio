'use client';

import { useState, useEffect, useRef, Suspense } from 'react';
import { useSearchParams } from 'next/navigation';
import { useAuth } from '@/contexts/auth-context';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';

const REMEMBERED_USER_KEY = 'rensaio_remembered_username';

// Error codes the OIDC callback can redirect back with (?error=oidc_*)
const OIDC_ERRORS: Record<string, string> = {
  oidc_denied: 'Sign-in was cancelled at the identity provider.',
  oidc_no_account: 'No Rensaiō account matches your identity. Ask an administrator to create one.',
  oidc_inactive: 'This account is disabled.',
  oidc_state: 'The sign-in request expired. Please try again.',
  oidc_token: 'The identity provider response could not be verified. Please try again.',
  oidc_claims: 'The identity provider did not return a username. Check the OIDC claim settings.',
  oidc_provider: 'The identity provider could not be reached. Check the OIDC settings.',
};

function buildSsoUrl(rememberMe: boolean): string {
  const params = new URLSearchParams({ rememberMe: String(rememberMe), returnTo: '/library' });
  return `/api/auth/oidc/login?${params.toString()}`;
}

function LoginForm() {
  const searchParams = useSearchParams();
  const { login, completeSsoLogin, isAuthEnabled, oidc } = useAuth();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [rememberMe, setRememberMe] = useState(false);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const ssoHandled = useRef(false);

  const ssoCode = searchParams.get('sso');
  const ssoError = searchParams.get('error');
  const ssoEnabled = !!oidc?.enabled;
  const showPasswordForm = !oidc?.hidePasswordLogin;

  // On mount, pre-fill username from localStorage if previously remembered
  useEffect(() => {
    const rememberedUsername = localStorage.getItem(REMEMBERED_USER_KEY);
    if (rememberedUsername) {
      setUsername(rememberedUsername);
      setRememberMe(true);
    }
  }, []);

  // Surface an error the OIDC callback redirected back with
  useEffect(() => {
    if (ssoError) {
      setError(OIDC_ERRORS[ssoError] ?? 'Single sign-on failed.');
    }
  }, [ssoError]);

  // Finish an OIDC login: exchange the one-time code for a session
  useEffect(() => {
    if (!ssoCode || ssoHandled.current) return;
    ssoHandled.current = true;
    setLoading(true);
    const returnTo = searchParams.get('returnTo') || '/library';
    completeSsoLogin(ssoCode, returnTo).catch((err) => {
      setError(err instanceof Error ? err.message : 'Single sign-on failed.');
      setLoading(false);
      // Drop the spent code from the URL so a refresh shows the form, not the same error
      window.history.replaceState(null, '', '/login');
    });
  }, [ssoCode, searchParams, completeSsoLogin]);

  // Auto-redirect: go straight to the provider unless we are already mid-flow or showing an error
  useEffect(() => {
    if (oidc?.enabled && oidc.autoRedirect && !ssoCode && !ssoError) {
      window.location.href = buildSsoUrl(true);
    }
  }, [oidc, ssoCode, ssoError]);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      await login(username, password, rememberMe);
      // Persist or clear the remembered username based on checkbox state
      if (rememberMe) {
        localStorage.setItem(REMEMBERED_USER_KEY, username);
      } else {
        localStorage.removeItem(REMEMBERED_USER_KEY);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Login failed');
    } finally {
      setLoading(false);
    }
  };

  // If auth is disabled, redirect is handled by layout
  if (!isAuthEnabled) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <Card className="w-full max-w-md">
          <CardHeader>
            <CardTitle>Authentication Disabled</CardTitle>
            <CardDescription>
              Authentication is not enabled. Please go back to the user selection page.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button onClick={() => window.location.href = '/user-select'} className="w-full">
              Go to User Selection
            </Button>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="flex items-center justify-center min-h-screen bg-background">
      <Card className="w-full max-w-md mx-4">
        <CardHeader className="space-y-3">
          <CardTitle className="flex justify-center">
            <img src="/rensaiow.png" alt="Rensaiō" className="h-20 w-auto" />
          </CardTitle>
          <CardDescription className="text-center">
            {ssoCode ? 'Signing you in...' : showPasswordForm ? 'Enter your credentials to log in' : 'Sign in to continue'}
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          {error && (
            <div className="p-3 text-sm text-red-500 bg-red-50 dark:bg-red-950 rounded-md">
              {error}
            </div>
          )}

          {ssoEnabled && (
            <Button
              type="button"
              variant={showPasswordForm ? 'outline' : 'default'}
              className="w-full"
              disabled={loading}
              onClick={() => { window.location.href = buildSsoUrl(rememberMe || !showPasswordForm); }}
            >
              {oidc?.buttonLabel || 'Single Sign-On'}
            </Button>
          )}

          {ssoEnabled && showPasswordForm && (
            <div className="relative">
              <div className="absolute inset-0 flex items-center">
                <span className="w-full border-t" />
              </div>
              <div className="relative flex justify-center text-xs uppercase">
                <span className="bg-card px-2 text-muted-foreground">or</span>
              </div>
            </div>
          )}

          {showPasswordForm && (
            <form onSubmit={handleSubmit} className="space-y-4">
              <div className="space-y-2">
                <Label htmlFor="username">Username</Label>
                <Input
                  id="username"
                  type="text"
                  placeholder="Enter your username"
                  value={username}
                  onChange={(e) => setUsername(e.target.value)}
                  required
                  autoFocus
                />
              </div>
              <div className="space-y-2">
                <Label htmlFor="password">Password</Label>
                <Input
                  id="password"
                  type="password"
                  placeholder="Enter your password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                />
              </div>
              <div className="flex items-center space-x-2">
                <Checkbox
                  id="rememberMe"
                  checked={rememberMe}
                  onCheckedChange={(checked) => setRememberMe(checked === true)}
                />
                <Label htmlFor="rememberMe" className="text-sm cursor-pointer">
                  Remember me
                </Label>
              </div>
              <Button type="submit" className="w-full" disabled={loading}>
                {loading ? 'Logging in...' : 'Log in'}
              </Button>
            </form>
          )}
        </CardContent>
      </Card>
    </div>
  );
}

export default function LoginPage() {
  // useSearchParams needs a Suspense boundary under static export
  return (
    <Suspense fallback={null}>
      <LoginForm />
    </Suspense>
  );
}
