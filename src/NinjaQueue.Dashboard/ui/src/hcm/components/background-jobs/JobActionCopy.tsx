'use client';

/** Plan §15.11 (G6): Retry and Requeue are deliberately different - this copy is shared by the single and bulk dialogs. */
export const JOB_ACTION_COPY = {
  cancel: {
    title: 'Cancel job',
    bulkTitle: 'Cancel jobs',
    confirmLabel: 'Cancel job',
    bulkConfirmLabel: 'Cancel jobs',
    summary:
      'A queued, scheduled or waiting job is cancelled at once. A running job is asked to stop: it stops at its next checkpoint, or after its current attempt if it cannot be interrupted. A finished job cannot be cancelled.',
  },
  retry: {
    title: 'Retry job',
    bulkTitle: 'Retry jobs',
    confirmLabel: 'Retry now',
    bulkConfirmLabel: 'Retry now',
    summary:
      'Retry gives a dead-lettered job exactly one more attempt, now. It keeps the same job, payload and attempt history; the attempt limit becomes "attempts so far + 1". Only dead-lettered jobs can be retried.',
  },
  requeue: {
    title: 'Requeue job',
    bulkTitle: 'Requeue jobs',
    confirmLabel: 'Requeue',
    bulkConfirmLabel: 'Requeue',
    summary:
      "Requeue gives a dead-lettered or cancelled job a fresh attempt budget (the job type's default number of attempts on top of those already used), now or at a time you choose. It keeps the same job, payload and history, and clears any cancel request.",
  },
} as const;

export function RetryVsRequeueNote() {
  return (
    <div className="rounded-lg border border-border bg-muted/40 px-3 py-2 text-xs text-muted-foreground">
      <p>
        <span className="font-medium text-foreground">Retry</span> = one more
        attempt now (dead-lettered only).{' '}
        <span className="font-medium text-foreground">Requeue</span> = a fresh
        set of attempts, now or later (dead-lettered or cancelled).
      </p>
      <p className="mt-1">
        Neither changes the payload, and a succeeded job is never re-run. To fix
        a bad payload, cancel the job and start a new one.
      </p>
    </div>
  );
}