import { apiClient } from '@/lib/api/client';
import {
  type ExternalMappingsPage,
  type ExternalSeriesGroup,
  type ExternalTitleMapping,
  type ScrobblerProvider,
  type SeriesMappingStatus,
} from '@/lib/api/types';

const BASE = '/api/external-mappings';

export const externalMappingsService = {
  // Optionally apply a consistent envelope: our backend returns the page DTO directly.
  async list(params: {
    filter?: 'all' | 'unmatched' | 'blocked';
    page?: number;
    pageSize?: number;
    status?: SeriesMappingStatus | null;
  }): Promise<ExternalMappingsPage> {
    const query = new URLSearchParams();
    query.set('filter', params.filter ?? 'unmatched');
    query.set('page', String(params.page ?? 0));
    query.set('pageSize', String(params.pageSize ?? 50));
    if (params.status != null) query.set('status', params.status.toString());
    return apiClient.get<ExternalMappingsPage>(`${BASE}?${query.toString()}`);
  },

  async getSeries(seriesId: string): Promise<ExternalSeriesGroup> {
    return apiClient.get<ExternalSeriesGroup>(`${BASE}/series/${seriesId}`);
  },

  async scanSeries(seriesId: string): Promise<void> {
    return apiClient.post<void>(`${BASE}/series/${seriesId}/scan`);
  },

  async linkSeries(seriesId: string, provider: ScrobblerProvider): Promise<{ message: string }> {
    const params = new URLSearchParams({ provider: provider.toString() });
    return apiClient.post<{ message: string }>(`${BASE}/series/${seriesId}/link?${params.toString()}`);
  },

  async blockSeries(seriesId: string, provider: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/series/${seriesId}/${provider}/block`);
  },

  async unblockSeries(seriesId: string, provider: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/series/${seriesId}/${provider}/unblock`);
  },

  async ignoreSeries(seriesId: string, provider: string, forever: boolean): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/series/${seriesId}/${provider}/ignore`, { forever });
  },

  async ignoreAllUnmatched(seriesId: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/series/${seriesId}/ignore-all`);
  },

  async scanAll(): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/scan`);
  },
};