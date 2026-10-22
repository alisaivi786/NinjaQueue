'use client';

import { useState, type ReactNode } from 'react';
import { useRouter } from 'next/navigation';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Ban, ListRestart, RotateCcw } from 'lucide-react';

import { Badge } from '../ui/badge';
import { Button } from '../ui/button';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { DataGrid, type DataGridColumn } from '../ui/data-grid';
import { formatLocalDateTime } from '@shared';

import { JobStatusBadge } from './BackgroundJobBadges';
import { RetryVsRequeueNote } from './JobActionCopy';
import { JobActionDialog, type JobAction } from './JobActionDialog';
import {
  backgroundJobErrorCode,
  backgroundJobErrorMessage,
  backgroundJobKeys,
  BACKGROUND_JOB_ERROR_CODES,
  CANCELLABLE_STATUSES,
  fetchBackgroundJob,
  PLATFORM_BACKGROUND_JOB_ACCESS,
  REQUEUEABLE_STATUSES,
  RETRYABLE_STATUSES,
  type BackgroundJobAccess,
  type BackgroundJobAttempt,
  type BackgroundJobEvent,
  type BackgroundJobScope,
  type BackgroundJobSideEffect,
} from './background-jobs-api';

function prettyJson(value: string) {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <p className="text-xs font-medium uppercase tracking-wider text-muted-foreground">
        {label}
      </p>
      <div className="mt-0.5 break-words text-sm text-foreground">
        {children ?? '-'}
      </div>
    </div>
  );
}

const NO_ACCESS: BackgroundJobAccess = {
  canView: false,
  canManage: false,
  canBulk: false,
  canViewPayload: false,
};

export type BackgroundJobDetailViewProps = {
  /** 'tenant' (Admin portal, own tenant only) or 'platform' (Console, every tenant, everything allowed). */
  scope: BackgroundJobScope;
  jobId: string;
  /** The Background Jobs page; another job's detail page (the parent job) is `${basePath}/${jobId}`. */
  basePath: string;
  /** False until the caller may load data (signed in, hydrated, allowed). */
  enabled: boolean;
  /** Tenant scope: what the user may do (see backgroundJobAccess). Ignored in platform scope. */
  access?: BackgroundJobAccess;
  /** Platform scope: a tenant's display name ('Platform' for null). Defaults to the TenantId. */
  tenantName?: (id: string | null) => string;
};

/**
 * One background job: status and actions, last error, attempts, events, external steps and (with view-payload) the
 * masked payload. No page header or breadcrumbs - each app wraps it in its own.
 */
