"use client";

import React, { useState, useEffect, useCallback } from 'react';
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { LazyImage } from "@/components/ui/lazy-image";
import { Tooltip, TooltipContent, TooltipProvider, TooltipTrigger } from "@/components/ui/tooltip";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { ExternalLink, Search, Ban, RotateCcw, EyeOff, RefreshCw, Clock, Check, AlertCircle, Layers, ShieldAlert, CloudUpload } from 'lucide-react';
import { useQueryClient } from '@tanstack/react-query';
import { getProgressHub } from '@/lib/api/signalr/progressHub';
import { JobType, ProgressStatus } from '@/lib/api/types';
import { useContributionUpload } from '@/lib/api/hooks/useContributionUpload';
import { useToast } from '@/hooks/use-toast';
import {
  useContributionMappings,
  useContributionMappingsScanAll,
  useContributionMappingsScanMapping,
  useContributionMappingsLink,
  useContributionMappingsBlock,
  useContributionMappingsUnblock,
  useContributionMappingsIgnore,
  contributionMappingsQueryKey,
} from "@/lib/api/hooks/useContributionMappings";
import { ScrobblerSearchRequester } from '@/components/comp/scrobbler/scrobbler-search-requester';
import { ScrobblerProvider, SeriesMappingStatus, type ContributionMappingGroup, type ContributionMappingsPage, type ExternalSeriesProviderMapping } from '@/lib/api/types';
import { useContributionEnabled } from '@/hooks/use-contribution-enabled';

type MappingScanState = 'scanning' | 'done' | 'failed';

function ThumbnailPopover({ src, alt, className = 'h-10 w-10 rounded object-cover flex-shrink-0', side = 'right' }: {
  src: string;
  alt?: string;
  className?: string;
  side?: 'right' | 'left' | 'top' | 'bottom';
}) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <LazyImage src={src} alt={alt ?? ''} className={className} loading="eager" />
      </TooltipTrigger>
      <TooltipPrimitive.Portal>
        <TooltipContent side={side} className="p-0 bg-transparent border-none shadow-none">
          <div className="relative w-48 aspect-[3/4]">
            <LazyImage
              src={src}
              alt={alt ?? ''}
              className="w-full h-full object-cover rounded-md border border-secondary"
            />
          </div>
        </TooltipContent>
      </TooltipPrimitive.Portal>
    </Tooltip>
  );
}

function statusBadge(status: SeriesMappingStatus) {
  switch (status) {
    case SeriesMappingStatus.Unmatched: return <Badge variant="secondary">Not matched</Badge>;
    case SeriesMappingStatus.AutoMatched: return <Badge variant="default">Auto</Badge>;
    case SeriesMappingStatus.UserConfirmed: return <Badge variant="default">User</Badge>;
    case SeriesMappingStatus.TemporaryIgnored: return <Badge variant="secondary">Temp. ignored</Badge>;
    case SeriesMappingStatus.ForeverIgnored: return <Badge variant="secondary">Disabled</Badge>;
    case SeriesMappingStatus.Blocked: return <Badge variant="destructive">Blocked</Badge>;
    default: return <Badge variant="secondary">Unknown</Badge>;
  }
}

