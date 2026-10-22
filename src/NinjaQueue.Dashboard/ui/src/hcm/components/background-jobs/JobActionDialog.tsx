'use client';

import { useState } from 'react';
import { useMutation } from '@tanstack/react-query';

import { Button } from '../ui/button';
import { Checkbox } from '../ui/checkbox';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '../ui/dialog';
import { Input } from '../ui/input';
import { Label } from '../ui/label';
import { Textarea } from '../ui/textarea';
import { useToast } from '../ui/toast';

import {
  backgroundJobErrorMessage,
  cancelBackgroundJob,
  REASON_MAX_LENGTH,
  requeueBackgroundJob,
  retryBackgroundJob,
  type BackgroundJobSummary,
} from './background-jobs-api';
import { JOB_ACTION_COPY } from './JobActionCopy';

export type JobAction = 'cancel' | 'retry' | 'requeue';

const CANCEL_RESULT_TEXT: Record<string, string> = {
  Cancelled: 'The job was cancelled.',
  CancelRequested:
    'The running job was asked to stop. It stops at its next checkpoint.',
  CancelAfterCurrentAttempt:
    'The running job cannot be interrupted. It is cancelled after its current attempt.',
  AlreadyRequested: 'A cancel had already been requested for this job.',
};

const SUCCESS_TITLE: Record<JobAction, string> = {
  cancel: 'Cancel recorded',
  retry: 'Job retried',
  requeue: 'Job requeued',
};

/** Reason prompt + confirmation for one job. The reason is required by the server and written to the audit log. */
export function JobActionDialog({
  action,
  job,
  onClose,
  onDone,
}: {
  action: JobAction;
  job: BackgroundJobSummary;
  onClose: () => void;
  onDone: () => void;
}) {
  const { toast } = useToast();
  const copy = JOB_ACTION_COPY[action];
  const [reason, setReason] = useState('');
  const [includeContinuations, setIncludeContinuations] = useState(true);
  const [runAtLocal, setRunAtLocal] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const trimmedReason = reason.trim();
  const runAtUtc =
    action === 'requeue' && runAtLocal
      ? new Date(runAtLocal).toISOString()
      : null;

  const mutation = useMutation({
    mutationFn: async () => {
      if (action === 'cancel') {
        const result = await cancelBackgroundJob(job.id, trimmedReason);
        return (
          CANCEL_RESULT_TEXT[result?.result ?? ''] ?? 'The cancel was recorded.'
        );
      }
      const result =
        action === 'retry'
          ? await retryBackgroundJob(
              job.id,
              trimmedReason,
              includeContinuations,
            )
          : await requeueBackgroundJob(
              job.id,
              trimmedReason,
              includeContinuations,
              runAtUtc,
            );
      const restored = result?.restoredContinuations ?? 0;
      const conflicts = result?.continuationConflicts ?? 0;
      const base =
        action === 'retry'
          ? 'The job will run again now.'
          : runAtUtc
            ? 'The job is scheduled to run again.'
            : 'The job will run again now.';
      const extra = [
        restored > 0
          ? `${restored} follow-up job${restored === 1 ? '' : 's'} restored.`
          : '',
        conflicts > 0
          ? `${conflicts} follow-up job${conflicts === 1 ? '' : 's'} could not be restored (unique key held).`
          : '',
      ]
        .filter(Boolean)
        .join(' ');
      return extra ? `${base} ${extra}` : base;
    },
    onSuccess: (message) => {
      toast({
        variant: 'success',
        title: SUCCESS_TITLE[action],
        description: message,
      });
      onDone();
    },
    onError: (err) => setError(backgroundJobErrorMessage(err)),
  });

  const canSubmit =
    trimmedReason.length > 0 && confirmed && !mutation.isPending;

  return (
    <Dialog open onOpenChange={(open) => (open ? undefined : onClose())}>
      <DialogContent className="max-w-lg">
        <DialogHeader>
          <DialogTitle>{copy.title}</DialogTitle>
          <DialogDescription className="text-muted-foreground">
            {copy.summary}
          </DialogDescription>
        </DialogHeader>

        <div className="rounded-lg border border-border px-3 py-2 text-xs">
          <p className="font-mono text-foreground">{job.jobType}</p>
          <p className="text-muted-foreground">
            {job.id} · attempt {job.attempt} of {job.maxAttempts}
          </p>
        </div>

        {action === 'cancel' && job.status === 'Processing' ? (
          <p className="text-xs text-muted-foreground">
            This job is running now. If running-job cancel is turned off on the
            server, the request is refused - wait for the attempt to end, then
            cancel.
          </p>
        ) : null}

        <div className="grid gap-1.5">
          <Label htmlFor="job-action-reason">Reason (required)</Label>
          <Textarea
            id="job-action-reason"
            rows={3}
            maxLength={REASON_MAX_LENGTH}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
            placeholder="Why are you doing this? Recorded in the audit log."
          />
        </div>

        {action === 'requeue' ? (
          <div className="grid gap-1.5">
            <Label htmlFor="job-action-run-at">Run at (optional)</Label>
            <Input
              id="job-action-run-at"
              type="datetime-local"
              value={runAtLocal}
              onChange={(event) => setRunAtLocal(event.target.value)}
            />
            <p className="text-xs text-muted-foreground">
              Leave empty to run now. Your local time.
            </p>
          </div>
        ) : null}

        {action !== 'cancel' ? (
          <label className="flex items-start gap-2 text-sm">
            <Checkbox
              checked={includeContinuations}
              onCheckedChange={(value) =>
                setIncludeContinuations(value === true)
              }
              className="mt-0.5"
            />
            <span>
              Also restore follow-up jobs that were stopped because this job
              failed or was cancelled
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
            I understand this action is applied now and recorded in the audit
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
            variant={action === 'cancel' ? 'destructive' : 'default'}
            disabled={!canSubmit}
            onClick={() => {
              setError(null);
              mutation.mutate();
            }}
          >
            {mutation.isPending ? 'Working...' : copy.confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}