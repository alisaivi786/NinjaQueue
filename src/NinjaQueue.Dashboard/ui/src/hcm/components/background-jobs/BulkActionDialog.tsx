'use client';

import { useMemo, useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';

import { Button } from '../ui/button';
import { Checkbox } from '../ui/checkbox';
import { DataGrid, type DataGridColumn } from '../ui/data-grid';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '../ui/dialog';
import { Input } from '../ui/input';
import { KpiTile } from '../ui/kpi-tile';
import { Label } from '../ui/label';
import { Textarea } from '../ui/textarea';
import { useToast } from '../ui/toast';
import { cn } from '../../utils';

import {
  backgroundJobErrorMessage,
  backgroundJobKeys,
  BULK_MAX_JOB_IDS,
  fetchBulkOperation,
  isBulkFinished,
  jobStatusLabel,
  newIdempotencyKey,
  REASON_MAX_LENGTH,
  submitBulkOperation,
  toBulkFilter,
  type BackgroundJobBulkItem,
  type BackgroundJobBulkReport,
  type BulkOperation,
  type JobListFilters,
} from './background-jobs-api';
import { BulkOutcomeBadge } from './BackgroundJobBadges';
import { JOB_ACTION_COPY, RetryVsRequeueNote } from './JobActionCopy';

type Selector = 'ids' | 'filter';

/**
 * Bulk retry / requeue / cancel (plan §17.9). A request carries exactly ONE selector - the ticked jobs (1-500, applied
 * now, 200 + report) or the list's current filters (runs as a job, 202 + operation id, polled here). The idempotency
 * key is created once per dialog, so a double submit replays the stored report and applies nothing twice.
 */
export function BulkActionDialog({
  operation,
  selectedIds,
  filters,
  tenantId = null,
  filterLabels,
  onClose,
  onDone,
}: {
  operation: BulkOperation;
  selectedIds: string[];
  filters: JobListFilters;
  /** Platform scope only: the tenant the list is filtered to (part of the filter selector). */
  tenantId?: string | null;
  /** Human labels of the active filters, for the confirmation text. */
  filterLabels: string[];
  onClose: () => void;
  onDone: () => void;
}) {
  const { toast } = useToast();
  const copy = JOB_ACTION_COPY[operation];
  const idsAvailable = selectedIds.length > 0;
  const idsTooMany = selectedIds.length > BULK_MAX_JOB_IDS;
  const filterHasCriteria = filterLabels.length > 0;

  const [selector, setSelector] = useState<Selector>(
    idsAvailable && !idsTooMany ? 'ids' : 'filter',
  );
  const [reason, setReason] = useState('');
  const [includeContinuations, setIncludeContinuations] = useState(true);
  const [runAtLocal, setRunAtLocal] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState<BackgroundJobBulkReport | null>(null);
  const idempotencyKey = useMemo(() => newIdempotencyKey(), []);

  const trimmedReason = reason.trim();
  const selectorValid =
    selector === 'ids' ? idsAvailable && !idsTooMany : filterHasCriteria;

  const mutation = useMutation({
    mutationFn: () =>
      submitBulkOperation(
        operation,
        selector === 'ids'
          ? { jobIds: selectedIds }
          : { filter: toBulkFilter(filters, tenantId) },
        {
          reason: trimmedReason,
          idempotencyKey,
          includeContinuations,
          runAtUtc:
            operation === 'requeue' && runAtLocal
              ? new Date(runAtLocal).toISOString()
              : null,
        },
      ),
    onSuccess: ({ report: result, accepted }) => {
      setReport(result);
      if (result?.replayed) {
        toast({
          variant: 'info',
          title: 'Already submitted',
          description:
            'This bulk action was already applied. Showing its stored report.',
        });
      } else if (accepted) {
        toast({
          variant: 'info',
          title: 'Bulk action started',
          description: 'It runs in the background. Progress appears below.',
        });
      } else {
        toast({
          variant: 'success',
          title: 'Bulk action applied',
          description: `${result?.applied ?? 0} of ${result?.requested ?? 0} jobs changed.`,
        });
      }
      onDone();
    },
    onError: (err) => setError(backgroundJobErrorMessage(err)),
  });

  // A filter-based operation runs as a job: poll its report until it finishes.
  const pollQuery = useQuery({
    queryKey: backgroundJobKeys.bulk(report?.operationId ?? ''),
    queryFn: () => fetchBulkOperation(report!.operationId),
    enabled: Boolean(report) && !isBulkFinished(report),
    refetchInterval: (query) =>
      isBulkFinished(query.state.data) ? false : 2000,
    retry: false,
  });
  const liveReport = pollQuery.data ?? report;

  const canSubmit =
    selectorValid &&
    trimmedReason.length > 0 &&
    confirmed &&
    !mutation.isPending &&
    !report;

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent className={cn(liveReport ? 'max-w-4xl' : 'max-w-xl')}>
        <DialogHeader>
          <DialogTitle>{copy.bulkTitle}</DialogTitle>
          <DialogDescription className="text-muted-foreground">
            {copy.summary}
          </DialogDescription>
        </DialogHeader>

        {liveReport ? (
          <BulkReportView
            report={liveReport}
            isPolling={!isBulkFinished(liveReport)}
          />
        ) : (
          <>
            {operation !== 'cancel' ? <RetryVsRequeueNote /> : null}

            <fieldset className="grid gap-2">
              <legend className="mb-1 text-sm font-medium text-foreground">
                Which jobs? (choose one)
              </legend>
              <SelectorOption
                checked={selector === 'ids'}
                disabled={!idsAvailable || idsTooMany}
                onSelect={() => setSelector('ids')}
                title={`The ${selectedIds.length} selected job${selectedIds.length === 1 ? '' : 's'}`}
                hint={
                  !idsAvailable
                    ? 'Tick jobs in the list to use this option.'
                    : idsTooMany
                      ? `At most ${BULK_MAX_JOB_IDS} jobs can be selected. Use the filters instead.`
                      : 'Applied now. Jobs that are not eligible are skipped and listed in the report.'
                }
              />
              <SelectorOption
                checked={selector === 'filter'}
                disabled={!filterHasCriteria}
                onSelect={() => setSelector('filter')}
                title="Every job matching the current filters"
                hint={
                  !filterHasCriteria
                    ? 'Set at least one filter (status, queue, job type, tenant or dates) first.'
                    : `Runs in the background, in chunks. Filters: ${filterLabels.join(' · ')}. Includes jobs on other pages.`
                }
              />
            </fieldset>

            <div className="grid gap-1.5">
              <Label htmlFor="bulk-action-reason">Reason (required)</Label>
              <Textarea
                id="bulk-action-reason"
                rows={3}
                maxLength={REASON_MAX_LENGTH}
                value={reason}
                onChange={(event) => setReason(event.target.value)}
                placeholder="Why are you doing this? Recorded in the audit log."
              />
            </div>

            {operation === 'requeue' ? (
              <div className="grid gap-1.5">
                <Label htmlFor="bulk-action-run-at">Run at (optional)</Label>
                <Input
                  id="bulk-action-run-at"
                  type="datetime-local"
                  value={runAtLocal}
                  onChange={(event) => setRunAtLocal(event.target.value)}
                />
                <p className="text-xs text-muted-foreground">
                  Leave empty to run now. Your local time.
                </p>
              </div>
            ) : null}

            {operation !== 'cancel' ? (
              <label className="flex items-start gap-2 text-sm">
                <Checkbox
                  checked={includeContinuations}
                  onCheckedChange={(value) =>
                    setIncludeContinuations(value === true)
                  }
                  className="mt-0.5"
                />
                <span>
                  Also restore follow-up jobs that were stopped because these
                  jobs failed or were cancelled
                </span>
              </label>
            ) : null}

            <label className="flex items-start gap-2 text-sm">
              <Checkbox
                checked={confirmed}
                onCheckedChange={(value) => setConfirmed(value === true)}
                className="mt-0.5"
              />
              <span>
                I understand this changes{' '}
                {selector === 'ids'
                  ? `up to ${selectedIds.length} jobs`
                  : 'every matching job, including ones not shown on this page'}{' '}
                and is recorded in the audit log.
              </span>
            </label>
          </>
        )}

        {error ? (
          <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {error}
          </div>
        ) : null}
        {pollQuery.isError ? (
          <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {backgroundJobErrorMessage(
              pollQuery.error,
              'Could not load the bulk operation progress.',
            )}
          </div>
        ) : null}

        <DialogFooter className="gap-2">
          <Button type="button" variant="outline" onClick={onClose}>
            Close
          </Button>
          {!liveReport ? (
            <Button
              type="button"
              variant={operation === 'cancel' ? 'destructive' : 'default'}
              disabled={!canSubmit}
              onClick={() => {
                setError(null);
                mutation.mutate();
              }}
            >
              {mutation.isPending ? 'Working...' : copy.bulkConfirmLabel}
            </Button>
          ) : null}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function SelectorOption({
  checked,
  disabled,
  onSelect,
  title,
  hint,
}: {
  checked: boolean;
  disabled: boolean;
  onSelect: () => void;
  title: string;
  hint: string;
}) {
  return (
    <label
      className={cn(
        'flex cursor-pointer items-start gap-3 rounded-lg border px-3 py-2',
        checked ? 'border-primary bg-primary/5' : 'border-border',
        disabled && 'cursor-not-allowed opacity-60',
      )}
    >
      <input
        type="radio"
        name="bulk-selector"
        className="mt-1"
        checked={checked}
        disabled={disabled}
        onChange={onSelect}
      />
      <span>
        <span className="block text-sm font-medium text-foreground">
          {title}
        </span>
        <span className="block text-xs text-muted-foreground">{hint}</span>
      </span>
    </label>
  );
}

export function BulkReportView({
  report,
  isPolling,
}: {
  report: BackgroundJobBulkReport;
  isPolling: boolean;
}) {
  const columns: Array<DataGridColumn<BackgroundJobBulkItem>> = [
    { key: 'jobId', label: 'Job', minWidth: '280px', copyable: true },
    {
      key: 'outcome',
      label: 'Outcome',
      minWidth: '120px',
      render: (row) => <BulkOutcomeBadge outcome={row.outcome} />,
    },
    {
      key: 'status',
      label: 'Status',
      minWidth: '120px',
      render: (row) => (row.status ? jobStatusLabel(row.status) : '-'),
    },
    {
      key: 'error',
      label: 'Detail',
      minWidth: '240px',
      render: (row) =>
        row.holderJobId ? (
          <span className="text-xs">
            Unique key held by job {row.holderJobId}
          </span>
        ) : row.error ? (
          <span className="text-xs text-destructive">{row.error}</span>
        ) : (
          <span className="text-muted-foreground">-</span>
        ),
    },
  ];

  return (
    <div className="grid gap-4">
      <p className="text-sm text-muted-foreground">
        Operation {report.operationId} · {report.operation} ·{' '}
        <span className="font-medium text-foreground">{report.status}</span>
        {report.replayed ? ' · replayed (nothing applied twice)' : ''}
        {isPolling ? ' · updating...' : ''}
      </p>
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-6">
        <KpiTile label="Requested" value={report.requested} />
        <KpiTile label="Applied" value={report.applied} tone="success" />
        <KpiTile
          label="Not eligible"
          value={report.notEligible}
          tone={report.notEligible ? 'warning' : 'default'}
        />
        <KpiTile
          label="Conflict"
          value={report.conflict}
          tone={report.conflict ? 'warning' : 'default'}
        />
        <KpiTile label="Not found" value={report.notFound} />
        <KpiTile
          label="Failed"
          value={report.failed}
          tone={report.failed ? 'danger' : 'default'}
        />
      </div>
      {report.items.length > 0 ? (
        <DataGrid<BackgroundJobBulkItem>
          rows={report.items}
          rowKey={(row) => row.jobId}
          columns={columns}
          paginationMode="client"
          defaultPageSize={10}
          showPageSizeSelector={false}
          showColumnOptions={false}
          enableColumnReorder={false}
        />
      ) : null}
    </div>
  );
}