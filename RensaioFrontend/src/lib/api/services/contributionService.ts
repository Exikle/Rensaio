import { apiClient } from '@/lib/api/client';

export interface ContributionUploadResult {
  queued: boolean;
}

/**
 * Contribution API — triggers a ContributionSnapshotV1 export of the local
 * contribution database to the cloud contribution worker.
 *
 * The backend enqueues the upload and returns immediately (202 Accepted); the
 * export runs in a background process thereafter.
 */
export const contributionService = {
  /**
   * Enqueue an export of pending contribution changes (Version 0 / -1) to the
   * cloud worker. Returns once the request is queued — the actual upload runs
   * in the background, after which those rows become Version -2 (Uploaded).
   */
  async uploadContributions(): Promise<ContributionUploadResult> {
    return apiClient.post<ContributionUploadResult>('/api/contributions/upload');
  },
};