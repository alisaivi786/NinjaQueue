'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Pause, Play, Zap } from 'lucide-react';

import { Badge, type BadgeColor } from '../ui/badge';
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
import { Label } from '../ui/label';
import { Textarea } from '../ui/textarea';
import { useToast } from '../ui/toast';
import { formatLocalDateTime } from '@shared';

import {
  backgroundJobErrorMessage,
  backgroundJobKeys,
  fetchRecurringSchedules,
  REASON_MAX_LENGTH,
  recurringScheduleState,
  recurringTriggerErrorMessage,
  runRecurringAction,
  triggerRecurringSchedule,
  type BackgroundJobAccess,
  type RecurringAction,
  type RecurringSchedule,
  type RecurringScheduleState,
} from './background-jobs-api';

const STATE_BADGE: Record<
  RecurringScheduleState,
  { color: BadgeColor; label: string; hint: string }
> = {
  active: {
    color: 'success',
    label: 'Active',
    hint: 'Runs on its schedule.',
  },
  paused: {
    color: 'warning',
    label: 'Paused',
    hint: 'Paused by an administrator. It stays paused across restarts and deploys until someone resumes it.',
  },
  disabled: {
    color: 'dark',
    label: 'Disabled',
    hint: 'Removed from the application code. It cannot be resumed here.',
  },
};

/** Pause / resume, or "Run now" (one off-schedule run, POST recurring/{code}/trigger). */
type RecurringDialogAction = RecurringAction | 'trigger';

/**
 * Recurring schedules (platform-wide System jobs). Platform scope (the Console) only. Pause / resume / run now take a
 * required reason and are audited (subject type RecurringSchedule).
 */
export function RecurringSchedulesTab({
  access,
  enabled,
  basePath,
}: {
  access: BackgroundJobAccess;
  enabled: boolean;
  /** The Background Jobs page; a job's detail page is `${basePath}/${jobId}`. */
  basePath: string;
}) {
  const queryClient = useQueryClient();
  const canManage = access.canManage;
  const [actionTarget, setActionTarget] = useState<{
    action: RecurringDialogAction;
    schedule: RecurringSchedule;
  } | null>(null);

  const listQuery = useQuery({
    queryKey: backgroundJobKeys.recurring,
    queryFn: fetchRecurringSchedules,
    enabled,
    retry: false,
  });

  const columns: Array<DataGridColumn<RecurringSchedule>> = [
    {
      key: 'code',
      label: 'Schedule',
      minWidth: '240px',
      required: true,
      render: (row) => (
        <div className="min-w-0">
          <p className="truncate text-sm text-foreground" title={row.code}>
            {row.code}
          </p>
          <p
            className="truncate font-mono text-xs text-muted-foreground"
            title={row.jobType}
          >
            {row.jobType}
          </p>
        </div>
      ),
    },
    {
      key: 'enabled',
      label: 'State',
      minWidth: '120px',
      render: (row) => {
        const state = STATE_BADGE[recurringScheduleState(row)];
        return (
          <span title={state.hint}>
            <Badge color={state.color}>{state.label}</Badge>
          </span>
        );
      },
    },
    {
      key: 'cron',
      label: 'Schedule (cron)',
      minWidth: '170px',
      render: (row) => (
        <div>
          <p className="font-mono text-xs text-foreground">{row.cron}</p>
          <p className="text-xs text-muted-foreground">{row.timeZone}</p>
        </div>
      ),
    },
    { key: 'queue', label: 'Queue', minWidth: '110px' },
    {
      key: 'nextRunAtUtc',
      label: 'Next run',
      minWidth: '160px',
      render: (row) =>
        recurringScheduleState(row) === 'active' ? (
          formatLocalDateTime(row.nextRunAtUtc)
        ) : (
          <span className="text-muted-foreground">-</span>
        ),
    },
    {
      key: 'lastRunAtUtc',
      label: 'Last run',
      minWidth: '160px',
      render: (row) => formatLocalDateTime(row.lastRunAtUtc),
    },
    {
      key: 'misfirePolicy',
      label: 'Missed runs',
      minWidth: '140px',
      render: (row) => (
        <div className="text-xs">
          <p>{row.misfirePolicy}</p>
          <p className="text-muted-foreground">
            {row.allowOverlap ? 'Overlap allowed' : 'No overlap'}
          </p>
        </div>
      ),
    },
    {
      key: 'auditSubjectId',
      label: 'Actions',
      minWidth: '280px',
      required: true,
      render: (row) => {
        const state = recurringScheduleState(row);
        return (
          <div className="flex items-center gap-1">
            {canManage && state === 'active' ? (
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() =>
                  setActionTarget({ action: 'pause', schedule: row })
                }
              >
                <Pause className="h-4 w-4" /> Pause
              </Button>
            ) : null}
            {canManage && state === 'paused' ? (
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() =>
                  setActionTarget({ action: 'resume', schedule: row })
                }
              >
                <Play className="h-4 w-4" /> Resume
              </Button>
            ) : null}
            {canManage && state === 'active' ? (
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() =>
                  setActionTarget({ action: 'trigger', schedule: row })
                }
              >
                <Zap className="h-4 w-4" /> Run now
              </Button>
            ) : null}
          </div>
        );
      },
    },
  ];

  return (
    <div className="grid gap-4">
      <p className="text-sm text-muted-foreground">
        Jobs 360PeopleForce starts on a timetable, for every tenant. Pausing
        stops new runs until an administrator resumes the schedule; a resumed
        schedule continues from now and does not replay the runs it missed. Run
        now starts one extra run straight away without changing the timetable.
      </p>

      {listQuery.isError ? (
        <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
          {backgroundJobErrorMessage(
            listQuery.error,
            'Failed to load the recurring schedules. Please try refresh.',
          )}
        </div>
      ) : null}

      <DataGrid<RecurringSchedule>
        rows={listQuery.data ?? []}
        isLoading={listQuery.isLoading || listQuery.isFetching}
        rowKey={(row) => row.code}
        columns={columns}
        paginationMode="client"
        sortMode="client"
        enableColumnReorder={false}
        noRecordsLabel="No recurring schedules are registered."
        showRefresh
        onRefresh={async () => {
          await queryClient.refetchQueries({
            queryKey: backgroundJobKeys.recurring,
          });
        }}
      />

      {actionTarget ? (
        <RecurringActionDialog
          action={actionTarget.action}
          schedule={actionTarget.schedule}
          basePath={basePath}
          onClose={() => setActionTarget(null)}
          onDone={() => {
            setActionTarget(null);
            void queryClient.invalidateQueries({
              queryKey: backgroundJobKeys.recurring,
            });
          }}
        />
      ) : null}

    </div>
  );
}

