'use client';

import { useState } from 'react';
import { Check, Copy } from 'lucide-react';

import { Badge, type BadgeColor } from '../ui/badge';

import { jobStatusLabel } from './background-jobs-api';

const JOB_STATUS_COLOR: Record<string, BadgeColor> = {
  Enqueued: 'info',
  Scheduled: 'light',
  Awaiting: 'light',
  Processing: 'primary',
  Succeeded: 'success',
  Cancelled: 'dark',
  DeadLettered: 'error',
};

export function JobStatusBadge({ status }: { status: string }) {
  return (
    <Badge color={JOB_STATUS_COLOR[status] ?? 'light'}>
      {jobStatusLabel(status)}
    </Badge>
  );
}

const EMAIL_STATUS_COLOR: Record<string, BadgeColor> = {
  Pending: 'light',
  Sending: 'info',
  Sent: 'success',
  Failed: 'error',
  Unknown: 'warning',
  Suppressed: 'dark',
};

export function EmailDeliveryStatusBadge({ status }: { status: string }) {
  return <Badge color={EMAIL_STATUS_COLOR[status] ?? 'light'}>{status}</Badge>;
}

const BULK_OUTCOME_COLOR: Record<string, BadgeColor> = {
  Applied: 'success',
  NotEligible: 'warning',
  Conflict: 'warning',
  NotFound: 'light',
  Failed: 'error',
};

export function BulkOutcomeBadge({ outcome }: { outcome: string }) {
  return (
    <Badge color={BULK_OUTCOME_COLOR[outcome] ?? 'light'}>{outcome}</Badge>
  );
}

/** A job's / delivery's tenant (platform scope): the resolved name, the full TenantId on hover and a copy button. */
export function JobTenantCell({
  tenantId,
  tenantName,
}: {
  tenantId: string | null;
  tenantName: (id: string | null) => string;
}) {
  const [copied, setCopied] = useState(false);
  const label = tenantName(tenantId);
  if (!tenantId) {
    return <span className="text-muted-foreground">{label}</span>;
  }
  const copy = () => {
    void navigator.clipboard?.writeText(tenantId).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };
  return (
    <span className="inline-flex min-w-0 items-center gap-1.5">
      <span
        className={
          // An id shown as the label (in full or shortened) reads better in mono.
          tenantId.startsWith(label.replace(/…$/, ''))
            ? 'truncate font-mono text-xs'
            : 'truncate'
        }
        title={tenantId}
      >
        {label}
      </span>
      <button
        type="button"
        onClick={copy}
        title={copied ? 'Copied!' : 'Copy TenantId'}
        aria-label="Copy TenantId"
        className="inline-flex shrink-0 items-center text-muted-foreground hover:text-foreground"
      >
        {copied ? (
          <Check className="h-3.5 w-3.5" />
        ) : (
          <Copy className="h-3.5 w-3.5" />
        )}
      </button>
    </span>
  );
}