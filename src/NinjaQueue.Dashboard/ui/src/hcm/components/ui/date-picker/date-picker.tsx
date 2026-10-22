'use client';

import { useEffect, useId, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { CalendarDays, X } from 'lucide-react';

import { cn } from '../../../utils';
import { getFloatingPortalTarget, useFloatingPosition } from '../../../hooks/use-floating-position';
import { Calendar } from './calendar';
import { formatDisplayDate, isBeforeDay, parseISODate, toISODate } from './date-utils';

export type DatePickerProps = {
  id?: string;
  label?: string;
  placeholder?: string;
  /** ISO 'YYYY-MM-DD', or '' / null / undefined for no date. */
  value?: string | null;
  onChange: (value: string) => void;
  onClear?: () => void;
  minDate?: string;
  maxDate?: string;
  /** Blocks selecting any date after today. Defaults to true — pass `false` to allow future dates. */
  disableFutureDates?: boolean;
  required?: boolean;
  disabled?: boolean;
  error?: boolean;
  hint?: string;
  className?: string;
  'aria-label'?: string;
};

/**
 * Themed, standalone replacement for a bare `<input type="date">` — popover-calendar UX ninja-web's DatePicker offered, rebuilt from scratch on opsfusion's own
 * tokens/utils (no flatpickr, no ninja-web import) so ninja-web/ninja-theme can be retired later
 * without taking this field down with them. Mirrors Select.tsx's outside-click/positioning
 * pattern rather than pulling in a popover primitive. `label` renders as a standard label above
 * the box (never floating), so every form's date fields look the same.
 */
export function DatePicker({
  id,
  label,
  placeholder = 'Select date',
  value,
  onChange,
  onClear,
  minDate,
  maxDate,
  disableFutureDates = true,
  required = false,
  disabled = false,
  error = false,
  hint,
  className,
  'aria-label': ariaLabel,
}: DatePickerProps) {
  const generatedId = useId();
  const inputId = id ?? generatedId;
  const rootRef = useRef<HTMLDivElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const [isOpen, setIsOpen] = useState(false);
  const today = new Date();
  // 330px ≈ calendar popover height (7 rows + header + padding/border)
  const position = useFloatingPosition(rootRef, isOpen, 330);

  const selectedDate = parseISODate(value);
  const minDateObj = parseISODate(minDate) ?? undefined;
  const explicitMaxDateObj = parseISODate(maxDate) ?? undefined;
  const maxDateObj = disableFutureDates
    ? explicitMaxDateObj && isBeforeDay(explicitMaxDateObj, today)
      ? explicitMaxDateObj
      : today
    : explicitMaxDateObj;
  const [viewMonth, setViewMonth] = useState(() => selectedDate ?? new Date());

  useEffect(() => {
    const next = parseISODate(value);
    if (next) setViewMonth(next);
  }, [value]);

  useEffect(() => {
    if (!isOpen) return;

    const handleOutsideClick = (event: MouseEvent) => {
      if (!(event.target instanceof Node)) return;
      // The calendar is portaled to document.body (see the `createPortal` call below) so it's
      // outside rootRef's own DOM subtree - a click inside it must not count as "outside", or
      // picking a date would close the picker on mousedown before the day button's onSelect fires.
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

  const openPicker = () => {
    if (disabled) return;
    setIsOpen((current) => !current);
  };

  const hasValue = Boolean(selectedDate);

  return (
    <div ref={rootRef} className={cn('relative', className)}>
      {label ? (
        <label htmlFor={inputId} className="mb-1.5 block text-[13px] font-medium text-foreground">
          {label}
          {required ? <span className="ml-1 text-destructive" aria-hidden="true">*</span> : null}
        </label>
      ) : null}

      <button
        id={inputId}
        type="button"
        disabled={disabled}
        aria-haspopup="dialog"
        aria-expanded={isOpen}
        aria-label={ariaLabel ?? label}
        onClick={openPicker}
        className={cn(
          'border-input dark:bg-input/30 flex h-9 w-full min-w-0 items-center justify-between gap-2 rounded-md border bg-input-background px-3 py-1 text-left text-base outline-none transition-[color,box-shadow] focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 md:text-sm',
          error && 'border-destructive focus-visible:ring-destructive/20',
        )}
      >
        <span className={cn('truncate', !hasValue && 'text-muted-foreground')}>
          {selectedDate ? formatDisplayDate(selectedDate) : placeholder}
        </span>
        <span className="flex shrink-0 items-center gap-1">
          {hasValue && !disabled ? (
            <span
              role="button"
              tabIndex={0}
              aria-label="Clear date"
              onClick={(event) => {
                event.stopPropagation();
                onChange('');
                onClear?.();
              }}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault();
                  event.stopPropagation();
                  onChange('');
                  onClear?.();
                }
              }}
              className="rounded-full p-0.5 text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
            >
              <X className="size-3.5" aria-hidden="true" />
            </span>
          ) : null}
          <CalendarDays className="size-4 text-muted-foreground" aria-hidden="true" />
        </span>
      </button>

      {isOpen && position
        ? createPortal(
            <div
              ref={menuRef}
              data-floating-popover
              // A modal Dialog sets `document.body.style.pointerEvents = 'none'` while open and only
              // re-enables `auto` on its own content node — since this popover portals to document.body
              // as a sibling, it silently inherited `none` and every click/scroll passed straight
              // through to whatever was behind it (often the dialog overlay, closing the dialog).
              style={{ position: 'fixed', top: position.top ?? undefined, bottom: position.bottom ?? undefined, left: position.left, pointerEvents: 'auto' }}
              className="z-[130]"
            >
              <div className="overflow-hidden rounded-md border border-border bg-popover text-popover-foreground shadow-md">
                <Calendar
                  month={viewMonth}
                  onMonthChange={setViewMonth}
                  selected={selectedDate}
                  minDate={minDateObj}
                  maxDate={maxDateObj}
                  onSelect={(date) => {
                    onChange(toISODate(date));
                    setIsOpen(false);
                  }}
                />
              </div>
            </div>,
            getFloatingPortalTarget(rootRef.current),
          )
        : null}

      {hint ? <p className={cn('mt-1.5 text-xs', error ? 'text-destructive' : 'text-muted-foreground')}>{hint}</p> : null}
    </div>
  );
}