const DIALOG_COPY: Record<
  RecurringDialogAction,
  { title: string; description: string; confirm: string }
> = {
  pause: {
    title: 'Pause schedule',
    description:
      'No new runs start until an administrator resumes it. The pause survives restarts and deploys. A run already in progress is not stopped.',
    confirm: 'Pause',
  },
  resume: {
    title: 'Resume schedule',
    description:
      'The schedule continues from now: the next run is worked out from the current time, and runs missed while paused are not replayed.',
    confirm: 'Resume',
  },
  trigger: {
    title: 'Run schedule now',
    description:
      'Starts one run of this job straight away, for every tenant, on top of its regular runs. The timetable does not change. If a run is still going and the schedule does not allow overlapping runs, nothing is started.',
    confirm: 'Run now',
  },
};

function RecurringActionDialog({
  action,
  schedule,
  basePath,
  onClose,
  onDone,
}: {
  action: RecurringDialogAction;
  schedule: RecurringSchedule;
  basePath: string;
  onClose: () => void;
  onDone: () => void;
}) {
  const { toast } = useToast();
  const router = useRouter();
  const [reason, setReason] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const trimmed = reason.trim();
  const copy = DIALOG_COPY[action];

  const mutation = useMutation({
    mutationFn: async () => {
      if (action === 'trigger') {
        const result = await triggerRecurringSchedule(schedule.code, trimmed);
        return { updated: result?.schedule, jobId: result?.jobId ?? null };
      }
      const updated = await runRecurringAction(schedule.code, action, trimmed);
      return { updated, jobId: null };
    },
    onSuccess: ({ updated, jobId }) => {
      if (action === 'trigger') {
        toast({
          variant: 'success',
          title: 'Run started',
          description: schedule.code,
          duration: 10_000,
          action: jobId
            ? {
                label: 'Open the job',
                onClick: () => router.push(`${basePath}/${jobId}`),
              }
            : undefined,
        });
      } else {
        toast({
          variant: 'success',
          title: action === 'pause' ? 'Schedule paused' : 'Schedule resumed',
          description:
            action === 'resume' && updated
              ? `Next run ${formatLocalDateTime(updated.nextRunAtUtc)}.`
              : schedule.code,
        });
      }
      onDone();
    },
    onError: (err) =>
      setError(
        action === 'trigger'
          ? recurringTriggerErrorMessage(err)
          : backgroundJobErrorMessage(err),
      ),
  });

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{copy.title}</DialogTitle>
          <DialogDescription className="text-muted-foreground">
            {copy.description}
          </DialogDescription>
        </DialogHeader>
        <div className="rounded-lg border border-border px-3 py-2 text-xs">
          <p className="text-foreground">{schedule.code}</p>
          <p className="font-mono text-muted-foreground">
            {schedule.cron} ({schedule.timeZone})
          </p>
        </div>
        <div className="grid gap-1.5">
          <Label htmlFor="recurring-action-reason">Reason (required)</Label>
          <Textarea
            id="recurring-action-reason"
            rows={3}
            maxLength={REASON_MAX_LENGTH}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            placeholder="Why are you doing this? Recorded in the audit log."
          />
        </div>
        <label className="flex items-start gap-2 text-sm">
          <Checkbox
            checked={confirmed}
            onCheckedChange={(value) => setConfirmed(value === true)}
            className="mt-0.5"
          />
          <span>
            I understand this affects every tenant and is recorded in the audit
            log.
          </span>
        </label>
        {error ? (
          <div className="rounded-xl border border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive">
            {error}
          </div>
        ) : null}
        <DialogFooter className="gap-2">
          <Button type="button" variant="outline" onClick={onClose}>
            Close
          </Button>
          <Button
            type="button"
            variant={action === 'pause' ? 'destructive' : 'default'}
            disabled={!trimmed || !confirmed || mutation.isPending}
            onClick={() => {
              setError(null);
              mutation.mutate();
            }}
          >
            {mutation.isPending ? 'Working...' : copy.confirm}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}