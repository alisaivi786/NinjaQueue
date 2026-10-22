'use client';

import { useMemo, useState } from 'react';
import { useRouter } from 'next/navigation';
import {
  keepPreviousData,
  useQuery,
  useQueryClient,
} from '@tanstack/react-query';
import {
  Ban,
  ChevronLeft,
  ChevronRight,
  Eye,
  ListRestart,
  RotateCcw,
  Search,
  X,
} from 'lucide-react';

import { Button } from '../ui/button';
import { Card, CardContent } from '../ui/card';
import { Checkbox } from '../ui/checkbox';
import { DataGrid, type DataGridColumn } from '../ui/data-grid';
import { DatePicker } from '../ui/date-picker';
import { KpiTile } from '../ui/kpi-tile';
import { Label } from '../ui/label';
import { Select } from '../ui/select';
import { formatLocalDateTime } from '@shared';

import {
  backgroundJobErrorMessage,
  backgroundJobKeys,
  BULK_MAX_JOB_IDS,
  CANCELLABLE_STATUSES,
  EMPTY_JOB_FILTERS,
  fetchBackgroundJobs,
  fetchBackgroundJobLookups,
  fetchBackgroundJobStats,
  formatJobDuration,
  JOB_STATUSES,
  jobStatusLabel,
  JOBS_PAGE_SIZE_OPTIONS,
  REQUEUEABLE_STATUSES,
  STATS_WINDOW_OPTIONS,
  RETRYABLE_STATUSES,
  type BackgroundJobAccess,
  type BackgroundJobScope,
  type BackgroundJobSummary,
  type BulkOperation,
  type JobListFilters,
} from './background-jobs-api';
import { JobStatusBadge, JobTenantCell } from './BackgroundJobBadges';
import { BulkActionDialog } from './BulkActionDialog';
import { JobActionDialog, type JobAction } from './JobActionDialog';

/**
 * The job list: server-side filters, keyset paging (Previous / Next), row actions and bulk actions. In platform scope
 * the list (and its figures and bulk filter) follow `tenantId`, chosen outside the tab; in tenant scope the server
 * returns the caller's own tenant only and no tenant is ever sent.
 */
