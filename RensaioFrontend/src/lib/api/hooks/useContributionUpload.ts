import { useMutation } from '@tanstack/react-query';
import { contributionService } from '@/lib/api/services/contributionService';

/**
 * Triggers a ContributionSnapshotV1 upload of the local contribution database
 * to the cloud contribution worker (POST /api/contributions/upload).
 */
export const useContributionUpload = () => {
  return useMutation({
    mutationFn: () => contributionService.uploadContributions(),
  });
};