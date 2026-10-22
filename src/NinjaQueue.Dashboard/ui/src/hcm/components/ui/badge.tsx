import * as React from 'react';

import { cn } from '../../utils';

// Ports ninja-web's Badge (libs/ninja-web/src/components/ui/badge/Badge.tsx)
// styling/color system wholesale — same variant/color/size matrix, same hex
// values — but with the colors inlined instead of referencing ninja-theme's
// --color-brand-*/--color-success-*/etc. CSS custom properties, so this
// component renders correctly without libs/ninja-theme loaded (opsfusion-web
// only depends on opsfusion-theme). Values below are copied from
// libs/ninja-theme/src/styles.css's default (non-palette-overridden) hex
// values for brand/success/error/warning/blue-light/gray.
export type BadgeVariant = 'light' | 'solid';
export type BadgeSize = 'sm' | 'md';
export type BadgeColor = 'primary' | 'success' | 'error' | 'warning' | 'info' | 'light' | 'dark';

const SIZE_CLASSES: Record<BadgeSize, string> = {
  sm: 'text-[12px] leading-[18px]',
  md: 'text-sm',
};

const VARIANT_CLASSES: Record<BadgeVariant, Record<BadgeColor, string>> = {
  light: {
    primary: 'bg-[#ecf3ff] text-[#465fff] dark:bg-[#465fff]/15 dark:text-[#7592ff]',
    success: 'bg-[#ecfdf3] text-[#039855] dark:bg-[#12b76a]/15 dark:text-[#12b76a]',
    error: 'bg-[#fef3f2] text-[#d92d20] dark:bg-[#f04438]/15 dark:text-[#f04438]',
    warning: 'bg-[#fffaeb] text-[#dc6803] dark:bg-[#f79009]/15 dark:text-[#fd853a]',
    info: 'bg-[#f0f9ff] text-[#0ba5ec] dark:bg-[#0ba5ec]/15 dark:text-[#0ba5ec]',
    light: 'bg-[#f2f4f7] text-[#344054] dark:bg-white/5 dark:text-white/80',
    dark: 'bg-[#667085] text-white dark:bg-white/5 dark:text-white',
  },
  solid: {
    primary: 'bg-[#465fff] text-white',
    success: 'bg-[#12b76a] text-white',
    error: 'bg-[#f04438] text-white',
    warning: 'bg-[#f79009] text-white',
    info: 'bg-[#0ba5ec] text-white',
    light: 'bg-[#98a2b3] text-white dark:bg-white/5 dark:text-white/80',
    dark: 'bg-[#344054] text-white',
  },
};

export type BadgeProps = Omit<React.ComponentProps<'span'>, 'color'> & {
  variant?: BadgeVariant;
  color?: BadgeColor;
  size?: BadgeSize;
  startIcon?: React.ReactNode;
  endIcon?: React.ReactNode;
};

function Badge({
  variant = 'light',
  color = 'primary',
  size = 'md',
  startIcon,
  endIcon,
  className,
  children,
  ...props
}: BadgeProps) {
  return (
    <span
      data-slot="badge"
      className={cn(
        'inline-flex w-fit shrink-0 items-center justify-center gap-1 rounded-full px-2.5 py-0.5 font-medium whitespace-nowrap',
        SIZE_CLASSES[size],
        VARIANT_CLASSES[variant][color],
        className,
      )}
      {...props}
    >
      {startIcon ? <span className="shrink-0">{startIcon}</span> : null}
      {children}
      {endIcon ? <span className="shrink-0">{endIcon}</span> : null}
    </span>
  );
}

export { Badge };