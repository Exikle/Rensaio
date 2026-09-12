"use client";

import React, { useState, useCallback } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { useScrobblerConfigs, useScrobblerAuthorize, useScrobblerDisconnect, useSaveComicVineApiKey, useKitsuDirectAuth, useMangaDexDirectAuth } from '@/lib/api/hooks/useScrobbler';
import { ScrobblerProvider, ProviderFeatures, type ScrobblerConfig } from '@/lib/api/types';
import { Link, Link2Off, Key, Radio, ExternalLink } from 'lucide-react';
import { apiClient } from '@/lib/api/client';

interface UserTrackerRequesterProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

/**
 * A provider shown in the Trackers dialog must require authentication.
 * Public metadata-only providers (MangaBaka/Bangumi/MangaUpdates) have no
 * login/connect step, so they are filtered out here. ComicVine is metadata-only
 * but requires an API key, so it still requires authentication.
 */
function requiresAuthentication(config: ScrobblerConfig): boolean {
  const features = config.features ?? 0;
  const hasMetadata = (features & ProviderFeatures.Metadata) === ProviderFeatures.Metadata;
  const hasScrobbling = (features & ProviderFeatures.Scrobbling) === ProviderFeatures.Scrobbling;
  const isPublicMetadataId =
    config.provider === ScrobblerProvider.MangaBaka ||
    config.provider === ScrobblerProvider.Bangumi ||
    config.provider === ScrobblerProvider.MangaUpdates;
  const isComicVine = config.provider === ScrobblerProvider.ComicVine;
  const isPublic = isPublicMetadataId || (hasMetadata && !hasScrobbling && !config.supportsDirectAuth && !isComicVine);
  return !isPublic;
}