export function BackgroundJobDetailView({
  scope,
  jobId: id,
  basePath,
  enabled,
  access: tenantAccess,
  tenantName = (tenantId) => tenantId ?? 'Platform',
}: BackgroundJobDetailViewProps) {
  const isPlatform = scope === 'platform';
  const access = isPlatform
    ? PLATFORM_BACKGROUND_JOB_ACCESS
    : (tenantAccess ?? NO_ACCESS);
  const router = useRouter();
  const queryClient = useQueryClient();
  const [action, setAction] = useState<JobAction | null>(null);

  const detailQuery = useQuery({
    queryKey: backgroundJobKeys.detail(id),
    queryFn: () => fetchBackgroundJob(id),
    enabled: enabled && access.canView,
    retry: false,
    // A running job moves on its own: keep the page current while it is active.
    refetchInterval: (query) =>
      query.state.data &&
      CANCELLABLE_STATUSES.includes(query.state.data.job.status)
        ? 5000
        : false,
  });

  const detail = detailQuery.data;
  const job = detail?.job;
  const notFound =
    backgroundJobErrorCode(detailQuery.error) ===
    BACKGROUND_JOB_ERROR_CODES.jobNotFound;

  const attemptColumns: Array<DataGridColumn<BackgroundJobAttempt>> = [
    { key: 'attemptSeq', label: '#', width: '60px' },
    {
      key: 'outcome',
      label: 'Outcome',
      minWidth: '120px',
      render: (row) => row.outcome ?? 'Running',
    },
    {
      key: 'workerId',
      label: 'Worker',
      minWidth: '180px',
      render: (row) => (
        <span className="font-mono text-xs">{row.workerId}</span>
      ),
    },
    {
      key: 'startedAtUtc',
      label: 'Started',
      minWidth: '160px',
      render: (row) => formatLocalDateTime(row.startedAtUtc),
    },
    {
      key: 'durationMs',
      label: 'Duration',
      minWidth: '100px',
      render: (row) =>
        row.durationMs == null
          ? '-'
          : row.durationMs < 1000
            ? `${row.durationMs} ms`
            : `${(row.durationMs / 1000).toFixed(1)} s`,
    },
    {
      key: 'charged',
      label: 'Counted',
      minWidth: '90px',
      render: (row) =>
        row.charged ? (
          'Yes'
        ) : (
          <span className="text-muted-foreground">No (refunded)</span>
        ),
    },
    {
      key: 'error',
      label: 'Error',
      minWidth: '260px',
      render: (row) =>
        row.error || row.errorCode ? (
          <div className="text-xs">
            {row.errorCode ? (
              <p className="font-mono text-foreground">{row.errorCode}</p>
            ) : null}
            {row.error ? (
              <p className="whitespace-pre-wrap break-words text-destructive">
                {row.error}
              </p>
            ) : null}
          </div>
        ) : (
          <span className="text-muted-foreground">-</span>
        ),
    },
  ];

  const eventColumns: Array<DataGridColumn<BackgroundJobEvent>> = [
    {
      key: 'occurredAtUtc',
      label: 'Time',
      minWidth: '170px',
      render: (row) => formatLocalDateTime(row.occurredAtUtc),
    },
    {
      key: 'eventCode',
      label: 'Event',
      minWidth: '180px',
      render: (row) => (
        <span className="font-mono text-xs">{row.eventCode}</span>
      ),
    },
    {
      key: 'reason',
      label: 'Reason',
      minWidth: '200px',
      render: (row) =>
        row.reason ?? <span className="text-muted-foreground">-</span>,
    },
    {
      key: 'actorId',
      label: 'By',
      minWidth: '160px',
      render: (row) =>
        row.actorId ? (
          <span className="font-mono text-xs">{row.actorId}</span>
        ) : row.workerId ? (
          <span className="font-mono text-xs text-muted-foreground">
            {row.workerId}
          </span>
        ) : (
          <span className="text-muted-foreground">System</span>
        ),
    },
    {
      key: 'data',
      label: 'Data',
      minWidth: '220px',
      render: (row) =>
        row.data ? (
          <code
            className="line-clamp-3 break-all text-xs text-muted-foreground"
            title={row.data}
          >
            {row.data}
          </code>
        ) : (
          <span className="text-muted-foreground">-</span>
        ),
    },
  ];

  const sideEffectColumns: Array<DataGridColumn<BackgroundJobSideEffect>> = [
    { key: 'stepKey', label: 'Step', minWidth: '140px' },
    {
      key: 'status',
      label: 'Status',
      minWidth: '140px',
      render: (row) => (
        <Badge
          color={
            row.status === 'Completed'
              ? 'success'
              : row.status === 'Ambiguous'
                ? 'warning'
                : row.status === 'Abandoned'
                  ? 'error'
                  : 'info'
          }
        >
          {row.status === 'Ambiguous' ? 'Unknown outcome' : row.status}
        </Badge>
      ),
    },
    { key: 'issueCount', label: 'Issues', minWidth: '80px' },
    {
      key: 'externalRef',
      label: 'External reference',
      minWidth: '200px',
      copyable: true,
    },
    {
      key: 'startedAtUtc',
      label: 'Started',
      minWidth: '160px',
      render: (row) => formatLocalDateTime(row.startedAtUtc),
    },
    {
      key: 'completedAtUtc',
      label: 'Completed',
      minWidth: '160px',
      render: (row) => formatLocalDateTime(row.completedAtUtc),
    },
  ];

  const canRetry = Boolean(
    job && access.canManage && RETRYABLE_STATUSES.includes(job.status),
  );
  const canRequeue = Boolean(
    job && access.canManage && REQUEUEABLE_STATUSES.includes(job.status),
  );
  const canCancel = Boolean(
    job && access.canManage && CANCELLABLE_STATUSES.includes(job.status),
  );

  return (
    <div>
      {detailQuery.isError ? (
        <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {notFound
            ? 'This job was not found, or it belongs to a tenant you cannot access. Finished jobs are removed after their retention period.'
            : backgroundJobErrorMessage(
                detailQuery.error,
                'Failed to load the job. Please try refresh.',
              )}
        </div>
      ) : null}

      {detailQuery.isLoading ? (
        <p className="text-sm text-muted-foreground">Loading...</p>
      ) : null}

      {detail && job ? (
        <div className="grid gap-6">
          <Card>
            <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-3">
              <div className="flex min-w-0 flex-wrap items-center gap-3">
                <div className="min-w-0">
                  <p className="truncate font-mono text-sm font-medium text-foreground">
                    {job.jobType}
                  </p>
                  <p className="truncate font-mono text-xs text-muted-foreground">
                    {job.id}
                  </p>
                </div>
                <JobStatusBadge status={job.status} />
                {job.cancelRequestedAtUtc &&
                CANCELLABLE_STATUSES.includes(job.status) ? (
                  <span className="text-sm text-muted-foreground">
                    Cancel requested{' '}
                    {formatLocalDateTime(job.cancelRequestedAtUtc)}
                  </span>
                ) : null}
              </div>
              {access.canManage ? (
                <div className="flex flex-wrap gap-2">
                  {canRetry ? (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => setAction('retry')}
                    >
                      <RotateCcw className="h-4 w-4" /> Retry
                    </Button>
                  ) : null}
                  {canRequeue ? (
                    <Button
                      type="button"
                      variant="outline"
                      size="sm"
                      onClick={() => setAction('requeue')}
                    >
                      <ListRestart className="h-4 w-4" /> Requeue
                    </Button>
                  ) : null}
                  {canCancel ? (
                    <Button
                      type="button"
                      variant="destructive"
                      size="sm"
                      onClick={() => setAction('cancel')}
                    >
                      <Ban className="h-4 w-4" /> Cancel
                    </Button>
                  ) : null}
                </div>
              ) : null}
            </CardHeader>
            <CardContent className="grid gap-4">
              {canRetry || canRequeue ? <RetryVsRequeueNote /> : null}
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
                <Field label="Queue">{job.queue}</Field>
                <Field label="Attempts">
                  {job.attempt} of {job.maxAttempts}
                </Field>
                {isPlatform ? (
                  <Field label="Tenant">{tenantName(job.tenantId)}</Field>
                ) : null}
                <Field label="Source">{job.source}</Field>
                <Field label="Created">
                  {formatLocalDateTime(job.createdAtUtc)}
                </Field>
                <Field label="Run at">
                  {formatLocalDateTime(job.runAtUtc)}
                </Field>
                <Field label="Started">
                  {formatLocalDateTime(job.startedAtUtc)}
                </Field>
                <Field label="Finished">
                  {formatLocalDateTime(job.completedAtUtc)}
                </Field>
                {job.terminalReason ? (
                  <Field label="Finish reason">{job.terminalReason}</Field>
                ) : null}
                {job.progressTotal ? (
                  <Field label="Progress">
                    {job.progressProcessed ?? 0} of {job.progressTotal}
                  </Field>
                ) : null}
                {job.parentJobId ? (
                  <Field label="Parent job">
                    <button
                      type="button"
                      className="font-mono text-xs text-primary hover:underline"
                      onClick={() =>
                        router.push(`${basePath}/${job.parentJobId}`)
                      }
                    >
                      {job.parentJobId}
                    </button>
                  </Field>
                ) : null}
                {job.uniqueKey ? (
                  <Field label="Unique key">
                    <span className="font-mono text-xs">{job.uniqueKey}</span>
                  </Field>
                ) : null}
                {job.correlationId ? (
                  <Field label="Correlation id">
                    <span className="font-mono text-xs">
                      {job.correlationId}
                    </span>
                  </Field>
                ) : null}
                {job.actorId ? (
                  <Field label="Started by">
                    <span className="font-mono text-xs">{job.actorId}</span>
                  </Field>
                ) : null}
              </div>
            </CardContent>
          </Card>

          {job.lastError || job.lastErrorCode ? (
            <Card>
              <CardHeader>
                <CardTitle>Last error</CardTitle>
              </CardHeader>
              <CardContent>
                {job.lastErrorCode ? (
                  <p className="mb-2 font-mono text-sm text-foreground">
                    {job.lastErrorCode}
                  </p>
                ) : null}
                {job.lastError ? (
                  <pre className="max-h-80 overflow-auto whitespace-pre-wrap break-words rounded-lg bg-destructive/5 p-3 text-xs text-destructive">
                    {job.lastError}
                  </pre>
                ) : null}
              </CardContent>
            </Card>
          ) : null}

          <section className="grid gap-3">
            <h2 className="text-lg font-semibold text-foreground">Attempts</h2>
            <DataGrid<BackgroundJobAttempt>
              rows={detail.attempts}
              rowKey={(row) => row.attemptSeq}
              columns={attemptColumns}
              paginationMode="client"
              showPageSizeSelector={false}
              enableColumnReorder={false}
              noRecordsLabel="The job has not started an attempt yet."
            />
          </section>

          <section className="grid gap-3">
            <h2 className="text-lg font-semibold text-foreground">Events</h2>
            <DataGrid<BackgroundJobEvent>
              rows={detail.events}
              rowKey={(row) =>
                `${row.occurredAtUtc}-${row.eventCode}-${row.workerId ?? ''}-${row.actorId ?? ''}`
              }
              columns={eventColumns}
              paginationMode="client"
              showPageSizeSelector={false}
              enableColumnReorder={false}
              noRecordsLabel="No events recorded."
            />
          </section>

          {detail.sideEffects.length > 0 ? (
            <section className="grid gap-3">
              <div>
                <h2 className="text-lg font-semibold text-foreground">
                  External steps
                </h2>
                <p className="text-sm text-muted-foreground">
                  Calls to outside systems (for an email: the send).
                  &quot;Unknown outcome&quot; means the other system may or may
                  not have done it.
                </p>
              </div>
              <DataGrid<BackgroundJobSideEffect>
                rows={detail.sideEffects}
                rowKey={(row) => row.stepKey}
                columns={sideEffectColumns}
                paginationMode="client"
                showPageSizeSelector={false}
                showColumnOptions={false}
                enableColumnReorder={false}
              />
            </section>
          ) : null}

          {access.canViewPayload ? (
            <Card>
              <CardHeader>
                <CardTitle>Payload</CardTitle>
              </CardHeader>
              <CardContent>
                {detail.payload ? (
                  <>
                    <p className="mb-2 text-xs text-muted-foreground">
                      Sensitive fields are masked by the server.
                    </p>
                    <pre className="max-h-96 overflow-auto rounded-lg bg-muted p-3 text-xs text-foreground">
                      {prettyJson(detail.payload)}
                    </pre>
                  </>
                ) : (
                  <p className="text-sm text-muted-foreground">
                    This job type does not allow its payload to be shown.
                  </p>
                )}
              </CardContent>
            </Card>
          ) : null}
        </div>
      ) : null}

      {action && job ? (
        <JobActionDialog
          action={action}
          job={job}
          onClose={() => setAction(null)}
          onDone={() => {
            setAction(null);
            void queryClient.invalidateQueries({
              queryKey: backgroundJobKeys.all,
            });
          }}
        />
      ) : null}
    </div>
  );
}