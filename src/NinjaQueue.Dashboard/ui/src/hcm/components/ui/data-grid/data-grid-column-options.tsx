'use client';

import { Tooltip, TooltipContent, TooltipTrigger } from '../tooltip';
import {
  DropdownMenu,
  DropdownMenuCheckboxItem,
  DropdownMenuContent,
  DropdownMenuTrigger,
} from '../dropdown-menu';
import { TableIcon } from '@ninja-icon';
import type { DataGridColumn } from './data-grid';

/**
 * Column show/hide picker. The original ninja-web `ColumnOptionsDropdown`
 * hand-rolled its own open state, outside-click listener, and fixed-position
 * menu math. OpsFusion-web already ships a Radix `DropdownMenu` (positioning,
 * outside-click, escape handling all built in), so this is a much smaller
 * wrapper around it — genuinely simpler, not just re-themed.
 */
export function DataGridColumnOptions<T>({
  columns,
  visibleColumns,
  onChange,
  onReset,
  title,
  resetLabel,
  visibleCountLabel,
}: {
  columns: Array<DataGridColumn<T>>;
  visibleColumns: Record<string, boolean>;
  onChange: (columnId: string, visible: boolean) => void;
  onReset: () => void;
  title: string;
  resetLabel: string;
  visibleCountLabel: (visible: number, total: number) => string;
}) {
  const optionColumns = columns.filter(
    (column) => column.showInColumnOptions !== false,
  );
  const visibleCount = optionColumns.filter(
    (column) => visibleColumns[column.id ?? column.key],
  ).length;

  return (
    <DropdownMenu>
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <button
              type="button"
              aria-label={title}
              className="inline-flex h-11 w-11 items-center justify-center rounded-lg border border-border bg-transparent text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
            >
              <TableIcon />
            </button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>{title}</TooltipContent>
      </Tooltip>

      <DropdownMenuContent align="end" className="w-64 p-3">
        <div className="mb-3 flex items-start justify-between gap-3">
          <div>
            <p className="text-sm font-semibold text-foreground">{title}</p>
            <p className="text-xs text-muted-foreground">
              {visibleCountLabel(visibleCount, optionColumns.length)}
            </p>
          </div>
          <button
            type="button"
            onClick={onReset}
            className="text-xs font-medium text-primary hover:text-primary/80"
          >
            {resetLabel}
          </button>
        </div>

        <div className="opsfusion-content-scrollbar max-h-64 space-y-0.5 overflow-y-auto pr-1">
          {optionColumns.map((column) => {
            const columnId = column.id ?? column.key;
            return (
              <DropdownMenuCheckboxItem
                key={columnId}
                checked={Boolean(visibleColumns[columnId])}
                disabled={column.required}
                onSelect={(event) => event.preventDefault()}
                onCheckedChange={(checked) => onChange(columnId, checked)}
              >
                {column.label}
              </DropdownMenuCheckboxItem>
            );
          })}
        </div>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}