export function UserTrackerRequester({ open, onOpenChange }: UserTrackerRequesterProps) {
  const queryClient = useQueryClient();
  const { data: configs, isLoading: configsLoading, error: configsError } = useScrobblerConfigs(open);
  const authorize = useScrobblerAuthorize();
  const disconnect = useScrobblerDisconnect();

  const kitsuAuth = useKitsuDirectAuth();
  const mangaDexAuth = useMangaDexDirectAuth();
  const saveComicVineKey = useSaveComicVineApiKey();

  const [comicVineApiKey, setComicVineApiKey] = useState('');
  const [kitsuEmail, setKitsuEmail] = useState('');
  const [kitsuPassword, setKitsuPassword] = useState('');
  const [mdUsername, setMdUsername] = useState('');
  const [mdPassword, setMdPassword] = useState('');
  const [mdClientId, setMdClientId] = useState('');
  const [mdClientSecret, setMdClientSecret] = useState('');
  const [connecting, setConnecting] = useState<ScrobblerProvider | null>(null);

  const handleConnectOAuth = useCallback(async (config: ScrobblerConfig) => {
    const providerName = ScrobblerProvider[config.provider];
    setConnecting(config.provider);
    try {
      const result = await authorize.mutateAsync(providerName);

      // Backend backstop: provider reports noAuthRequired — nothing to connect.
      if (result.noAuthRequired || !result.authUrl) {
        await queryClient.invalidateQueries({ queryKey: ['scrobbler', 'configs'] });
        return;
      }

      // Open OAuth popup — opens on the proxy domain (HTTPS)
      const popup = window.open(result.authUrl, 'oauth-popup', 'width=600,height=700');

      // Poll the backend callback until the proxy has stored the tokens.
      const callbackUrl = `/api/externalprovider/callback/${providerName}?state=${result.state}`;

      let connected = false;
      let attempts = 0;
      while (!connected && attempts < 60) {
        await new Promise(resolve => setTimeout(resolve, 2000));
        attempts++;
        try {
          await apiClient.get<{ connected: boolean }>(callbackUrl);
          connected = true;
        } catch {
          // Tokens not yet stored in the proxy — retry
        }
      }

      popup?.close();

      if (connected) {
        // Invalidate configs so the UI shows the provider as connected
        await queryClient.invalidateQueries({ queryKey: ['scrobbler', 'configs'] });
      }
    } finally {
      setConnecting(null);
    }
  }, [authorize, queryClient]);

  const handleDisconnect = useCallback((config: ScrobblerConfig) => {
    const providerName = ScrobblerProvider[config.provider];
    disconnect.mutate(providerName);
  }, [disconnect]);

  const handleKitsuConnect = useCallback(async () => {
    if (!kitsuEmail.trim() || !kitsuPassword.trim()) return;
    setConnecting(ScrobblerProvider.Kitsu);
    try {
      await kitsuAuth.mutateAsync({ email: kitsuEmail, password: kitsuPassword });
      setKitsuEmail('');
      setKitsuPassword('');
    } finally {
      setConnecting(null);
    }
  }, [kitsuEmail, kitsuPassword, kitsuAuth]);

  const handleMangaDexConnect = useCallback(async () => {
    if (!mdUsername.trim() || !mdPassword.trim() || !mdClientId.trim() || !mdClientSecret.trim()) return;
    setConnecting(ScrobblerProvider.MangaDex);
    try {
      await mangaDexAuth.mutateAsync({
        username: mdUsername,
        password: mdPassword,
        clientId: mdClientId,
        clientSecret: mdClientSecret,
      });
      setMdUsername('');
      setMdPassword('');
      setMdClientId('');
      setMdClientSecret('');
    } finally {
      setConnecting(null);
    }
  }, [mdUsername, mdPassword, mdClientId, mdClientSecret, mangaDexAuth]);

  const handleSaveComicVine = useCallback(async () => {
    if (!comicVineApiKey.trim()) return;
    setConnecting(ScrobblerProvider.ComicVine);
    try {
      await saveComicVineKey.mutateAsync(comicVineApiKey);
      setComicVineApiKey('');
    } finally {
      setConnecting(null);
    }
  }, [comicVineApiKey, saveComicVineKey]);

  // Only show providers that require authentication (no public metadata-only providers).
  const authConfigs = (configs ?? []).filter(requiresAuthentication);

  return (
    <>
      <Dialog open={open} onOpenChange={onOpenChange}>
        <DialogContent className="max-w-2xl max-h-[85vh] overflow-y-auto">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <Radio className="h-5 w-5" />
              External Services
            </DialogTitle>
            <DialogDescription>
              Log in and manage your authentication with external services
            </DialogDescription>
          </DialogHeader>

          {configsLoading ? (
            <div className="p-4 text-muted-foreground text-center">Loading trackers...</div>
          ) : configsError ? (
            <div className="p-4 text-destructive text-center text-sm">
              Failed to load trackers: {configsError.message}
            </div>
          ) : authConfigs.length === 0 ? (
            <div className="p-4 text-muted-foreground text-center">
              No tracking providers that require authentication are available.
            </div>
          ) : (
            <div className="space-y-3">
              {authConfigs.map((config) => {
                const isConnecting = connecting === config.provider;

                return (
                  <div
                    key={config.provider}
                    className="rounded-lg border p-4 space-y-3"
                  >
                    {/* Header row */}
                    <div className="flex items-center justify-between">
                      <div className="flex items-center gap-3 min-w-0">
                        {config.icon ? (
                          <img
                            src={config.icon}
                            alt={config.displayName}
                            className="h-8 w-8 rounded-lg flex-shrink-0"
                          />
                        ) : (
                          <div className="h-8 w-8 rounded-lg bg-primary/10 flex items-center justify-center text-sm font-bold text-primary flex-shrink-0">
                            {config.displayName.charAt(0)}
                          </div>
                        )}
                        <div className="min-w-0">
                          <div className="flex items-center gap-2">
                            <span className="text-sm font-medium">{config.displayName}</span>
                            {config.isConnected ? (
                              <Badge variant="default" className="text-xs">Connected</Badge>
                            ) : (
                              <Badge variant="secondary" className="text-xs">Disconnected</Badge>
                            )}
                          </div>
                        </div>
                      </div>

                      {/* Provider link (aligned to right, after name+status) */}
                      <div className="flex items-center gap-2 flex-shrink-0">
                        {config.link && (
                          <Button
                            variant="link"
                            size="sm"
                            className="text-xs gap-1"
                            asChild
                          >
                            <a
                              href={config.link}
                              target="_blank"
                              rel="noopener noreferrer"
                            >
                              <ExternalLink className="h-3 w-3" />
                              {config.linkDescription}
                            </a>
                          </Button>
                        )}
                        {config.isConnected && (
                          <Button
                            variant="destructive"
                            size="sm"
                            onClick={() => handleDisconnect(config)}
                            disabled={disconnect.isPending}
                          >
                            <Link2Off className="h-4 w-4" />
                          </Button>
                        )}
                      </div>
                    </div>

                    {/* Connect form (only when not connected) */}
                    {!config.isConnected && (
                      <div className="flex items-start justify-end">
                        {config.supportsDirectAuth ? (
                          config.provider === ScrobblerProvider.Kitsu ? (
                            <div className="flex items-center gap-2 w-full">
                              <div className="flex flex-1 flex-wrap items-center gap-2">
                                <Input
                                  type="email"
                                  placeholder="Email"
                                  value={kitsuEmail}
                                  onChange={(e) => setKitsuEmail(e.target.value)}
                                  className="h-8 flex-1 min-w-[120px] text-xs"
                                />
                                <Input
                                  type="password"
                                  placeholder="Password"
                                  value={kitsuPassword}
                                  onChange={(e) => setKitsuPassword(e.target.value)}
                                  className="h-8 flex-1 min-w-[120px] text-xs"
                                />
                              </div>
                              <Button
                                variant="default"
                                size="sm"
                                onClick={handleKitsuConnect}
                                disabled={isConnecting || !kitsuEmail.trim() || !kitsuPassword.trim()}
                              >
                                <Link className="h-4 w-4 mr-1" />
                                {isConnecting ? 'Connecting...' : 'Connect'}
                              </Button>
                            </div>
                          ) : config.provider === ScrobblerProvider.MangaDex ? (
                            <div className="flex items-center gap-2 w-full">
                              <div className="flex flex-1 flex-wrap items-center gap-2">
                                <Input
                                  type="text"
                                  placeholder="Username"
                                  value={mdUsername}
                                  onChange={(e) => setMdUsername(e.target.value)}
                                  className="h-8 flex-1 min-w-[100px] text-xs"
                                />
                                <Input
                                  type="password"
                                  placeholder="Password"
                                  value={mdPassword}
                                  onChange={(e) => setMdPassword(e.target.value)}
                                  className="h-8 flex-1 min-w-[100px] text-xs"
                                />
                                <Input
                                  type="text"
                                  placeholder="Client ID"
                                  value={mdClientId}
                                  onChange={(e) => setMdClientId(e.target.value)}
                                  className="h-8 flex-1 min-w-[100px] text-xs"
                                />
                                <Input
                                  type="password"
                                  placeholder="Client Secret"
                                  value={mdClientSecret}
                                  onChange={(e) => setMdClientSecret(e.target.value)}
                                  className="h-8 flex-1 min-w-[100px] text-xs"
                                />
                              </div>
                              <Button
                                variant="default"
                                size="sm"
                                onClick={handleMangaDexConnect}
                                disabled={isConnecting || !mdUsername.trim() || !mdPassword.trim() || !mdClientId.trim() || !mdClientSecret.trim()}
                              >
                                <Link className="h-4 w-4 mr-1" />
                                {isConnecting ? 'Connecting...' : 'Connect'}
                              </Button>
                            </div>
                          ) : null
                        ) : config.provider === ScrobblerProvider.ComicVine ? (
                          <div className="flex items-center gap-2 w-full">
                            <Input
                              type="password"
                              placeholder="Enter ComicVine API key"
                              value={comicVineApiKey}
                              onChange={(e) => setComicVineApiKey(e.target.value)}
                              className="h-8 flex-1 text-xs"
                            />
                            <Button
                              variant="default"
                              size="sm"
                              onClick={handleSaveComicVine}
                              disabled={isConnecting || !comicVineApiKey.trim()}
                            >
                              <Key className="h-4 w-4 mr-1" />
                              {isConnecting ? 'Saving...' : 'Save Key'}
                            </Button>
                          </div>
                        ) : (
                          <Button
                            variant="default"
                            size="sm"
                            onClick={() => handleConnectOAuth(config)}
                            disabled={isConnecting || authorize.isPending}
                          >
                            <Link className="h-4 w-4 mr-1" />
                            {isConnecting ? 'Connecting...' : 'Connect'}
                          </Button>
                        )}
                      </div>
                    )}
                  </div>
                );
              })}
            </div>
          )}
        </DialogContent>
      </Dialog>
    </>
  );
}