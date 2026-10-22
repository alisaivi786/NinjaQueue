'use client';

import { cn } from '../../../utils';
import { Tooltip, TooltipContent, TooltipTrigger } from '../tooltip';

export type DataGridRefreshButtonProps = {
  onClick: () => unknown | Promise<unknown>;
  isLoading?: boolean;
  label: string;
  className?: string;
  /** `plain`: borderless, subtle hover. `bordered`: matches the boxed toolbar icon buttons (Export, Column Options). */
  variant?: 'plain' | 'bordered';
};

/**
 * Re-themed port of ninja-web's `RefreshIconButton`, wired to OpsFusion's
 * own Radix-based `Tooltip` (opsfusion-web already ships one, so this
 * doesn't need ninja-web's standalone portal-based `Tooltip`) instead of
 * ninja-theme's `gray-*`/`brand-*` classes.
 */
export function DataGridRefreshButton({
  onClick,
  isLoading = false,
  label,
  className = '',
  variant = 'plain',
}: DataGridRefreshButtonProps) {
  const variantClassName =
    variant === 'bordered'
      ? 'h-11 w-11 rounded-lg border border-border bg-transparent text-muted-foreground hover:bg-accent hover:text-accent-foreground'
      : 'h-8 w-8 rounded-md text-muted-foreground hover:bg-accent hover:text-accent-foreground';

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          onClick={onClick}
          className={cn(
            'inline-flex items-center justify-center transition',
            variantClassName,
            className,
          )}
          aria-label={label}
          disabled={isLoading}
        >
          <svg
            className={cn('size-5', isLoading ? 'animate-spin text-primary' : '')}
            width="20"
            height="20"
            viewBox="0 0 24 24"
            fill="none"
            aria-hidden="true"
          >
            <path
              d="M20 12a8 8 0 1 1-2.34-5.66"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
            <path
              d="M20 4v4h-4"
              stroke="currentColor"
              strokeWidth="1.8"
              strokeLinecap="round"
              strokeLinejoin="round"
            />
          </svg>
        </button>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}