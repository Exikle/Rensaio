import { apiClient } from '@/lib/api/client';
import {
  type ScrobblerConfig,
  type ScrobblerConfigUpdate,
  type OAuthAuthorizeResponse,
  type OAuthCallbackRequest,
  type SeriesMatchStatus,
  type SeriesMatchSearchRequest,
  type AutoMatchResult,
  type ConfirmMatchRequest,
  type DisableLinkRequest,
  type SyncStatus,
  type ScrobblerProvider,
  type KitsuDirectAuthRequest,
  type MangaDexDirectAuthRequest,
  type MetadataLinkRequest,
  type MetadataLinkResult,
  type MetadataLinkAllResult,
  type MetadataSeriesView,
  type MetadataRefreshRequest,
} from '@/lib/api/types';

export const scrobblerService = {
  // ── Providers ──

  async getProviders(): Promise<ScrobblerConfig[]> {
    return apiClient.get<ScrobblerConfig[]>('/api/externalprovider/providers');
  },

  // ── Config ──

  async getConfigs(): Promise<ScrobblerConfig[]> {
    return apiClient.get<ScrobblerConfig[]>('/api/externalprovider/config');
  },

  async updateConfig(provider: ScrobblerProvider, update: ScrobblerConfigUpdate): Promise<void> {
    const params = new URLSearchParams({ provider: provider.toString() });
    return apiClient.put<void>(`/api/externalprovider/config?${params.toString()}`, update);
  },

  // ── OAuth ──

  async authorize(provider: string): Promise<OAuthAuthorizeResponse> {
    return apiClient.post<OAuthAuthorizeResponse>(`/api/externalprovider/config/${provider}/authorize`);
  },

  async callback(provider: string, request: OAuthCallbackRequest): Promise<{ connected: boolean }> {
    return apiClient.post<{ connected: boolean }>(`/api/externalprovider/config/${provider}/callback`, request);
  },

  async disconnect(provider: string): Promise<void> {
    return apiClient.delete<void>(`/api/externalprovider/config/${provider}`);
  },

  // ── Direct Auth (Kitsu, MangaDex) ──

  async kitsuDirectAuth(request: KitsuDirectAuthRequest): Promise<{ connected: boolean }> {
    return apiClient.post<{ connected: boolean }>('/api/externalprovider/config/kitsu/direct', request);
  },

  async mangaDexDirectAuth(request: MangaDexDirectAuthRequest): Promise<{ connected: boolean }> {
    return apiClient.post<{ connected: boolean }>('/api/externalprovider/config/mangadex/direct', request);
  },

  // ── Matching ──

  async getMatches(): Promise<SeriesMatchStatus[]> {
    return apiClient.get<SeriesMatchStatus[]>('/api/externalprovider/matches');
  },

  async getUnmatched(): Promise<SeriesMatchStatus[]> {
    return apiClient.get<SeriesMatchStatus[]>('/api/externalprovider/matches/unmatched');
  },

  async searchExternal(request: SeriesMatchSearchRequest): Promise<{ provider: ScrobblerProvider; results: import('@/lib/api/types').ScrobblerSearchResult[] }> {
    return apiClient.post('/api/externalprovider/matches/search', request);
  },

  async autoMatchAll(provider: ScrobblerProvider): Promise<AutoMatchResult> {
    const params = new URLSearchParams({ provider: provider.toString() });
    return apiClient.post<AutoMatchResult>(`/api/externalprovider/matches/auto?${params.toString()}`);
  },

  async autoMatchSeries(seriesId: string): Promise<void> {
    return apiClient.post<void>(`/api/externalprovider/matches/auto/${seriesId}`);
  },

  async confirmMatch(request: ConfirmMatchRequest): Promise<void> {
    return apiClient.post<void>('/api/externalprovider/matches/confirm', request);
  },

  async disableLink(request: DisableLinkRequest): Promise<void> {
    return apiClient.post<void>('/api/externalprovider/matches/disable', request);
  },

  async removeMapping(seriesId: string, provider: ScrobblerProvider): Promise<void> {
    return apiClient.delete<void>(`/api/externalprovider/matches/${seriesId}/${provider}`);
  },

  // ── API Key (ComicVine) ──

  async saveComicVineApiKey(apiKey: string): Promise<void> {
    return apiClient.post<void>('/api/externalprovider/config/comicvine/apikey', { apiKey });
  },

  // ── Sync ──

  async triggerSync(): Promise<void> {
    return apiClient.post<void>('/api/externalprovider/sync');
  },

  async getSyncStatus(): Promise<SyncStatus[]> {
    return apiClient.get<SyncStatus[]>('/api/externalprovider/sync/status');
  },

  // ── Metadata Link Engine ──

  async linkSeries(request: MetadataLinkRequest): Promise<MetadataLinkResult> {
    return apiClient.post<MetadataLinkResult>('/api/externalprovider/link', request);
  },

  async linkAll(onProgress?: (processed: number, linked: number, suggested: number) => void): Promise<MetadataLinkAllResult> {
    try {
      // link-all is a long-running server job; progress comes from the final summary.
      const result = await apiClient.post<MetadataLinkAllResult>('/api/externalprovider/link-all');
      onProgress?.(result.processedSeries ?? 0, result.linkedProviderEntries ?? 0, result.suggestedEntries ?? 0);
      return result;
    } catch (err) {
      throw err;
    }
  },

  async getSeriesLinkState(seriesId: string): Promise<MetadataSeriesView> {
    return apiClient.get<MetadataSeriesView>(`/api/externalprovider/series/${seriesId}`);
  },

  async refreshSeries(request: MetadataRefreshRequest): Promise<MetadataLinkResult> {
    return apiClient.post<MetadataLinkResult>('/api/externalprovider/refresh', request);
  },
};