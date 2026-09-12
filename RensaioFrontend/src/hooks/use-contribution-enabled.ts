import { useSettings } from "@/lib/api/hooks/useSettings";

/**
 * Whether contribution features (Contribution page, cloud export, uploads)
 * are unlocked. Requires the master switch ON, a Contributor Id configured,
 * AND that Id verified against RensaioContributionDB.CF.
 */
export function useContributionEnabled() {
  const { data: settings } = useSettings();
  return (
    !!settings?.contributionEnabled &&
    !!settings?.contributionContributorId &&
    !!settings?.contributionVerified
  );
}