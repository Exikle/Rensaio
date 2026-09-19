import { apiClient } from '@/lib/api/client';
import {
  type ContributionMappingGroup,
  type ContributionMappingsPage,
  type ContributionMappingLinkRequest,
  type ScrobblerProvider,
  type SeriesMappingStatus,
} from '@/lib/api/types';

const BASE = '/api/contribution-mappings';

export const contributionMappingsService = {
  async list(params: {
    filter?: 'all' | 'unmatched' | 'blocked';
    page?: number;
    pageSize?: number;
    status?: SeriesMappingStatus | null;
  }): Promise<ContributionMappingsPage> {
    const query = new URLSearchParams();
    query.set('filter', params.filter ?? 'unmatched');
    query.set('page', String(params.page ?? 0));
    query.set('pageSize', String(params.pageSize ?? 50));
    if (params.status != null) query.set('status', params.status.toString());
    return apiClient.get<ContributionMappingsPage>(`${BASE}?${query.toString()}`);
  },

  async getMapping(mappingId: string): Promise<ContributionMappingGroup> {
    return apiClient.get<ContributionMappingGroup>(`${BASE}/mappings/${mappingId}`);
  },

  async scanMapping(mappingId: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/scan`);
  },

  async linkMapping(request: ContributionMappingLinkRequest): Promise<{ message: string }> {
    const params = new URLSearchParams({ provider: request.provider.toString() });
    return apiClient.post<{ message: string }>(
      `${BASE}/mappings/${request.mappingId}/link?${params.toString()}`,
      {
        externalSeriesId: request.externalSeriesId,
        externalSeriesTitle: request.externalSeriesTitle,
      },
    );
  },

  async blockMapping(mappingId: string, provider: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}/block`);
  },

  async unblockMapping(mappingId: string, provider: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}/unblock`);
  },

  async ignoreMapping(mappingId: string, provider: string, forever: boolean): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}/ignore`, { forever });
  },

  async ignoreAllUnmatched(mappingId: string): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/mappings/${mappingId}/ignore-all`);
  },

  async unlinkMapping(mappingId: string, provider: string): Promise<{ message: string }> {
    return apiClient.delete<{ message: string }>(`${BASE}/mappings/${mappingId}/${provider}`);
  },

  async scanAll(): Promise<{ message: string }> {
    return apiClient.post<{ message: string }>(`${BASE}/scan`);
  },
};
