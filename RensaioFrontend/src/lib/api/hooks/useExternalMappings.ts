import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { externalMappingsService } from '@/lib/api/services/externalMappingsService';
import {
  type ExternalMappingsPage,
  type ScrobblerProvider,
  type SeriesMappingStatus,
} from '@/lib/api/types';

interface ListParams {
  filter?: 'all' | 'unmatched' | 'blocked';
  page?: number;
  pageSize?: number;
  status?: SeriesMappingStatus | null;
}

export const useExternalMappings = (params: ListParams = {}, enabled = true) => {
  return useQuery({
    queryKey: ['external-mappings', params.filter ?? 'unmatched', params.page ?? 0, params.pageSize ?? 50, params.status],
    queryFn: () => externalMappingsService.list(params),
    enabled,
  });
};

export const useExternalMappingSeries = (seriesId: string | null) => {
  return useQuery({
    queryKey: ['external-mappings', 'series', seriesId],
    queryFn: () => externalMappingsService.getSeries(seriesId!),
    enabled: !!seriesId,
  });
};

export const useExternalMappingsScanAll = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => externalMappingsService.scanAll(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
    },
  });
};

export const useExternalMappingsScanSeries = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (seriesId: string) => externalMappingsService.scanSeries(seriesId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
    },
  });
};

export const useExternalMappingsLink = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ seriesId, provider }: { seriesId: string; provider: ScrobblerProvider }) =>
      externalMappingsService.linkSeries(seriesId, provider),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
    },
  });
};

export const useExternalMappingsBlock = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ seriesId, provider }: { seriesId: string; provider: string }) =>
      externalMappingsService.blockSeries(seriesId, provider),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
    },
  });
};

export const useExternalMappingsUnblock = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ seriesId, provider }: { seriesId: string; provider: string }) =>
      externalMappingsService.unblockSeries(seriesId, provider),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
    },
  });
};

export const useExternalMappingsIgnore = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ seriesId, provider, forever }: { seriesId: string; provider: string; forever: boolean }) =>
      externalMappingsService.ignoreSeries(seriesId, provider, forever),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
    },
  });
};

export const useExternalMappingsIgnoreAll = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (seriesId: string) => externalMappingsService.ignoreAllUnmatched(seriesId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['external-mappings'] });
    },
  });
};