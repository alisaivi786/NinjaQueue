import * as React from 'react';

import { cn } from '../../utils';

export type KpiTileTone = 'default' | 'danger' | 'warning' | 'success';

const TONE_CLASSES: Record<KpiTileTone, string> = {
  default: 'text-foreground',
  danger: 'text-[#d92d20] dark:text-[#f97066]',
  warning: 'text-[#dc6803] dark:text-[#fd853a]',
  success: 'text-[#039855] dark:text-[#32d583]',
};

/** One headline number with its label - for summary strips above a record or a list. */
export function KpiTile({
  label,
  value,
  hint,
  tone = 'default',
  className,
}: {
  label: string;
  value: React.ReactNode;
  hint?: React.ReactNode;
  tone?: KpiTileTone;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'min-w-0 rounded-xl border border-border bg-card px-3 py-3 sm:px-4',
        className,
      )}
    >
      <p className="truncate text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
        {label}
      </p>
      <p
        className={cn(
          'mt-1 truncate text-lg font-semibold tabular-nums sm:text-xl',
          TONE_CLASSES[tone],
        )}
      >
        {value}
      </p>
      {hint ? (
        <p className="truncate text-[11px] text-muted-foreground">{hint}</p>
      ) : null}
    </div>
  );
}