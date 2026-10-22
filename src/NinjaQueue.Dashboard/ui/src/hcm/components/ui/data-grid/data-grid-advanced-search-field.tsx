'use client';

import { Input } from '../input';
import { Label } from '../label';
import { cn } from '../../../utils';

export type DataGridAdvancedSearchField = {
  key: string;
  label: string;
  type: 'text' | 'select' | 'number' | 'date';
  placeholder?: string;
  options?: { value: string; label: string }[];
  value: string;
  onChange: (value: string) => void;
  disabled?: boolean;
  allowFutureDates?: boolean;
};

const nativeSelectClassName = cn(
  'border-input flex h-9 w-full min-w-0 rounded-md border bg-input-background dark:bg-input/30 px-3 py-1 text-sm text-foreground',
  'transition-[color,box-shadow] outline-none focus-visible:border-ring focus-visible:ring-ring/50 focus-visible:ring-[3px]',
  'disabled:pointer-events-none disabled:cursor-not-allowed disabled:opacity-50',
);

/**
 * Renders one row of the advanced-search drawer for a given
 * `DataGridAdvancedSearchField`. The original ninja-web DataGrid used
 * TailAdmin's floating-label input/select/date-picker components
 * (`FormFieldFloatingLabelInput`/`...Select`/`...DatePicker`, which in turn
 * pull in `DatePicker.tsx`, `datePickerUtils.ts`, `dropdownManager.ts`, and
 * `floating-field-theme.ts`). Porting that whole floating-label engine for a
 * handful of filter fields wasn't worth it — this builds the same field
 * types directly on opsfusion-web's existing `Input`/`Label` plus a plain
 * native `<select>`/`<input type="date">`, keeping the exact same
 * `DataGridAdvancedSearchField` prop shape so callers don't need to change
 * how they build their filter field list.
 */
export function DataGridAdvancedSearchField({
  field,
}: {
  field: DataGridAdvancedSearchField;
}) {
  const fieldId = `data-grid-advanced-search-${field.key}`;
  const maxDate = field.allowFutureDates === false
    ? new Date().toISOString().slice(0, 10)
    : undefined;

  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={fieldId} className="text-muted-foreground">
        {field.label}
      </Label>

      {field.type === 'select' ? (
        <select
          id={fieldId}
          value={field.value}
          disabled={field.disabled}
          onChange={(event) => field.onChange(event.target.value)}
          className={nativeSelectClassName}
        >
          <option value="">{field.placeholder ?? field.label}</option>
          {(field.options ?? []).map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
      ) : field.type === 'date' ? (
        <Input
          id={fieldId}
          type="date"
          value={field.value}
          disabled={field.disabled}
          max={maxDate}
          onChange={(event) => field.onChange(event.target.value)}
        />
      ) : (
        <Input
          id={fieldId}
          type={field.type}
          value={field.value}
          disabled={field.disabled}
          placeholder={field.placeholder}
          onChange={(event) => field.onChange(event.target.value)}
        />
      )}
    </div>
  );
}