export function JobsTab({
  scope,
  access,
  enabled,
  basePath,
  tenantId,
  tenantName,
}: {
  scope: BackgroundJobScope;
  access: BackgroundJobAccess;
  enabled: boolean;
  basePath: string;
  /** Platform scope only: one tenant's jobs, or null for every tenant. */
  tenantId: string | null;
  tenantName: (id: string | null) => string;
}) {
  const isPlatform = scope === 'platform';
  const tenant = isPlatform ? tenantId : null;
  const router = useRouter();
  const queryClient = useQueryClient();

  const [draft, setDraft] = useState<JobListFilters>(EMPTY_JOB_FILTERS);
  const [filters, setFilters] = useState<JobListFilters>(EMPTY_JOB_FILTERS);
  const [pageSize, setPageSize] = useState(50);
  // Keyset paging: cursors[i] opens page i + 1 (null = first page).
  const [cursors, setCursors] = useState<Array<string | null>>([null]);
  const pageIndex = cursors.length - 1;
  const cursor = cursors[pageIndex];
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [rowAction, setRowAction] = useState<{
    action: JobAction;
    job: BackgroundJobSummary;
  } | null>(null);
  const [bulkOperation, setBulkOperation] = useState<BulkOperation | null>(
    null,
  );

  const listQuery = useQuery({
    queryKey: backgroundJobKeys.list(filters, tenant, cursor, pageSize),
    queryFn: () => fetchBackgroundJobs(filters, tenant, cursor, pageSize),
    enabled,
    placeholderData: keepPreviousData,
    retry: false,
  });

  // Tenant scope: the caller's own tenant's figures. Platform scope: platform-wide, or the selected tenant's.
  const [windowHours, setWindowHours] = useState(24);
  const statsQuery = useQuery({
    queryKey: backgroundJobKeys.stats(windowHours, tenant),
    queryFn: () => fetchBackgroundJobStats(windowHours, tenant),
    enabled,
    placeholderData: keepPreviousData,
    retry: false,
  });

  const lookupsQuery = useQuery({
    queryKey: backgroundJobKeys.lookups,
    queryFn: fetchBackgroundJobLookups,
    enabled,
    staleTime: 5 * 60 * 1000,
    retry: false,
  });

  const rows = listQuery.data?.items ?? [];
  const nextCursor = listQuery.data?.nextCursor ?? null;
  const stats = statsQuery.data;
  const queues = stats?.queues ?? [];
  const lookups = lookupsQuery.data;
  const statusOptions = (
    lookups?.statuses.length ? lookups.statuses : [...JOB_STATUSES]
  ).map((status) => ({ value: status, label: jobStatusLabel(status) }));
  // Platform (System) job types are only visible in platform scope.
  const visibleJobTypes = (lookups?.jobTypes ?? []).filter(
    (type) => isPlatform || type.tenancy !== 'System',
  );
  const queueOptions = [
    ...new Set(
      lookups
        ? visibleJobTypes.map((type) => type.queue)
        : queues.map((queue) => queue.queue),
    ),
  ]
    .sort()
    .map((queue) => ({ value: queue, label: queue }));
  const jobTypeOptions = visibleJobTypes
    .filter((type) => !draft.queue || type.queue === draft.queue)
    .map((type) => ({
      value: type.code,
      label: type.tenancy === 'System' ? `${type.code} (platform)` : type.code,
    }));

  const resetPaging = () => {
    setCursors([null]);
    setSelected(new Set());
  };

  const applyFilters = (next: JobListFilters) => {
    setFilters(next);
    resetPaging();
  };

  const refreshAll = async () => {
    await queryClient.invalidateQueries({ queryKey: backgroundJobKeys.all });
  };

  const filterLabels = useMemo(() => {
    const labels: string[] = [];
    if (filters.status) labels.push(`status ${jobStatusLabel(filters.status)}`);
    if (filters.queue) labels.push(`queue ${filters.queue}`);
    if (filters.jobType) labels.push(`type ${filters.jobType}`);
    if (tenant) labels.push(`tenant ${tenantName(tenant)}`);
    if (filters.fromDate) labels.push(`from ${filters.fromDate}`);
    if (filters.toDate) labels.push(`to ${filters.toDate}`);
    return labels;
  }, [filters, tenant, tenantName]);

  const toggleRow = (id: string, checked: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      if (checked) next.add(id);
      else next.delete(id);
      return next;
    });

  const allOnPageSelected =
    rows.length > 0 && rows.every((row) => selected.has(row.id));
  const togglePage = (checked: boolean) =>
    setSelected((current) => {
      const next = new Set(current);
      rows.forEach((row) => (checked ? next.add(row.id) : next.delete(row.id)));
      return next;
    });

  const columns: Array<DataGridColumn<BackgroundJobSummary>> = [];
  if (access.canBulk) {
    columns.push({
      key: 'id',
      id: 'select',
      label: '',
      width: '44px',
      required: true,
      showInColumnOptions: false,
      render: (row) => (
        <Checkbox
          aria-label={`Select job ${row.id}`}
          checked={selected.has(row.id)}
          onCheckedChange={(value) => toggleRow(row.id, value === true)}
        />
      ),
    });
  }
  columns.push(
    {
      key: 'jobType',
      label: 'Job type',
      minWidth: '240px',
      required: true,
      render: (row) => (
        <div className="min-w-0">
          <p
            className="truncate font-mono text-xs text-foreground"
            title={row.jobType}
          >
            {row.jobType}
          </p>
          <p className="truncate text-xs text-muted-foreground">{row.id}</p>
        </div>
      ),
    },
    {
      key: 'status',
      label: 'Status',
      minWidth: '150px',
      render: (row) => (
        <div className="flex flex-col items-start gap-1">
          <JobStatusBadge status={row.status} />
          {row.cancelRequestedAtUtc &&
          !['Succeeded', 'Cancelled', 'DeadLettered'].includes(row.status) ? (
            <span className="text-xs text-muted-foreground">
              Cancel requested
            </span>
          ) : null}
          {row.terminalReason ? (
            <span className="text-xs text-muted-foreground">
              {row.terminalReason}
            </span>
          ) : null}
        </div>
      ),
    },
    { key: 'queue', label: 'Queue', minWidth: '110px' },
  );
  if (isPlatform) {
    columns.push({
      key: 'tenantId',
      label: 'Tenant',
      minWidth: '160px',
      render: (row) => (
        <JobTenantCell tenantId={row.tenantId} tenantName={tenantName} />
      ),
    });
  }
  columns.push(
    {
      key: 'attempt',
      label: 'Attempts',
      minWidth: '100px',
      render: (row) => (
        <div>
          <p className="tabular-nums">
            {row.attempt} / {row.maxAttempts}
          </p>
          {row.progressTotal ? (
            <p className="text-xs text-muted-foreground">
              {row.progressProcessed ?? 0} of {row.progressTotal}
            </p>
          ) : null}
        </div>
      ),
    },
    {
      key: 'createdAtUtc',
      label: 'Created',
      minWidth: '160px',
      render: (row) => formatLocalDateTime(row.createdAtUtc),
    },
    {
      key: 'runAtUtc',
      label: 'Run at / finished',
      minWidth: '160px',
      render: (row) => formatLocalDateTime(row.completedAtUtc ?? row.runAtUtc),
    },
    {
      key: 'lastError',
      label: 'Last error',
      minWidth: '220px',
      render: (row) =>
        row.lastError || row.lastErrorCode ? (
          <div className="text-xs">
            {row.lastErrorCode ? (
              <p className="font-mono text-foreground">{row.lastErrorCode}</p>
            ) : null}
            {row.lastError ? (
              <p
                className="line-clamp-2 max-w-[320px] break-words text-destructive"
                title={row.lastError}
              >
                {row.lastError}
              </p>
            ) : null}
          </div>
        ) : (
          <span className="text-muted-foreground">-</span>
        ),
    },
    {
      key: 'source',
      id: 'actions',
      label: 'Actions',
      minWidth: '150px',
      required: true,
      render: (row) => (
        <div className="flex items-center gap-1">
          <Button
            type="button"
            size="icon"
            variant="ghost"
            title="View details"
            aria-label="View details"
            onClick={() => router.push(`${basePath}/${row.id}`)}
          >
            <Eye className="h-4 w-4" />
          </Button>
          {access.canManage && RETRYABLE_STATUSES.includes(row.status) ? (
            <Button
              type="button"
              size="icon"
              variant="ghost"
              title="Retry: one more attempt now"
              aria-label="Retry"
              onClick={() => setRowAction({ action: 'retry', job: row })}
            >
              <RotateCcw className="h-4 w-4" />
            </Button>
          ) : null}
          {access.canManage && REQUEUEABLE_STATUSES.includes(row.status) ? (
            <Button
              type="button"
              size="icon"
              variant="ghost"
              title="Requeue: a fresh set of attempts"
              aria-label="Requeue"
              onClick={() => setRowAction({ action: 'requeue', job: row })}
            >
              <ListRestart className="h-4 w-4" />
            </Button>
          ) : null}
          {access.canManage && CANCELLABLE_STATUSES.includes(row.status) ? (
            <Button
              type="button"
              size="icon"
              variant="ghost"
              title="Cancel"
              aria-label="Cancel"
              onClick={() => setRowAction({ action: 'cancel', job: row })}
            >
              <Ban className="h-4 w-4 text-destructive" />
            </Button>
          ) : null}
        </div>
      ),
    },
  );

  const oldest = queues.reduce(
    (max, queue) => Math.max(max, queue.oldestPendingAgeSeconds),
    0,
  );
  const totals = stats?.totals;
  const count = (value: number | undefined) =>
    value === undefined ? '-' : value.toLocaleString();
  const decimal = (value: number) =>
    value.toLocaleString(undefined, { maximumFractionDigits: 1 });
  const failureRate = stats?.failureRate ?? null;
  const selectedIds = [...selected];

  return (
    <div className="grid gap-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <p className="text-sm text-muted-foreground">
          {stats
            ? `${stats.scope === 'Tenant' ? (isPlatform ? `Jobs of ${tenantName(stats.tenantId)}` : "Your tenant's jobs") : 'Every tenant, including platform jobs'}. Finished-job figures since ${formatLocalDateTime(stats.sinceUtc)}.`
            : null}
        </p>
        <div className="grid gap-1.5">
          <Label htmlFor="jobs-stats-window">Figures for</Label>
          <Select
            id="jobs-stats-window"
            className="w-44"
            value={String(windowHours)}
            clearable={false}
            onChange={(value) => setWindowHours(Number(value) || 24)}
            options={STATS_WINDOW_OPTIONS}
          />
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <KpiTile
          label="Ready to run"
          value={count(totals?.enqueued)}
          hint={
            totals
              ? `${count(totals.scheduled)} scheduled, ${count(totals.awaiting)} awaiting`
              : undefined
          }
        />
        <KpiTile label="Processing" value={count(totals?.processing)} />
        <KpiTile
          label="Oldest waiting"
          value={stats ? formatJobDuration(oldest) : '-'}
          tone={oldest > 600 ? 'warning' : 'default'}
          hint="Age of the oldest ready job"
        />
        <KpiTile
          label="Dead-lettered (retained)"
          value={count(stats?.deadLetteredCount)}
          tone={stats?.deadLetteredCount ? 'danger' : 'default'}
          hint="Every one still kept - retry or requeue"
        />
        <KpiTile
          label="Succeeded"
          value={count(totals?.succeeded)}
          tone="success"
          hint="In the selected window"
        />
        <KpiTile
          label="Failed / cancelled"
          value={
            totals
              ? `${count(totals.deadLettered)} / ${count(totals.cancelled)}`
              : '-'
          }
          tone={totals?.deadLettered ? 'danger' : 'default'}
          hint="Dead-lettered / cancelled in the window"
        />
        <KpiTile
          label="Throughput"
          value={stats ? `${decimal(stats.throughputPerHour)}/h` : '-'}
          hint="Finished jobs per hour"
        />
        <KpiTile
          label="Failure rate"
          value={failureRate === null ? '-' : `${decimal(failureRate * 100)}%`}
          tone={(failureRate ?? 0) > 0.05 ? 'danger' : 'default'}
          hint={
            stats && failureRate === null
              ? 'Nothing finished in the window'
              : 'Of jobs finished in the window'
          }
        />
      </div>
      {statsQuery.isError ? (
        <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {backgroundJobErrorMessage(
            statsQuery.error,
            'Failed to load the job statistics.',
          )}
        </div>
      ) : null}
      {lookupsQuery.isError ? (
        <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {backgroundJobErrorMessage(
            lookupsQuery.error,
            'Failed to load the job type and queue lists.',
          )}
        </div>
      ) : null}

      <Card>
        <CardContent className="grid gap-4 py-4">
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-5">
            <div className="grid gap-1.5">
              <Label htmlFor="jobs-filter-status">Status</Label>
              <Select
                id="jobs-filter-status"
                value={draft.status}
                onChange={(status) =>
                  setDraft((current) => ({ ...current, status }))
                }
                placeholder="Any status"
                options={statusOptions}
              />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="jobs-filter-queue">Queue</Label>
              <Select
                id="jobs-filter-queue"
                value={draft.queue}
                disabled={lookupsQuery.isLoading}
                onChange={(queue) =>
                  setDraft((current) => ({
                    ...current,
                    queue,
                    // Keep the job type only if it runs on the chosen queue.
                    jobType:
                      queue &&
                      visibleJobTypes.find(
                        (type) => type.code === current.jobType,
                      )?.queue !== queue
                        ? ''
                        : current.jobType,
                  }))
                }
                placeholder="Any queue"
                options={queueOptions}
              />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="jobs-filter-type">Job type</Label>
              <Select
                id="jobs-filter-type"
                value={draft.jobType}
                disabled={lookupsQuery.isLoading}
                onChange={(jobType) =>
                  setDraft((current) => ({ ...current, jobType }))
                }
                placeholder="Any job type"
                options={jobTypeOptions}
                searchable
                searchPlaceholder="Search job types..."
              />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="jobs-filter-from">Created from</Label>
              <DatePicker
                id="jobs-filter-from"
                value={draft.fromDate}
                maxDate={draft.toDate || undefined}
                onChange={(fromDate) =>
                  setDraft((current) => ({ ...current, fromDate }))
                }
                onClear={() =>
                  setDraft((current) => ({ ...current, fromDate: '' }))
                }
              />
            </div>
            <div className="grid gap-1.5">
              <Label htmlFor="jobs-filter-to">Created to</Label>
              <DatePicker
                id="jobs-filter-to"
                value={draft.toDate}
                minDate={draft.fromDate || undefined}
                onChange={(toDate) =>
                  setDraft((current) => ({ ...current, toDate }))
                }
                onClear={() =>
                  setDraft((current) => ({ ...current, toDate: '' }))
                }
              />
            </div>
          </div>
          <div className="flex flex-wrap items-center justify-end gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => {
                setDraft(EMPTY_JOB_FILTERS);
                applyFilters(EMPTY_JOB_FILTERS);
              }}
            >
              <X className="h-4 w-4" /> Reset
            </Button>
            <Button type="button" size="sm" onClick={() => applyFilters(draft)}>
              <Search className="h-4 w-4" /> Apply filters
            </Button>
          </div>
        </CardContent>
      </Card>

      {listQuery.isError ? (
        <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {backgroundJobErrorMessage(
            listQuery.error,
            'Failed to load background jobs. Please try refresh.',
          )}
        </div>
      ) : null}

      {access.canBulk ? (
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <label className="flex items-center gap-2">
            <Checkbox
              checked={allOnPageSelected}
              onCheckedChange={(value) => togglePage(value === true)}
              aria-label="Select every job on this page"
            />
            Select page
          </label>
          <span
            className={
              selected.size > BULK_MAX_JOB_IDS
                ? 'text-destructive'
                : 'text-muted-foreground'
            }
          >
            {selected.size} selected
            {selected.size > BULK_MAX_JOB_IDS
              ? ` (max ${BULK_MAX_JOB_IDS})`
              : ''}
          </span>
          {selected.size > 0 ? (
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => setSelected(new Set())}
            >
              Clear selection
            </Button>
          ) : null}
        </div>
      ) : null}

      <DataGrid<BackgroundJobSummary>
        rows={rows}
        isLoading={listQuery.isLoading || listQuery.isFetching}
        rowKey={(row) => row.id}
        columns={columns}
        paginationMode="server"
        pageSize={pageSize}
        showPagination={false}
        sortMode="client"
        enableColumnReorder={false}
        noRecordsLabel="No background jobs match these filters."
        showRefresh
        onRefresh={refreshAll}
        toolbarButtons={
          access.canBulk
            ? [
                {
                  key: 'bulk-retry',
                  label: 'Bulk retry',
                  icon: <RotateCcw className="h-4 w-4" />,
                  onClick: () => setBulkOperation('retry'),
                  variant: 'outline',
                },
                {
                  key: 'bulk-requeue',
                  label: 'Bulk requeue',
                  icon: <ListRestart className="h-4 w-4" />,
                  onClick: () => setBulkOperation('requeue'),
                  variant: 'outline',
                },
                {
                  key: 'bulk-cancel',
                  label: 'Bulk cancel',
                  icon: <Ban className="h-4 w-4" />,
                  onClick: () => setBulkOperation('cancel'),
                  variant: 'danger',
                },
              ]
            : []
        }
      />

      <div className="flex flex-wrap items-center justify-between gap-3 text-sm">
        <div className="flex items-center gap-2 text-muted-foreground">
          <span>Rows per page</span>
          <Select
            className="w-24"
            value={String(pageSize)}
            clearable={false}
            onChange={(value) => {
              setPageSize(Number(value) || 50);
              resetPaging();
            }}
            options={JOBS_PAGE_SIZE_OPTIONS.map((size) => ({
              value: String(size),
              label: String(size),
            }))}
          />
        </div>
        <div className="flex items-center gap-2">
          <span className="text-muted-foreground">Page {pageIndex + 1}</span>
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={pageIndex === 0 || listQuery.isFetching}
            onClick={() => setCursors((current) => current.slice(0, -1))}
          >
            <ChevronLeft className="h-4 w-4" /> Previous
          </Button>
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={!nextCursor || listQuery.isFetching}
            onClick={() =>
              nextCursor && setCursors((current) => [...current, nextCursor])
            }
          >
            Next <ChevronRight className="h-4 w-4" />
          </Button>
        </div>
      </div>

      {rowAction ? (
        <JobActionDialog
          action={rowAction.action}
          job={rowAction.job}
          onClose={() => setRowAction(null)}
          onDone={() => {
            setRowAction(null);
            void refreshAll();
          }}
        />
      ) : null}

      {bulkOperation ? (
        <BulkActionDialog
          operation={bulkOperation}
          selectedIds={selectedIds}
          filters={filters}
          tenantId={tenant}
          filterLabels={filterLabels}
          onClose={() => setBulkOperation(null)}
          onDone={() => {
            setSelected(new Set());
            void refreshAll();
          }}
        />
      ) : null}
    </div>
  );
}