export default function ContributionPage() {
  const contributionEnabled = useContributionEnabled();
  const { toast } = useToast();
  const contributionUpload = useContributionUpload();

  const handleExportToCloud = () => {
    contributionUpload.mutate(undefined, {
      onSuccess: () => {
        toast({
          title: 'Contribution export queued',
          description: 'Your contributions are being uploaded to the cloud in the background.',
        });
      },
      onError: (error) => {
        toast({
          title: 'Contribution export failed',
          description: error instanceof Error ? error.message : 'Unknown error',
          variant: 'destructive',
        });
      },
    });
  };

  const [filter, setFilter] = useState<'all' | 'unmatched'>('unmatched');
  const [page, setPage] = useState(0);
  const [statusFilter, setStatusFilter] = useState<number | null>(null);
  const [scanProgress, setScanProgress] = useState<string | null>(null);
  const [mappingScanStates, setMappingScanStates] = useState<Record<string, MappingScanState>>({});
  const [searchTarget, setSearchTarget] = useState<{
    mappingId: string;
    mappingTitle: string;
    mappingCoverUrl?: string;
    mappingTitles?: string;
    provider: ScrobblerProvider;
  } | null>(null);
  const pageSize = 50;
  const queryClient = useQueryClient();

  const { data, isLoading: loading } = useContributionMappings({ filter, page, pageSize, status: statusFilter });
  const scanAll = useContributionMappingsScanAll();
  const scanMapping = useContributionMappingsScanMapping();
  const linkMapping = useContributionMappingsLink();
  const block = useContributionMappingsBlock();
  const unblock = useContributionMappingsUnblock();
  const ignore = useContributionMappingsIgnore();

  // Confirm into the contribution DB via the generic search dialog's onConfirm hook.
  const handleSearchConfirmed = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
  }, [queryClient]);

  // Live progress for contribution mapping scans via SignalR.
  //  - id = "contribution-link-all"            → global scan aggregate (header badge next to Scan now).
  //  - id = "contribution-link-{mappingId}"    → single-mapping scan (per-row spinner + done/failed badge).
  useEffect(() => {
    let unsubscribe: (() => void) | null = null;
    const listen = async () => {
      try {
        await getProgressHub().startConnection();
        unsubscribe = getProgressHub().onProgress((p) => {
          if (p.jobType !== JobType.MetadataLink) return;

          if (p.id === 'contribution-link-all') {
            if (p.progressStatus === ProgressStatus.Started || p.progressStatus === ProgressStatus.InProgress) {
              setScanProgress(p.message || `Linking… ${p.percentage ?? 0}%`);
            } else if (p.progressStatus === ProgressStatus.Completed) {
              setScanProgress(p.message || 'Scan complete');
              void queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
              setTimeout(() => setScanProgress(null), 3000);
            } else if (p.progressStatus === ProgressStatus.Failed) {
              setScanProgress(p.errorMessage || 'Scan failed');
            }
            return;
          }

          const prefix = 'contribution-link-';
          if (p.id?.startsWith(prefix)) {
            const mappingId = p.id.slice(prefix.length);
            if (p.progressStatus === ProgressStatus.Started || p.progressStatus === ProgressStatus.InProgress) {
              setMappingScanStates((prev) => ({ ...prev, [mappingId]: 'scanning' }));
            } else if (p.progressStatus === ProgressStatus.Completed) {
              setMappingScanStates((prev) => ({ ...prev, [mappingId]: 'done' }));
              void queryClient.invalidateQueries({ queryKey: contributionMappingsQueryKey });
              setTimeout(() => {
                setMappingScanStates((prev) => {
                  const next = { ...prev };
                  delete next[mappingId];
                  return next;
                });
              }, 3000);
            } else if (p.progressStatus === ProgressStatus.Failed) {
              setMappingScanStates((prev) => ({ ...prev, [mappingId]: 'failed' }));
            }
          }
        });
      } catch {
        // SignalR unavailable — the per-row spinner still shows via the mutation's isPending.
      }
    };
    void listen();
    return () => { unsubscribe?.(); };
  }, [queryClient]);

  // Page-level gate: the Contribution page is only usable once the Contributor
  // Id has been verified against RensaioContributionDB.CF. Until then, show a
  // notice instead of any mapping/export UI.
  if (!contributionEnabled) {
    return (
      <div className="space-y-6">
        <div>
          <p className="flex items-center gap-2 text-sm font-medium">
            <ShieldAlert className="h-4 w-4" />
            Contribution is locked
          </p>
          <p className="mt-2 text-sm text-muted-foreground">
            This page requires a verified Contributor Id. Enable Contribution in
            Settings, enter your Contributor UUID, and verify it against the
            contribution database to unlock contribution uploads and mapping.
          </p>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-bold">Contribution</h1>
        <p className="text-sm text-muted-foreground">
          Manage sources and their metadata links in the contribution database.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-3">
        {/* Filter combo — left aligned, default Unmatched. */}
        <Select value={filter} onValueChange={(v) => { setFilter(v as 'all' | 'unmatched'); setPage(0); }}>
          <SelectTrigger className="w-40">
            <SelectValue placeholder="Unmatched" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="unmatched">Unmatched</SelectItem>
            <SelectItem value="all">All</SelectItem>
          </SelectContent>
        </Select>

        <div className="ml-auto flex items-center gap-2">
          {/* Export to cloud — only when the Contributor Id is verified. */}
          <Button
            size="sm"
            variant="outline"
            className="gap-1"
            onClick={handleExportToCloud}
            disabled={contributionUpload.isPending}
          >
            <CloudUpload className={`h-4 w-4 ${contributionUpload.isPending ? 'animate-pulse' : ''}`} />
            {contributionUpload.isPending ? 'Exporting…' : 'Export to cloud'}
          </Button>

          {/* Scan now — right aligned, icon + primary style. */}
          <Button
            size="sm"
            className="gap-1"
            onClick={() => { setScanProgress('Starting scan…'); scanAll.mutate(); }}
            disabled={scanAll.isPending}
          >
            <RefreshCw className={`h-4 w-4 ${scanAll.isPending ? 'animate-spin' : ''}`} />
            {scanAll.isPending ? 'Scanning…' : 'Scan now'}
          </Button>
          {scanProgress && (
            <Badge variant="secondary">{scanProgress}</Badge>
          )}
        </div>
      </div>

      <MappingTable
        groups={data?.groups ?? []}
        providerMeta={data?.providerMeta ?? {}}
        loading={loading}
        scanMapping={scanMapping}
        block={block}
        unblock={unblock}
        ignore={ignore}
        mappingScanStates={mappingScanStates}
        onSearch={setSearchTarget}
      />

      {searchTarget && (
        <ScrobblerSearchRequester
          open
          onOpenChange={(open) => {
            if (!open) {
              setSearchTarget(null);
              handleSearchConfirmed();
            }
          }}
          provider={searchTarget.provider}
          seriesId={searchTarget.mappingId}
          seriesTitle={searchTarget.mappingTitle}
          seriesThumbnail={searchTarget.mappingCoverUrl}
          seriesAltTitles={searchTarget.mappingTitles}
          confirmLabel="Confirm Mapping"
          onConfirm={async (externalSeriesId, externalSeriesTitle) => {
            await linkMapping.mutateAsync({
              mappingId: searchTarget.mappingId,
              provider: searchTarget.provider,
              externalSeriesId,
              externalSeriesTitle,
            });
          }}
        />
      )}

      {/* Pagination */}
      <div className="flex items-center gap-2">
        <Button variant="outline" size="sm" disabled={page <= 0} onClick={() => setPage(page - 1)}>Prev</Button>
        <span className="text-sm">Page {page + 1} · {data?.total ?? 0} total</span>
        <Button
          variant="outline"
          size="sm"
          disabled={(data?.total ?? 0) <= (page + 1) * pageSize}
          onClick={() => setPage(page + 1)}
        >
          Next
        </Button>
      </div>
    </div>
  );
}

