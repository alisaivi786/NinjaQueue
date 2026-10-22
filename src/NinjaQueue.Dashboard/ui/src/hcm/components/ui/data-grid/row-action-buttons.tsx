'use client';

import type { ButtonHTMLAttributes } from 'react';
import { History, Settings } from 'lucide-react';
import { EyeIcon, PencilIcon, TrashBinIcon } from '@ninja-icon';

import { cn } from '../../../utils';

// Small icon-only row action buttons for DataGrid rows. All three share the
// same prop shape as ninja-web's TrashActionButton for consistency.
export type RowActionButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  label: string;
};

export function TrashActionButton({ label, className, ...buttonProps }: RowActionButtonProps) {
  return (
    <button
      {...buttonProps}
      type="button"
      aria-label={label}
      className={cn(
        'inline-flex items-center justify-center text-muted-foreground transition-colors hover:text-destructive disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
    >
      <TrashBinIcon />
    </button>
  );
}

export function PencilActionButton({ label, className, ...buttonProps }: RowActionButtonProps) {
  return (
    <button
      {...buttonProps}
      type="button"
      aria-label={label}
      className={cn(
        'inline-flex items-center justify-center text-muted-foreground transition-colors hover:text-primary disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
    >
      <PencilIcon />
    </button>
  );
}

export function EyeActionButton({ label, className, ...buttonProps }: RowActionButtonProps) {
  return (
    <button
      {...buttonProps}
      type="button"
      aria-label={label}
      className={cn(
        'inline-flex items-center justify-center text-muted-foreground transition-colors hover:text-primary disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
    >
      <EyeIcon />
    </button>
  );
}

// No exact ninja-icon glyph fits "history"/"audit log" — using lucide-react's
// `History`, which is already a dependency used elsewhere in this repo (e.g.
// libs/opsfusion-shell/src/components/UserMenu.tsx). Caller supplies onClick;
// this button has no audit-log behavior baked in.
export function HistoryActionButton({ label, className, ...buttonProps }: RowActionButtonProps) {
  return (
    <button
      {...buttonProps}
      type="button"
      aria-label={label}
      className={cn(
        'inline-flex items-center justify-center text-muted-foreground transition-colors hover:text-primary disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
    >
      <History className="h-4 w-4" />
    </button>
  );
}

// No exact ninja-icon glyph fits "settings"/"configure" either — same
// lucide-react fallback as HistoryActionButton above.
export function SettingsActionButton({ label, className, ...buttonProps }: RowActionButtonProps) {
  return (
    <button
      {...buttonProps}
      type="button"
      aria-label={label}
      className={cn(
        'inline-flex items-center justify-center text-muted-foreground transition-colors hover:text-primary disabled:cursor-not-allowed disabled:opacity-50',
        className,
      )}
    >
      <Settings className="h-4 w-4" />
    </button>
  );
}