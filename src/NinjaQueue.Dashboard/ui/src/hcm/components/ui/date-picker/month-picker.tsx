'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { CalendarDays } from 'lucide-react';

import { cn } from '../../../utils';
import { getFloatingPortalTarget, useFloatingPosition } from '../../../hooks/use-floating-position';
import { MonthYearPicker } from './calendar';
import { MONTH_LABELS } from './date-utils';

export type MonthPickerProps = {
  id?: string;
  label?: string;
  /** 'YYYY-MM'. */
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  className?: string;
  'aria-label'?: string;
};

const parseMonth = (value: string) => {
  const [year, month] = value.split('-').map(Number);
  return year && month ? new Date(year, month - 1, 1) : new Date();
};

const toMonth = (date: Date) => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;

/** A month field - DatePicker's trigger and popover with the calendar's month grid, for "yyyy-MM" values. */
export function MonthPicker({ id, label, value, onChange, disabled = false, className, 'aria-label': ariaLabel }: MonthPickerProps) {
  const generatedId = useId();
  const inputId = id ?? generatedId;
  const rootRef = useRef<HTMLDivElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const [isOpen, setIsOpen] = useState(false);
  // ≈ the month grid's popover height (year row + 4 rows of months + padding)
  const position = useFloatingPosition(rootRef, isOpen, 190);
  const selected = parseMonth(value);

  useEffect(() => {
    if (!isOpen) return;

    // The popover is portaled out of rootRef, so a click inside it is not "outside".
    const handleOutsideClick = (event: MouseEvent) => {
      if (!(event.target instanceof Node)) return;
      if (rootRef.current?.contains(event.target) || menuRef.current?.contains(event.target)) return;
      setIsOpen(false);
    };
    const handleEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setIsOpen(false);
    };

    document.addEventListener('mousedown', handleOutsideClick);
    document.addEventListener('keydown', handleEscape);
    return () => {
      document.removeEventListener('mousedown', handleOutsideClick);
      document.removeEventListener('keydown', handleEscape);
    };
  }, [isOpen]);

  return (
    <div ref={rootRef} className={cn('relative', className)}>
      {label ? (
        <label htmlFor={inputId} className="mb-1.5 block text-[13px] font-medium text-foreground">
          {label}
        </label>
      ) : null}

      <button
        id={inputId}
        type="button"
        disabled={disabled}
        aria-haspopup="dialog"
        aria-expanded={isOpen}
        aria-label={ariaLabel ?? label}
        onClick={() => setIsOpen((current) => !current)}
        className="border-input dark:bg-input/30 flex h-9 w-full min-w-0 items-center justify-between gap-2 rounded-md border bg-input-background px-3 py-1 text-left text-base outline-none transition-[color,box-shadow] focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 md:text-sm"
      >
        <span className="truncate">
          {MONTH_LABELS[selected.getMonth()]} {selected.getFullYear()}
        </span>
        <CalendarDays className="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      </button>

      {isOpen && position
        ? createPortal(
            <div
              ref={menuRef}
              data-floating-popover
              // pointer-events: auto - see DatePicker (a modal Dialog disables pointer events on document.body).
              style={{ position: 'fixed', top: position.top ?? undefined, bottom: position.bottom ?? undefined, left: position.left, pointerEvents: 'auto' }}
              className="z-[130]"
            >
              <div className="w-[240px] select-none overflow-hidden rounded-md border border-border bg-popover p-3 text-popover-foreground shadow-md">
                <MonthYearPicker
                  month={selected}
                  onChange={(date) => {
                    onChange(toMonth(date));
                    setIsOpen(false);
                  }}
                />
              </div>
            </div>,
            getFloatingPortalTarget(rootRef.current),
          )
        : null}
    </div>
  );
}