interface MappingTableActions {
  scanMapping: ReturnType<typeof useContributionMappingsScanMapping>;
  block: ReturnType<typeof useContributionMappingsBlock>;
  unblock: ReturnType<typeof useContributionMappingsUnblock>;
  ignore: ReturnType<typeof useContributionMappingsIgnore>;
  mappingScanStates?: Record<string, MappingScanState>;
  onSearch: (target: {
    mappingId: string;
    mappingTitle: string;
    mappingCoverUrl?: string;
    mappingTitles?: string;
    provider: ScrobblerProvider;
  }) => void;
}

function MappingTable({ groups, providerMeta, loading, scanMapping, block, unblock, ignore, onSearch, mappingScanStates }: {
  groups: ContributionMappingGroup[];
  providerMeta: ContributionMappingsPage['providerMeta'];
  loading: boolean;
} & MappingTableActions) {
  if (loading) return <p className="text-sm text-muted-foreground">Loading…</p>;
  if (groups.length === 0) return <p className="text-sm text-muted-foreground">No mappings yet. Run a scan.</p>;

  return (
    <TooltipProvider>
      <div className="rounded-md border">
        <table className="w-full text-sm">
        <tbody>
          {groups.map((g) => (
            <MappingGroupRows
              key={g.mappingId}
              group={g}
              providerMeta={providerMeta}
              scanMapping={scanMapping}
              block={block}
              unblock={unblock}
              ignore={ignore}
              mappingScanStates={mappingScanStates}
              onSearch={onSearch}
            />
          ))}
        </tbody>
        </table>
      </div>
    </TooltipProvider>
  );
}

