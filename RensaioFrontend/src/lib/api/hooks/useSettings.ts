import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { settingsService } from '@/lib/api/services/settingsService';
import { type Settings } from '@/lib/api/types';

export const useSettings = () => {
  return useQuery({
    queryKey: ['settings'],
    queryFn: () => settingsService.getSettings(),
  });
};

export const useAvailableLanguages = () => {
  return useQuery({
    queryKey: ['settings', 'languages'],
    queryFn: () => settingsService.getAvailableLanguages(),
  });
};

export const useUpdateSettings = () => {
  const queryClient = useQueryClient();
  
  return useMutation({
    mutationFn: (settings: Settings) => settingsService.updateSettings(settings),
    onSuccess: (data) => {
      // If the backend returned a set-password URL, the settings-manager
      // will handle the redirect. Otherwise, invalidate settings.
      if (!data?.setPasswordUrl) {
        queryClient.invalidateQueries({ queryKey: ['settings'] });
      }
    },
  });
};

/**
 * Verifies a Contributor Id against RensaioContributionDB.CF via the backend.
 * The backend persists `contributionVerified=true` (plus the id/server URL) in
 * settings. We deliberately do NOT invalidate the settings query here: the
 * SettingsManager form holds the authoritative local state (id + verified flag),
 * and a background refetch would clobber the textbox mid-edit.
 */
export const useVerifyContributor = () => {
  return useMutation({
    mutationFn: (args: { serverUrl: string; contributorId: string }) =>
      settingsService.verifyContributor(args.serverUrl, args.contributorId),
  });
};
