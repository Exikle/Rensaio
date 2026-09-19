import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { contributionMappingsService } from '@/lib/api/services/contributionMappingsService';
import {
  type ContributionMappingLinkRequest,
  type ScrobblerProvider,
  type SeriesMappingStatus,
} from '@/lib/api/types';

interface ListParams {
  filter?: 'all' | 'unmatched' | 'blocked';
  page?: number;
  pageSize?: number;
  status?: SeriesMappingStatus | null;
}

export const contributionMappingsQueryKey = ['contribution-mappings'] as const;

export const useContributionMappings = (params: ListParams = {}, enabled = true) => {
  return useQuery({
    queryKey: [
      ...contributionMappingsQueryKey,
      params.filter ?? 'unmatched',
      params.page ?? 0,
      params.pageSize ?? 50,
      params.status,
    ],
    queryFn: () => contributionMappingsService.list(params),
    enabled,
  });
};

export const useContributionMapping = (mappingId: string | null) => {
  return useQuery({
    queryKey: [...contributionMappingsQueryKey, 'mapping', mappingId],
    queryFn: () => contributionMappingsService.getMapping(mappingId!),
    enabled: !!mappingId,
  });
};

export const useContributionMappingsScanAll = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => contributionMappingsService.scanAll(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};

export const useContributionMappingsScanMapping = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (mappingId: string) => contributionMappingsService.scanMapping(mappingId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};

export const useContributionMappingsLink = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (request: ContributionMappingLinkRequest) => contributionMappingsService.linkMapping(request),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};

export const useContributionMappingsBlock = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mappingId, provider }: { mappingId: string; provider: string }) =>
      contributionMappingsService.blockMapping(mappingId, provider),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};

export const useContributionMappingsUnblock = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mappingId, provider }: { mappingId: string; provider: string }) =>
      contributionMappingsService.unblockMapping(mappingId, provider),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};

export const useContributionMappingsIgnore = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mappingId, provider, forever }: { mappingId: string; provider: string; forever: boolean }) =>
      contributionMappingsService.ignoreMapping(mappingId, provider, forever),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};

export const useContributionMappingsIgnoreAll = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (mappingId: string) => contributionMappingsService.ignoreAllUnmatched(mappingId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};

export const useContributionMappingsUnlink = () => {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ mappingId, provider }: { mappingId: string; provider: string }) =>
      contributionMappingsService.unlinkMapping(mappingId, provider),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
    },
  });
};