/** Renders the grouped mapping: title header (with member-source summary + group-level Scan) + a left cover spanning all provider rows. */
function MappingGroupRows({ group, providerMeta, scanMapping, block, unblock, ignore, onSearch, mappingScanStates }: {
  group: ContributionMappingGroup;
  providerMeta: ContributionMappingsPage['providerMeta'];
} & MappingTableActions) {
  const providers = group.providers ?? [];
  const span = Math.max(providers.length, 1);
  const scanState = mappingScanStates?.[group.mappingId];
  const isScanning = scanState === 'scanning';
  const sourceSummary = (group.sources ?? []).map(s => s.sourceName || s.sourceKey).filter(Boolean);
  const altTitles = group.titles?.filter(t => t !== group.displayTitle).join(' · ') ?? '';
  return (
    <>
      <tr className="border-b bg-muted last:border-0">
        <td colSpan={7} className="py-2 pl-4 pr-3">
          <div className="flex items-center gap-2">
            <Layers className="h-4 w-4 shrink-0 text-muted-foreground" />
            <div className="min-w-0">
              <span className="font-semibold">{group.displayTitle || group.mappingId}</span>
              {sourceSummary.length > 0 && (
                <div className="text-xs text-muted-foreground truncate max-w-[520px]">
                  {sourceSummary.join(' · ')}
                </div>
              )}
              {altTitles && (
                <div className="text-xs text-muted-foreground truncate max-w-[520px]">{altTitles}</div>
              )}
            </div>
            <Button
              variant="outline"
              size="sm"
              className="ml-auto gap-1 shrink-0"
              onClick={() => scanMapping.mutate(group.mappingId)}
              disabled={isScanning}
              title={
                scanState === 'done'
                  ? 'Scan complete'
                  : scanState === 'failed'
                    ? 'Scan failed'
                    : isScanning
                      ? `Scanning ${group.displayTitle || 'mapping'}…`
                      : `Scan ${group.displayTitle || 'mapping'} against providers`
              }
            >
              {scanState === 'done' ? (
                <Check className="h-3.5 w-3.5" />
              ) : scanState === 'failed' ? (
                <AlertCircle className="h-3.5 w-3.5" />
              ) : (
                <RotateCcw className={`h-3.5 w-3.5 ${isScanning ? 'animate-spin' : ''}`} />
              )}
              {scanState === 'done'
                ? 'Done'
                : scanState === 'failed'
                  ? 'Failed'
                  : isScanning
                    ? 'Scanning…'
                    : 'Scan'}
            </Button>
          </div>
        </td>
      </tr>
      {providers.map((m, i) => (
        <tr key={`${group.mappingId}-${m.provider}`} className="border-b last:border-0">
          {i === 0 && (
            <td rowSpan={span} className="py-2 align-top pl-3 pr-1">
              {group.coverUrl ? (
                <ThumbnailPopover src={group.coverUrl} alt={group.displayTitle} className="w-36 rounded object-cover flex-shrink-0" />
              ) : (
                <div className="w-36 h-48 rounded bg-muted flex items-center justify-center text-xs text-muted-foreground">No cover</div>
              )}
            </td>
          )}
          <td className="py-2 pl-1">
            {(() => {
              const meta = providerMeta?.[ScrobblerProvider[m.provider]];
              return (
                <div className="flex items-center gap-2">
                  {meta?.icon && (
                    <img src={meta.icon} alt="" className="h-5 w-5 rounded-full flex-shrink-0" />
                  )}
                  <span>{ScrobblerProvider[m.provider]}</span>
                </div>
              );
            })()}
          </td>
          <td className="py-2 px-2">
            {m.providerCoverUrl ? (
              <ThumbnailPopover src={m.providerCoverUrl} alt={m.externalSeriesTitle} className="h-14 w-10 rounded object-cover flex-shrink-0" />
            ) : '—'}
          </td>
          <td className="py-2 pr-3">
            <div className="flex flex-col">
              <span>{m.externalSeriesTitle || '—'}</span>
              {(() => {
                const id = m.externalSeriesId;
                const url = providerMeta?.[ScrobblerProvider[m.provider]]?.seriesUrlTemplate;
                if (!id || !url) return <span className="text-xs text-muted-foreground">{id || '—'}</span>;
                return (
                  <a
                    href={url.replace('{0}', encodeURIComponent(id))}
                    target="_blank"
                    rel="noopener noreferrer"
                    className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
                    title={`Open ${ScrobblerProvider[m.provider]} page`}
                  >
                    <ExternalLink className="h-3 w-3 flex-shrink-0" />
                    {id}
                  </a>
                );
              })()}
            </div>
          </td>
          <td className="py-2 pr-3">
            {(() => {
              const alt = m.alternativeTitles ?? [];
              if (alt.length === 0) return '—';
              return (
                <span className="text-xs text-muted-foreground break-words max-w-[280px]">{alt.join(' · ')}</span>
              );
            })()}
          </td>
          <td className="py-2 pr-3">{statusBadge(m.mappingStatus)}</td>
          <td className="py-2 pr-3">
            <div className="flex gap-1">
              <Button
                variant="outline"
                size="sm"
                className="gap-1"
                onClick={() => onSearch({
                  mappingId: group.mappingId,
                  mappingTitle: group.displayTitle || group.mappingId,
                  mappingCoverUrl: group.coverUrl,
                  mappingTitles: group.titles?.join(' · ') ?? undefined,
                  provider: m.provider,
                })}
                title={`Search ${ScrobblerProvider[m.provider]} for a match`}
              >
                <Search className="h-3.5 w-3.5" />
                Search
              </Button>
              {m.mappingStatus === SeriesMappingStatus.Blocked ? (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => unblock.mutate({ mappingId: group.mappingId, provider: m.provider.toString() })}
                  title="Unblock this provider"
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                  Unblock
                </Button>
              ) : (
                (m.mappingStatus === SeriesMappingStatus.AutoMatched || m.mappingStatus === SeriesMappingStatus.UserConfirmed) && (
                  <Button
                    variant="outline"
                    size="sm"
                    className="gap-1"
                    onClick={() => block.mutate({ mappingId: group.mappingId, provider: m.provider.toString() })}
                    title="Block this provider link"
                  >
                    <Ban className="h-3.5 w-3.5" />
                    Block
                  </Button>
                )
              )}
              {/* Temporary ignore (re-evaluated after 1 month) */}
              {m.mappingStatus !== SeriesMappingStatus.TemporaryIgnored && (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => ignore.mutate({ mappingId: group.mappingId, provider: m.provider.toString(), forever: false })}
                  title="Ignore this provider for 1 month (re-evaluated afterward)"
                >
                  <EyeOff className="h-3.5 w-3.5" />
                  Ignore 1m
                </Button>
              )}

              {/* Permanent ignore */}
              {m.mappingStatus !== SeriesMappingStatus.ForeverIgnored && (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => ignore.mutate({ mappingId: group.mappingId, provider: m.provider.toString(), forever: true })}
                  title="Ignore this provider forever"
                >
                  <Clock className="h-3.5 w-3.5" />
                  Ignore always
                </Button>
              )}

              {/* Un-ignore: only when the row is currently ignored (temp or forever). */}
              {(m.mappingStatus === SeriesMappingStatus.TemporaryIgnored || m.mappingStatus === SeriesMappingStatus.ForeverIgnored) && (
                <Button
                  variant="outline"
                  size="sm"
                  className="gap-1"
                  onClick={() => unblock.mutate({ mappingId: group.mappingId, provider: m.provider.toString() })}
                  title="Stop ignoring this provider"
                >
                  <RotateCcw className="h-3.5 w-3.5" />
                  Un-ignore
                </Button>
              )}
            </div>
          </td>
        </tr>
      ))}
    </>
  );
}
