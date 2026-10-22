'use client';

import { useQuery, useQueryClient } from '@tanstack/react-query';

import { Badge } from '../ui/badge';
import { DataGrid, type DataGridColumn } from '../ui/data-grid';
import { formatLocalDateTime } from '@shared';

import {
  backgroundJobErrorMessage,
  backgroundJobKeys,
  fetchBackgroundJobStats,
  fetchBackgroundJobWorkers,
  formatJobDuration,
  type BackgroundJobQueueStats,
  type BackgroundJobWorker,
} from './background-jobs-api';

/** A worker that has not sent a heartbeat for this long is shown as stale. */
const STALE_HEARTBEAT_MS = 2 * 60 * 1000;

/** Platform-wide queue depth (never one tenant's) and the registered workers. Platform scope (the Console) only. */
export function QueuesWorkersTab({ enabled }: { enabled: boolean }) {
  const queryClient = useQueryClient();

  const statsQuery = useQuery({
    queryKey: backgroundJobKeys.stats(24),
    queryFn: () => fetchBackgroundJobStats(24),
    enabled,
    retry: false,
  });
  const workersQuery = useQuery({
    queryKey: backgroundJobKeys.workers,
    queryFn: fetchBackgroundJobWorkers,
    enabled,
    retry: false,
    refetchInterval: 30_000,
  });

  const queueColumns: Array<DataGridColumn<BackgroundJobQueueStats>> = [
    { key: 'queue', label: 'Queue', minWidth: '160px' },
    {
      key: 'depth',
      label: 'Ready',
      minWidth: '90px',
      render: (row) => row.enqueued.toLocaleString(),
    },
    {
      key: 'scheduled',
      label: 'Scheduled',
      minWidth: '100px',
      render: (row) => row.scheduled.toLocaleString(),
    },
    {
      key: 'awaiting',
      label: 'Awaiting',
      minWidth: '100px',
      render: (row) => row.awaiting.toLocaleString(),
    },
    {
      key: 'processing',
      label: 'Processing',
      minWidth: '100px',
      render: (row) => row.processing.toLocaleString(),
    },
    {
      key: 'oldestPendingAgeSeconds',
      label: 'Oldest waiting',
      minWidth: '140px',
      render: (row) => (
        <span
          className={
            row.oldestPendingAgeSeconds > 600 ? 'text-destructive' : undefined
          }
        >
          {formatJobDuration(row.oldestPendingAgeSeconds)}
        </span>
      ),
    },
  ];

  const workerColumns: Array<DataGridColumn<BackgroundJobWorker>> = [
    {
      key: 'workerId',
      label: 'Worker',
      minWidth: '220px',
      render: (row) => (
        <div>
          <p className="font-mono text-xs text-foreground">{row.workerId}</p>
          <p className="text-xs text-muted-foreground">
            {row.host} · PID {row.processId}
          </p>
        </div>
      ),
    },
    { key: 'queues', label: 'Queues', minWidth: '180px' },
    { key: 'appVersion', label: 'Version', minWidth: '100px' },
    {
      key: 'startedAtUtc',
      label: 'Started',
      minWidth: '160px',
      render: (row) => formatLocalDateTime(row.startedAtUtc),
    },
    {
      key: 'lastHeartbeatAtUtc',
      label: 'Last heartbeat',
      minWidth: '200px',
      render: (row) => {
        const stale =
          Date.now() - new Date(row.lastHeartbeatAtUtc).getTime() >
          STALE_HEARTBEAT_MS;
        return (
          <div className="flex items-center gap-2">
            <span>{formatLocalDateTime(row.lastHeartbeatAtUtc)}</span>
            <Badge color={stale ? 'warning' : 'success'}>
              {stale ? 'Stale' : 'Alive'}
            </Badge>
          </div>
        );
      },
    },
  ];

  return (
    <div className="grid gap-6">
      <section className="grid gap-3">
        <div>
          <h2 className="text-lg font-semibold text-foreground">Queues</h2>
          <p className="text-sm text-muted-foreground">
            Jobs ready to run per queue, and how long the oldest one has waited.
            Dead-lettered jobs:{' '}
            <span className="font-medium text-foreground">
              {statsQuery.data?.deadLetteredCount.toLocaleString() ?? '-'}
            </span>
          </p>
        </div>
        {statsQuery.isError ? (
          <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {backgroundJobErrorMessage(
              statsQuery.error,
              'Failed to load the queue statistics.',
            )}
          </div>
        ) : null}
        <DataGrid<BackgroundJobQueueStats>
          rows={statsQuery.data?.queues ?? []}
          isLoading={statsQuery.isLoading || statsQuery.isFetching}
          rowKey={(row) => row.queue}
          columns={queueColumns}
          paginationMode="client"
          sortMode="client"
          showPageSizeSelector={false}
          enableColumnReorder={false}
          noRecordsLabel="No queues have ready jobs."
          showRefresh
          onRefresh={async () => {
            await queryClient.refetchQueries({
              queryKey: backgroundJobKeys.stats(24),
            });
          }}
        />
      </section>

      <section className="grid gap-3">
        <div>
          <h2 className="text-lg font-semibold text-foreground">Workers</h2>
          <p className="text-sm text-muted-foreground">
            Processes running jobs, most recent heartbeat first. Refreshes every
            30 seconds.
          </p>
        </div>
        {workersQuery.isError ? (
          <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {backgroundJobErrorMessage(
              workersQuery.error,
              'Failed to load the workers.',
            )}
          </div>
        ) : null}
        <DataGrid<BackgroundJobWorker>
          rows={workersQuery.data ?? []}
          isLoading={workersQuery.isLoading}
          rowKey={(row) => row.workerId}
          columns={workerColumns}
          paginationMode="client"
          sortMode="client"
          showPageSizeSelector={false}
          enableColumnReorder={false}
          noRecordsLabel="No workers are registered."
          showRefresh
          onRefresh={async () => {
            await queryClient.refetchQueries({
              queryKey: backgroundJobKeys.workers,
            });
          }}
        />
      </section>
    </div>
  );
}