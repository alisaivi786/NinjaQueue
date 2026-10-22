'use client';

import { useState } from 'react';
import { ChevronLeft, ChevronRight } from 'lucide-react';

import { cn } from '../../../utils';
import { addMonths, buildMonthGrid, isAfterDay, isBeforeDay, isSameDay, MONTH_LABELS, WEEKDAY_LABELS } from './date-utils';

export type CalendarProps = {
  month: Date;
  onMonthChange: (month: Date) => void;
  selected: Date | null;
  onSelect: (date: Date) => void;
  minDate?: Date;
  maxDate?: Date;
  today?: Date;
  className?: string;
};

/**
 * Standalone month-grid calendar — plain React state and native Date math, no external date
 * library or third-party calendar widget (flatpickr, react-day-picker, etc). Owned entirely by
 * opsfusion-web so it doesn't carry a dependency on ninja-web, which is slated for retirement.
 */
export function Calendar({ month, onMonthChange, selected, onSelect, minDate, maxDate, today, className }: CalendarProps) {
  const [pickerView, setPickerView] = useState<'days' | 'months'>('days');
  const now = today ?? new Date();
  const days = buildMonthGrid(month);

  const isDisabled = (date: Date) =>
    (minDate ? isBeforeDay(date, minDate) : false) || (maxDate ? isAfterDay(date, maxDate) : false);

  return (
    <div className={cn('w-[280px] select-none p-3', className)}>
      <div className="mb-2 flex items-center justify-between">
        <button
          type="button"
          aria-label="Previous month"
          onClick={() => onMonthChange(addMonths(month, -1))}
          className="flex size-7 items-center justify-center rounded-md text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
        >
          <ChevronLeft className="size-4" />
        </button>
        <button
          type="button"
          onClick={() => setPickerView((view) => (view === 'days' ? 'months' : 'days'))}
          className="rounded-md px-2 py-1 text-sm font-medium text-foreground transition hover:bg-accent"
        >
          {MONTH_LABELS[month.getMonth()]} {month.getFullYear()}
        </button>
        <button
          type="button"
          aria-label="Next month"
          onClick={() => onMonthChange(addMonths(month, 1))}
          className="flex size-7 items-center justify-center rounded-md text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
        >
          <ChevronRight className="size-4" />
        </button>
      </div>

      {pickerView === 'months' ? (
        <MonthYearPicker
          month={month}
          onChange={(next) => {
            onMonthChange(next);
            setPickerView('days');
          }}
        />
      ) : (
        <>
          <div className="mb-1 grid grid-cols-7 text-center text-xs font-medium text-muted-foreground">
            {WEEKDAY_LABELS.map((label) => (
              <span key={label} className="py-1">
                {label}
              </span>
            ))}
          </div>
          <div className="grid grid-cols-7 gap-y-1 text-center text-sm">
            {days.map((date) => {
              const outsideMonth = date.getMonth() !== month.getMonth();
              const disabled = isDisabled(date);
              const isToday = isSameDay(date, now);
              // With nothing chosen yet, show today as the default-selected day so the
              // calendar doesn't open on a blank grid.
              const isSelected = selected ? isSameDay(date, selected) : isToday;
              return (
                <button
                  key={date.toISOString()}
                  type="button"
                  disabled={disabled}
                  onClick={() => onSelect(date)}
                  className={cn(
                    'mx-auto flex size-8 items-center justify-center rounded-full transition',
                    outsideMonth && 'text-muted-foreground/40',
                    !outsideMonth && !isSelected && 'text-foreground hover:bg-accent hover:text-accent-foreground',
                    isToday && !isSelected && 'font-semibold text-primary',
                    isSelected && 'bg-primary text-primary-foreground hover:bg-primary',
                    disabled && 'pointer-events-none opacity-30',
                  )}
                >
                  {date.getDate()}
                </button>
              );
            })}
          </div>
        </>
      )}
    </div>
  );
}

/** The year stepper and 12-month grid - the calendar's month view, and the whole of MonthPicker. */
export function MonthYearPicker({ month, onChange }: { month: Date; onChange: (date: Date) => void }) {
  const [year, setYear] = useState(month.getFullYear());

  return (
    <div>
      <div className="mb-2 flex items-center justify-center gap-3">
        <button
          type="button"
          aria-label="Previous year"
          onClick={() => setYear((current) => current - 1)}
          className="flex size-7 items-center justify-center rounded-md text-muted-foreground transition hover:bg-accent"
        >
          <ChevronLeft className="size-4" />
        </button>
        <span className="text-sm font-medium text-foreground">{year}</span>
        <button
          type="button"
          aria-label="Next year"
          onClick={() => setYear((current) => current + 1)}
          className="flex size-7 items-center justify-center rounded-md text-muted-foreground transition hover:bg-accent"
        >
          <ChevronRight className="size-4" />
        </button>
      </div>
      <div className="grid grid-cols-3 gap-1">
        {MONTH_LABELS.map((label, index) => {
          const isActive = index === month.getMonth() && year === month.getFullYear();
          return (
            <button
              key={label}
              type="button"
              onClick={() => onChange(new Date(year, index, 1))}
              className={cn(
                'rounded-md px-2 py-1.5 text-xs transition hover:bg-accent',
                isActive ? 'bg-primary text-primary-foreground hover:bg-primary' : 'text-foreground',
              )}
            >
              {label.slice(0, 3)}
            </button>
          );
        })}
      </div>
    </div>
  );
}