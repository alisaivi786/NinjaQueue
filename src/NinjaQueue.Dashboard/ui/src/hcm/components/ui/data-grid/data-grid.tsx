'use client';

import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type CSSProperties,
  type ReactNode,
} from 'react';
import type React from 'react';
import {
  AngleDownIcon,
  AngleUpIcon,
  ExportIcon,
  PdfIcon,
  MicrosoftExcelIcon,
} from '@ninja-icon';
import { Maximize2, Minimize2 } from 'lucide-react';

import { cn } from '../../../utils';
import { Button } from '../button';
import { Select } from '../select';
import { Tooltip, TooltipContent, TooltipTrigger } from '../tooltip';
import { DataGridPagination } from './data-grid-pagination';
import { DataGridRefreshButton } from './data-grid-refresh-button';
import { DataGridColumnOptions } from './data-grid-column-options';
import {
  DataGridAdvancedSearchField as DataGridAdvancedSearchFieldRow,
  type DataGridAdvancedSearchField,
} from './data-grid-advanced-search-field';
import {
  CheckIcon,
  CloseXIcon,
  CopyIcon,
  PrintIcon,
  SearchIcon,
} from './data-grid-icons';

// `DataGridAdvancedSearchField` (the field-shape type) is re-exported from
// this folder's index.ts directly off ./data-grid-advanced-search-field —
// not re-exported here — because that module also exports a same-named
// component (its row renderer), and index.ts needs to cherry-pick just the
// type to avoid an ambiguous `export *` collision. See index.ts.

type SortDirection = 'asc' | 'desc';

/**
 * The original ninja-web DataGrid supported 12 header-color variants
 * (`default | subtle | brand | blue | purple | pink | dark | slate |
 * success | warning | orange | vault | danger`), all hardcoded against
 * TailAdmin's fixed Tailwind palette (`bg-brand-50`, `text-blue-light-*`,
 * `bg-success-500`, `#fdf2fa`, etc). OpsFusion's own token set only defines
 * `primary` / `secondary` / `muted` / `accent` / `destructive` — there's no
 * OpsFusion equivalent for a "blue" or "purple" or "success" family, and
 * inventing new color tokens just to keep those variant names would work
 * against the actual goal here (sync with OUR palette, not import
 * TailAdmin's extra ones). So the variant set is collapsed down to what the
 * OpsFusion palette actually supports; `blue`/`purple`/`pink`/`orange`/
 * `vault`/`success`/`warning` are dropped entirely rather than faked.
 */

const MIN_REFRESH_SKELETON_MS = 400;

const SORT_COLLATOR = new Intl.Collator(undefined, {
  numeric: true,
  sensitivity: 'base',
});

const isEmptyCell = (value: unknown) =>
  value === null ||
  value === undefined ||
  value === '' ||
  (Array.isArray(value) && value.length === 0);

/** Header-sort order: numbers/booleans by value, text case-insensitive and number-aware ("EMP-2" < "EMP-10"),
 * arrays by their joined text, and empty cells always last whichever way the column is sorted. */
function compareCellValues(a: unknown, b: unknown, direction: 1 | -1) {
  if (isEmptyCell(a) || isEmptyCell(b))
    return Number(isEmptyCell(a)) - Number(isEmptyCell(b));
  if (typeof a === 'number' && typeof b === 'number')
    return (a - b) * direction;
  if (typeof a === 'boolean' && typeof b === 'boolean')
    return (Number(a) - Number(b)) * direction;
  const text = (value: unknown) =>
    Array.isArray(value) ? value.join(', ') : String(value);
  return SORT_COLLATOR.compare(text(a), text(b)) * direction;
}

export type DataGridHeaderVariant =
  'default' | 'subtle' | 'primary' | 'muted' | 'dark' | 'destructive';

export type DataGridHeaderClassNames = {
  thead: string;
  cell: string;
  label: string;
  sort: string;
  activeSort: string;
  resize: string;
};

const DATA_GRID_HEADER_VARIANTS: Record<
  DataGridHeaderVariant,
  DataGridHeaderClassNames
> = {
  default: {
    thead: 'bg-muted shadow-[0_-1px_0_0_var(--muted),0_1px_0_0_var(--border)]',
    cell: 'border-border bg-muted',
    label: 'text-foreground',
    sort: 'text-muted-foreground/40 hover:text-muted-foreground',
    activeSort: 'text-primary',
    resize: 'hover:border-primary',
  },
  subtle: {
    thead:
      'bg-background shadow-[0_-1px_0_0_var(--background),0_1px_0_0_var(--border)]',
    cell: 'border-border bg-background',
    label: 'text-foreground/90',
    sort: 'text-muted-foreground/40 hover:text-muted-foreground',
    activeSort: 'text-primary',
    resize: 'hover:border-primary',
  },
  primary: {
    // Solid palette color + white text — the default look, since `--primary`
    // itself now tracks the active color-theme palette (see
    // libs/opsfusion-theme/src/apply.ts), so this is what makes a DataGrid
    // header actually read as "themed" rather than a generic gray table.
    thead:
      'bg-primary shadow-[0_-1px_0_0_var(--primary),0_1px_0_0_var(--primary)]',
    cell: 'border-primary/60 bg-primary',
    label: 'text-primary-foreground',
    sort: 'text-primary-foreground/45 hover:text-primary-foreground',
    activeSort: 'text-primary-foreground',
    resize: 'hover:border-primary-foreground/60',
  },
  muted: {
    thead:
      'bg-secondary shadow-[0_-1px_0_0_var(--secondary),0_1px_0_0_var(--border)]',
    cell: 'border-border bg-secondary',
    label: 'text-secondary-foreground',
    sort: 'text-muted-foreground/50 hover:text-muted-foreground',
    activeSort: 'text-primary',
    resize: 'hover:border-primary',
  },
  dark: {
    thead:
      'bg-foreground shadow-[0_-1px_0_0_var(--foreground),0_1px_0_0_var(--foreground)]',
    cell: 'border-foreground/80 bg-foreground',
    label: 'text-background',
    sort: 'text-background/35 hover:text-background/70',
    activeSort: 'text-primary',
    resize: 'hover:border-background/50',
  },
  destructive: {
    thead:
      'bg-destructive/10 shadow-[0_-1px_0_0_var(--destructive),0_1px_0_0_var(--destructive)]',
    cell: 'border-destructive/20 bg-destructive/10',
    label: 'text-destructive',
    sort: 'text-destructive/40 hover:text-destructive/70',
    activeSort: 'text-destructive',
    resize: 'hover:border-destructive',
  },
};

export type DataGridColumn<T> = {
  key: keyof T & string;
  id?: string;
  label: string;
  /** Defaults to `false` — set `true` to make the column header sortable. */
  sortable?: boolean;
  className?: string;
  render?: (row: T) => ReactNode;
  /** Shown instead of an empty/null/undefined cell value. Falls back to the grid's `emptyValueLabel`. */
  emptyValue?: ReactNode;
  textLimit?: number;
  copyable?: boolean;
  width?: string | number;
  /** Minimum width used to seed the resizable width. Accepts e.g. `'160px'`. */
  minWidth?: string;
  /** Maximum width enforced while dragging the resize handle. */
  maxWidth?: string;
  /** Column cannot be hidden from the column-options dropdown. */
  required?: boolean;
  /** Hidden on first render when `false`. Defaults to visible. */
  defaultVisible?: boolean;
  /** Omit the column from the column-options dropdown entirely (still renders in the grid). */
  showInColumnOptions?: boolean;
};

export type DataGridAdvancedSearchSummaryItem = {
  label: string;
  value: string;
};

export type DataGridProps<T> = {
  columns: Array<DataGridColumn<T>>;
  rows: T[];
  isLoading?: boolean;
  /** Overrides the loading placeholder count. Defaults to the active page size, capped at 50. */
  skeletonRows?: number;
  rowKey: (row: T) => string | number;
  initialSortKey?: keyof T & string;
  initialSortDirection?: SortDirection;
  pageSizeOptions?: number[];
  defaultPageSize?: number;
  paginationMode?: 'client' | 'server';
  currentPage?: number;
  pageSize?: number;
  totalItems?: number;
  paginationVariant?: 'text' | 'text-icon' | 'icon';
  flat?: boolean;
  showPagination?: boolean;
  showPageSizeSelector?: boolean;
  showRefresh?: boolean;
  /** Adds a toolbar button that opens the grid over the whole screen (and back; Esc also exits). */
  showFullScreen?: boolean;
  showPrint?: boolean;
  showPdf?: boolean;
  showExcel?: boolean;
  showExport?: boolean;
  onRefresh?: () => void | Promise<void>;
  onPrint?: () => void | Promise<void>;
  onPdf?: () => void | Promise<void>;
  onExcel?: () => void | Promise<void>;
  onExport?: () => void | Promise<void>;
  printLabel?: string;
  pdfLabel?: string;
  excelLabel?: string;
  onPageChange?: (page: number) => void | Promise<void>;
  onPageSizeChange?: (pageSize: number) => void | Promise<void>;
  onSortChange?: (sort: {
    key: keyof T & string;
    direction: SortDirection;
  }) => void | Promise<void>;
  sortMode?: 'client' | 'server';
  sortKey?: keyof T & string;
  sortDirection?: SortDirection;
  toolbarButtons?: Array<{
    key: string;
    label: string;
    icon?: ReactNode;
    onClick: () => void | Promise<void>;
    className?: string;
    iconOnly?: boolean;
    disabled?: boolean;
    variant?: 'primary' | 'outline' | 'danger';
  }>;
  pageSizePrefixLabel?: string;
  pageSizeSuffixLabel?: string;
  refreshLabel?: string;
  exportLabel?: string;
  loadingEntriesLabel?: string;
  noRecordsLabel?: string;
  /** Default shown for a column cell whose value (or `render` result) is empty/null/undefined. */
  emptyValueLabel?: ReactNode;
  sortByLabel?: (label: string) => string;
  showingEntriesLabel?: (summary: {
    start: number;
    end: number;
    total: number;
  }) => string;
  // Search & Advanced Search
  searchValue?: string;
  searchPlaceholder?: string;
  onSearchChange?: (value: string) => void;
  toolbarFilters?: ReactNode;
  advancedSearchFields?: DataGridAdvancedSearchField[];
  /** Shows the Advanced Search button/drawer even when `advancedSearchFields` is empty (e.g. while the concrete filter set is still being defined). Defaults to hiding it when there are no fields. */
  showAdvancedSearch?: boolean;
  /** Message shown in the drawer body when there are no advanced search fields yet. */
  advancedSearchEmptyLabel?: string;
  onAdvancedSearch?: () => void | Promise<void>;
  onAdvancedSearchReset?: () => void;
  advancedSearchLabel?: string;
  advancedSearchTitle?: string;
  advancedSearchSummaryItems?: DataGridAdvancedSearchSummaryItem[];
  applyFiltersLabel?: string;
  resetFiltersLabel?: string;
  activeFiltersCount?: number;
  // Column resize / reorder / freeze / show-hide
  /** Lets users drag a column's right edge to resize it. Default `true`. */
  enableColumnResize?: boolean;
  /** Lets users drag-reorder columns by their header. Default `true`. */
  enableColumnReorder?: boolean;
  /** Freezes the first visible column while scrolling horizontally. Default `true`. */
  stickyFirstColumn?: boolean;
  /** Freeze the first N columns; one width entry per frozen column. Overrides `stickyFirstColumn` when set. */
  stickyLeadingColumnWidths?: string[];
  /** Width used for columns that do not define `width`, `minWidth`, or `maxWidth`. Omit to keep auto-fit behavior. */
  defaultColumnWidth?: string | number;
  /** Expands unsized columns based on their header and primitive row content. Explicit column widths still win. */
  autoSizeColumns?: boolean;
  /** Lower bound for `autoSizeColumns`. Defaults to `defaultColumnWidth`, then the grid's responsive minimum. */
  minAutoSizeColumnWidth?: string | number;
  /** Upper bound for `autoSizeColumns`. Defaults to 360px. */
  maxAutoSizeColumnWidth?: string | number;
  /** Shows the column show/hide picker button in the toolbar. Default `true`. */
  showColumnOptions?: boolean;
  /** Visual treatment for the table header. Default preserves the existing DataGrid header style. */
  headerVariant?: DataGridHeaderVariant;
  /** Optional class overrides appended to the selected header variant. */
  headerClassNames?: Partial<DataGridHeaderClassNames>;
  columnOptionsLabel?: string;
  columnOptionsResetLabel?: string;
  columnOptionsVisibleCountLabel?: (visible: number, total: number) => string;
  resizeColumnLabel?: (label: string) => string;
};

function CopyIconButton({ value }: { value: string }) {
  const [copied, setCopied] = useState(false);

  const handleCopy = () => {
    void navigator.clipboard.writeText(value).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  };

  return (
    <button
      type="button"
      onClick={handleCopy}
      title={copied ? 'Copied!' : 'Copy to clipboard'}
      className={cn(
        'ml-1.5 inline-flex flex-shrink-0 items-center justify-center transition-colors',
        copied ? 'text-primary' : 'text-muted-foreground hover:text-foreground',
      )}
    >
      {copied ? <CheckIcon /> : <CopyIcon />}
    </button>
  );
}

const parsePixelValue = (value?: string | number) => {
  if (value === undefined) return undefined;
  const parsed = typeof value === 'number' ? value : Number.parseFloat(value);
  return Number.isFinite(parsed) ? parsed : undefined;
};

/** Applied to any column that doesn't declare its own `minWidth`. */
const DEFAULT_COLUMN_MIN_WIDTH = '160px';

export function DataGrid<T>({
  columns,
  rows,
  isLoading: isLoadingProp = false,
  skeletonRows,
  rowKey,
  initialSortKey,
  initialSortDirection = 'asc',
  pageSizeOptions = [10, 20, 50, 100, 500, 1000],
  defaultPageSize = 10,
  paginationMode = 'client',
  currentPage: controlledCurrentPage,
  pageSize: controlledPageSize,
  totalItems: controlledTotalItems,
  paginationVariant = 'text-icon',
  flat = false,
  showPagination = true,
  showPageSizeSelector = true,
  showRefresh = true,
  showFullScreen = false,
  showPrint = false,
  showPdf = false,
  showExcel = false,
  showExport = false,
  onRefresh,
  onPrint,
  onPdf,
  onExcel,
  onExport,
  printLabel = 'Print',
  pdfLabel = 'Export PDF',
  excelLabel = 'Export Excel',
  onPageChange,
  onPageSizeChange,
  onSortChange,
  sortMode = 'client',
  sortKey: controlledSortKey,
  sortDirection: controlledSortDirection,
  toolbarButtons = [],
  pageSizePrefixLabel = 'Show',
  pageSizeSuffixLabel = 'entries',
  refreshLabel = 'Refresh data',
  exportLabel,
  loadingEntriesLabel = 'Loading entries...',
  noRecordsLabel = 'No records found',
  emptyValueLabel = '-',
  sortByLabel = (label) => `Sort by ${label}`,
  showingEntriesLabel = ({ start, end, total }) =>
    `Showing ${start}–${end} of ${total} total`,
  searchValue = '',
  searchPlaceholder = 'Search...',
  onSearchChange,
  toolbarFilters,
  advancedSearchFields,
  showAdvancedSearch = false,
  advancedSearchEmptyLabel = 'No filters are available for this list yet.',
  onAdvancedSearch,
  onAdvancedSearchReset,
  advancedSearchLabel = 'Advanced Search',
  advancedSearchTitle,
  advancedSearchSummaryItems = [],
  applyFiltersLabel = 'Apply Filters',
  resetFiltersLabel = 'Reset',
  activeFiltersCount = 0,
  enableColumnResize = true,
  enableColumnReorder = true,
  stickyFirstColumn = true,
  stickyLeadingColumnWidths,
  defaultColumnWidth,
  autoSizeColumns = false,
  minAutoSizeColumnWidth,
  maxAutoSizeColumnWidth,
  showColumnOptions = false,
  headerVariant = 'primary',
  headerClassNames,
  columnOptionsLabel = 'Column Options',
  columnOptionsResetLabel = 'Reset',
  columnOptionsVisibleCountLabel = (visible, total) =>
    `${visible} of ${total} visible`,
  resizeColumnLabel = (label) => `Resize ${label} column`,
}: DataGridProps<T>) {
  const [internalCurrentPage, setInternalCurrentPage] = useState(1);
  const [internalItemsPerPage, setInternalItemsPerPage] =
    useState(defaultPageSize);
  const [internalSortKey, setInternalSortKey] = useState<string | undefined>(
    initialSortKey,
  );
  const [internalSortOrder, setInternalSortOrder] =
    useState<SortDirection>(initialSortDirection);
  const [isRefreshing, setIsRefreshing] = useState(false);
  // A user refresh always shows the skeleton, even when the page only reports first-load `isLoading`.
  const isLoading = isLoadingProp || isRefreshing;
  const [isFullScreen, setIsFullScreen] = useState(false);

  // Full screen: Esc exits, and the page behind stops scrolling.
  useEffect(() => {
    if (!isFullScreen) return;
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setIsFullScreen(false);
    };
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    window.addEventListener('keydown', onKeyDown);
    return () => {
      document.body.style.overflow = overflow;
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [isFullScreen]);
  const [expandedCell, setExpandedCell] = useState<string | null>(null);
  const [isAdvancedSearchOpen, setIsAdvancedSearchOpen] = useState(false);
  const tableContainerRef = useRef<HTMLDivElement | null>(null);

  const getColumnId = (column: DataGridColumn<T>) => column.id ?? column.key;
  const parsedDefaultColumnWidth = parsePixelValue(defaultColumnWidth);
  const parsedMinAutoSizeColumnWidth = parsePixelValue(minAutoSizeColumnWidth);
  const parsedMaxAutoSizeColumnWidth = parsePixelValue(maxAutoSizeColumnWidth);

  const [columnOrder, setColumnOrder] = useState<string[]>(() =>
    columns.map(getColumnId),
  );
  // Only columns the consumer explicitly sized, or that the user has dragged
  // to resize, live here ("pinned"). Anything else is "auto" and shares the
  // remaining container width equally — see `autoColumnWidth` below — so the
  // table always fits the available space until the user customizes it.
  const [pinnedWidths, setPinnedWidths] = useState<Record<string, number>>(() =>
    columns.reduce(
      (widths, column, index) => {
        const explicit =
          parsePixelValue(column.width) ??
          parsePixelValue(column.minWidth ?? DEFAULT_COLUMN_MIN_WIDTH) ??
          parsePixelValue(column.maxWidth) ??
          parsePixelValue(stickyLeadingColumnWidths?.[index]) ??
          parsedDefaultColumnWidth;
        if (explicit !== undefined) widths[getColumnId(column)] = explicit;
        return widths;
      },
      {} as Record<string, number>,
    ),
  );
  const [containerWidth, setContainerWidth] = useState(0);
  const [draggedColumnId, setDraggedColumnId] = useState<string | null>(null);
  const [visibleColumns, setVisibleColumns] = useState<Record<string, boolean>>(
    () =>
      columns.reduce(
        (visible, column) => {
          visible[getColumnId(column)] = column.defaultVisible !== false;
          return visible;
        },
        {} as Record<string, boolean>,
      ),
  );
  const [isMobileView, setIsMobileView] = useState(false);
  const safeRows = Array.isArray(rows) ? rows : [];

  useEffect(() => {
    if (typeof window === 'undefined') return;
    const mediaQuery = window.matchMedia('(max-width: 767px)');
    const handleChange = (event: MediaQueryListEvent | MediaQueryList) =>
      setIsMobileView(event.matches);
    handleChange(mediaQuery);
    if (typeof mediaQuery.addEventListener === 'function') {
      mediaQuery.addEventListener('change', handleChange);
      return () => mediaQuery.removeEventListener('change', handleChange);
    }
    mediaQuery.addListener(handleChange);
    return () => mediaQuery.removeListener(handleChange);
  }, []);

  // Tracks the scroll container's available width so "auto" (unpinned)
  // columns can share it evenly and the table keeps fitting the screen as
  // columns are shown/hidden — resizing feeds off this same measurement.
  useEffect(() => {
    const container = tableContainerRef.current;
    if (!container || typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (entry) setContainerWidth(entry.contentRect.width);
    });
    observer.observe(container);
    return () => observer.disconnect();
  }, []);

  // Resync order/widths/visibility with the `columns` prop while preserving
  // any user-driven reordering, resizing, or show/hide choices already made.
  useEffect(() => {
    const nextIds = columns.map(getColumnId);
    const nextIdSet = new Set(nextIds);

    setColumnOrder((currentOrder) => {
      const orderedIds = currentOrder.filter((id) => nextIdSet.has(id));
      const newIds = nextIds.filter((id) => !orderedIds.includes(id));
      const nextOrder = [...orderedIds, ...newIds];
      if (
        nextOrder.length === currentOrder.length &&
        nextOrder.every((id, index) => id === currentOrder[index])
      ) {
        return currentOrder;
      }
      return nextOrder;
    });

    setVisibleColumns((current) => {
      const next = { ...current };
      let changed = false;
      for (const column of columns) {
        const id = getColumnId(column);
        if (!(id in next)) {
          next[id] = column.defaultVisible !== false;
          changed = true;
        }
      }
      return changed ? next : current;
    });

    setPinnedWidths((current) => {
      const next = { ...current };
      let changed = false;
      for (const column of columns) {
        const id = getColumnId(column);
        if (next[id] === undefined) {
          const explicit =
            parsePixelValue(column.width) ??
            parsePixelValue(column.minWidth ?? DEFAULT_COLUMN_MIN_WIDTH) ??
            parsePixelValue(column.maxWidth) ??
            parsedDefaultColumnWidth;
          if (explicit !== undefined) {
            next[id] = explicit;
            changed = true;
          }
        }
      }
      return changed ? next : current;
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [columns, parsedDefaultColumnWidth]);

  const columnById = useMemo(() => {
    const map: Record<string, DataGridColumn<T>> = {};
    for (const column of columns) map[getColumnId(column)] = column;
    return map;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [columns]);

  const visibleTableColumns = useMemo(
    () =>
      columnOrder
        .map((id) => columnById[id])
        .filter(
          (column): column is DataGridColumn<T> =>
            Boolean(column) && visibleColumns[getColumnId(column)] !== false,
        ),
    [columnOrder, columnById, visibleColumns],
  );

  const canResizeColumns = enableColumnResize && !isMobileView;
  const canReorderColumns = enableColumnReorder && !isMobileView;

  const stickyColumnCount = isMobileView
    ? 0
    : stickyLeadingColumnWidths?.length
      ? stickyLeadingColumnWidths.length
      : stickyFirstColumn
        ? 1
        : 0;

  // Columns without an explicit/pinned width equally share whatever space is
  // left in the container after pinned columns take theirs — this is what
  // keeps the table fitting the screen as columns are hidden or shown. Once
  // pinned columns leave less than this floor per auto column, the table
  // simply grows past the container and scrolls instead of squeezing them.
  // Above the mobile breakpoint the floor stays low so the grid keeps fitting
  // in one row (content truncates instead) — only on genuinely small/mobile
  // screens do we allow the more comfortable, scrollable floor.
  const AUTO_COLUMN_MIN_WIDTH = isMobileView ? 120 : 64;
  const maxMeasuredColumnWidth = parsedMaxAutoSizeColumnWidth ?? 360;
  const minMeasuredColumnWidth =
    parsedMinAutoSizeColumnWidth ??
    parsedDefaultColumnWidth ??
    AUTO_COLUMN_MIN_WIDTH;

  const getMeasuredColumnWidth = useCallback(
    (column: DataGridColumn<T>) => {
      const headerWidth = column.label.length * 7.5 + 54;
      const rowWidth = safeRows.slice(0, 50).reduce((largest, row) => {
        if (column.render) {
          const rendered = column.render(row);
          if (typeof rendered !== 'string' && typeof rendered !== 'number') {
            return largest;
          }
          return Math.max(largest, String(rendered).length * 7.5 + 32);
        }

        const value = row[column.key as keyof T];
        if (
          value === null ||
          value === undefined ||
          typeof value === 'object' ||
          typeof value === 'function'
        ) {
          return largest;
        }

        return Math.max(largest, String(value).length * 7.5 + 32);
      }, 0);

      return Math.min(
        maxMeasuredColumnWidth,
        Math.max(minMeasuredColumnWidth, headerWidth, rowWidth),
      );
    },
    [maxMeasuredColumnWidth, minMeasuredColumnWidth, safeRows],
  );

  const pinnedVisibleSum = useMemo(
    () =>
      visibleTableColumns.reduce((total, column) => {
        const pinned = pinnedWidths[getColumnId(column)];
        if (pinned !== undefined) return total + pinned;
        return autoSizeColumns ? total + getMeasuredColumnWidth(column) : total;
      }, 0),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [
      visibleTableColumns,
      pinnedWidths,
      autoSizeColumns,
      getMeasuredColumnWidth,
    ],
  );

  const autoVisibleCount = useMemo(
    () =>
      visibleTableColumns.filter(
        (column) => pinnedWidths[getColumnId(column)] === undefined,
      ).length,
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [visibleTableColumns, pinnedWidths],
  );

  const autoColumnWidth =
    autoVisibleCount > 0
      ? Math.max(
          AUTO_COLUMN_MIN_WIDTH,
          (containerWidth - pinnedVisibleSum) / autoVisibleCount,
        )
      : 0;

  const getDesiredColumnPxWidth = (column: DataGridColumn<T>) =>
    pinnedWidths[getColumnId(column)] ??
    (autoSizeColumns
      ? Math.max(getMeasuredColumnWidth(column), autoColumnWidth)
      : autoColumnWidth);

  const desiredTableWidth = useMemo(
    () =>
      visibleTableColumns.reduce(
        (total, column) => total + getDesiredColumnPxWidth(column),
        0,
      ),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [visibleTableColumns, pinnedWidths, autoColumnWidth],
  );

  const fillWidthExtraPerColumn =
    visibleTableColumns.length > 0
      ? Math.max(0, containerWidth - desiredTableWidth) /
        visibleTableColumns.length
      : 0;

  const getColumnPxWidth = (column: DataGridColumn<T>) =>
    getDesiredColumnPxWidth(column) + fillWidthExtraPerColumn;

  const effectiveTableWidth = Math.max(desiredTableWidth, containerWidth);

  const getStickyLeftOffset = (index: number) => {
    let offset = 0;
    for (let i = 0; i < index; i += 1) {
      offset += getColumnPxWidth(visibleTableColumns[i]);
    }
    return offset;
  };

  const getStickyCellStyle = (index: number): CSSProperties | undefined => {
    if (index >= stickyColumnCount) return undefined;
    const column = visibleTableColumns[index];
    const width = getColumnPxWidth(column);
    return {
      position: 'sticky',
      left: `${getStickyLeftOffset(index)}px`,
      zIndex: index === stickyColumnCount - 1 ? 25 : 24 - index,
      minWidth: `${width}px`,
      width: `${width}px`,
    };
  };

  // Frozen cells need a fully OPAQUE background so columns scrolling
  // underneath don't show through — a sticky cell isn't just sitting once
  // over the page shell like a card, it's compositing directly over whatever
  // row/column is scrolling past beneath it, every frame. Every body cell
  // (frozen or not) now paints the same solid bg-card + hover tint directly
  // in its own className, so a frozen column can never drift out of sync
  // with the rest of the row the way it would if it alone opted out of the
  // page's translucent "card surface" tint.
  const getStickyClassName = (index: number, isHeader: boolean) => {
    if (index >= stickyColumnCount) return '';
    const dividerClass =
      index === stickyColumnCount - 1 ? 'shadow-[1px_0_0_0_var(--border)]' : '';
    if (isHeader) {
      return index === stickyColumnCount - 1
        ? 'z-30 after:absolute after:right-0 after:top-0 after:h-full after:w-px after:bg-border after:content-[""]'
        : 'z-30';
    }
    return `relative isolate ${dividerClass}`;
  };

  const handleColumnDragStart = (
    event: React.DragEvent<HTMLTableCellElement>,
    columnId: string,
  ) => {
    if (!canReorderColumns) return;
    setDraggedColumnId(columnId);
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', columnId);
  };

  const handleColumnDrop = (
    event: React.DragEvent<HTMLTableCellElement>,
    targetColumnId: string,
  ) => {
    event.preventDefault();
    if (
      !canReorderColumns ||
      !draggedColumnId ||
      draggedColumnId === targetColumnId
    ) {
      setDraggedColumnId(null);
      return;
    }
    setColumnOrder((currentOrder) => {
      const nextOrder = currentOrder.filter((id) => id !== draggedColumnId);
      const targetIndex = nextOrder.indexOf(targetColumnId);
      if (targetIndex === -1) return currentOrder;
      nextOrder.splice(targetIndex, 0, draggedColumnId);
      return nextOrder;
    });
    setDraggedColumnId(null);
  };

  const handleColumnResizeStart = (
    event: React.MouseEvent<HTMLButtonElement>,
    columnId: string,
  ) => {
    if (!canResizeColumns) return;
    event.preventDefault();
    event.stopPropagation();

    const activeColumn = columnById[columnId];
    const startWidth =
      pinnedWidths[columnId] ??
      (activeColumn ? getDesiredColumnPxWidth(activeColumn) : undefined) ??
      autoColumnWidth;
    const startX = event.clientX;

    // Freeze every other currently-auto column at its present rendered width
    // the moment a drag begins, so dragging one column never shifts any
    // other — only the table's total width (and therefore the scroll area)
    // grows or shrinks.
    setPinnedWidths((current) => {
      const next = { ...current };
      for (const column of visibleTableColumns) {
        const id = getColumnId(column);
        if (next[id] === undefined) next[id] = getDesiredColumnPxWidth(column);
      }
      next[columnId] = startWidth;
      return next;
    });

    const handleMouseMove = (moveEvent: MouseEvent) => {
      setPinnedWidths((current) => ({
        ...current,
        [columnId]: Math.max(88, startWidth + moveEvent.clientX - startX),
      }));
    };
    const handleMouseUp = () => {
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
      document.removeEventListener('mousemove', handleMouseMove);
      document.removeEventListener('mouseup', handleMouseUp);
    };
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
    document.addEventListener('mousemove', handleMouseMove);
    document.addEventListener('mouseup', handleMouseUp);
  };

  const currentPage = controlledCurrentPage ?? internalCurrentPage;
  const itemsPerPage = controlledPageSize ?? internalItemsPerPage;
  const activeSortKey = controlledSortKey ?? internalSortKey;
  const activeSortOrder = controlledSortDirection ?? internalSortOrder;

  const hasSearch = Boolean(onSearchChange);
  const hasAdvancedSearch =
    showAdvancedSearch ||
    (Array.isArray(advancedSearchFields) && advancedSearchFields.length > 0);

  const toggleCell = useCallback((rowId: string, colKey: string) => {
    const cellKey = `${rowId}::${colKey}`;
    setExpandedCell((prev) => (prev === cellKey ? null : cellKey));
  }, []);

  const filteredAndSortedData = useMemo(() => {
    if (sortMode === 'server' || !activeSortKey) return safeRows;

    const direction = activeSortOrder === 'asc' ? 1 : -1;
    return [...safeRows].sort((a, b) =>
      compareCellValues(
        a[activeSortKey as keyof T],
        b[activeSortKey as keyof T],
        direction,
      ),
    );
  }, [safeRows, activeSortKey, activeSortOrder, sortMode]);

  const totalItems =
    paginationMode === 'server'
      ? controlledTotalItems || safeRows.length
      : filteredAndSortedData.length;
  const totalPages = Math.ceil(totalItems / itemsPerPage) || 1;
  const startIndex = (currentPage - 1) * itemsPerPage;
  const endIndex = Math.min(startIndex + itemsPerPage, totalItems);
  // Server paging + client sort: the server picks the page, the header sort orders the rows on it.
  const currentData =
    paginationMode === 'server'
      ? filteredAndSortedData
      : filteredAndSortedData.slice(startIndex, endIndex);
  const effectiveSkeletonRows = skeletonRows ?? Math.min(itemsPerPage, 50);
  const headerVariantClasses =
    DATA_GRID_HEADER_VARIANTS[headerVariant] ??
    DATA_GRID_HEADER_VARIANTS.default;
  const headerClasses: DataGridHeaderClassNames = {
    thead: `${headerVariantClasses.thead} ${headerClassNames?.thead ?? ''}`,
    cell: `${headerVariantClasses.cell} ${headerClassNames?.cell ?? ''}`,
    label: `${headerVariantClasses.label} ${headerClassNames?.label ?? ''}`,
    sort: `${headerVariantClasses.sort} ${headerClassNames?.sort ?? ''}`,
    activeSort: `${headerVariantClasses.activeSort} ${
      headerClassNames?.activeSort ?? ''
    }`,
    resize: `${headerVariantClasses.resize} ${headerClassNames?.resize ?? ''}`,
  };
  const loadingRows = Array.from(
    { length: effectiveSkeletonRows },
    (_, index) => index,
  );
  const hasNoData = !isLoading && currentData.length === 0;

  const handlePageChange = (page: number) => {
    if (page < 1 || page > totalPages) return;
    if (paginationMode === 'server') {
      onPageChange?.(page);
      return;
    }
    setInternalCurrentPage(page);
    onPageChange?.(page);
  };

  const handleSort = (key: string) => {
    const nextDirection =
      activeSortKey === key && activeSortOrder === 'asc' ? 'desc' : 'asc';

    if (sortMode === 'server') {
      onSortChange?.({
        key: key as keyof T & string,
        direction: nextDirection,
      });
    } else {
      setInternalSortKey(key);
      setInternalSortOrder(nextDirection);
    }
  };

  const collapseExpandedCell = () => {
    setExpandedCell(null);
  };

  useEffect(() => {
    const handlePointerDown = (event: MouseEvent | TouchEvent) => {
      if (!expandedCell) return;
      const target = event.target as Node | null;
      if (!target) return;
      if (tableContainerRef.current?.contains(target)) return;
      setExpandedCell(null);
    };

    document.addEventListener('mousedown', handlePointerDown);
    document.addEventListener('touchstart', handlePointerDown);

    return () => {
      document.removeEventListener('mousedown', handlePointerDown);
      document.removeEventListener('touchstart', handlePointerDown);
    };
  }, [expandedCell]);

  const getColumnWidthStyle = (
    column: DataGridColumn<T>,
  ): CSSProperties | undefined => {
    const width = getColumnPxWidth(column);
    const maxWidth = parsePixelValue(column.maxWidth);
    return {
      width: `${width}px`,
      minWidth: `${width}px`,
      ...(maxWidth !== undefined ? { maxWidth: `${maxWidth}px` } : undefined),
    };
  };

  const handleApplyFilters = async () => {
    await onAdvancedSearch?.();
    setIsAdvancedSearchOpen(false);
  };

  const handleResetFilters = () => {
    onAdvancedSearchReset?.();
    setIsAdvancedSearchOpen(false);
  };

  const buttonVariantMap: Record<
    'primary' | 'outline' | 'danger',
    'default' | 'outline' | 'destructive'
  > = {
    primary: 'default',
    outline: 'outline',
    danger: 'destructive',
  };

  return (
    <div
      className={
        isFullScreen
          ? 'opsfusion-content-scrollbar fixed inset-0 z-[90] w-full overflow-y-auto bg-card'
          : flat
            ? 'w-full max-w-full'
            : 'w-full max-w-full rounded-xl border border-border bg-card'
      }
    >
      {/* ── Toolbar ── */}
      {
        <div className="flex flex-col gap-4 rounded-t-xl border-b border-border px-4 py-4 md:flex-row md:items-center md:justify-between">
          {/* Left: search + advanced search toggle */}
          <div className="flex min-w-0 flex-col gap-3 sm:flex-row sm:flex-wrap sm:items-center md:flex-1">
            {hasSearch ? (
              <div className="relative w-full min-w-[220px] sm:w-[300px] xl:w-[360px]">
                <span className="pointer-events-none absolute left-4 top-1/2 -translate-y-1/2 text-muted-foreground">
                  <SearchIcon />
                </span>
                <input
                  type="text"
                  value={searchValue}
                  onChange={(e) => onSearchChange?.(e.target.value)}
                  placeholder={searchPlaceholder}
                  className="h-11 w-full rounded-lg border border-input bg-input-background py-2.5 pl-11 pr-4 text-sm text-foreground placeholder:text-muted-foreground outline-none focus:border-ring focus:ring-[3px] focus:ring-ring/20"
                />
              </div>
            ) : null}

            {toolbarFilters ? <>{toolbarFilters}</> : null}

            {hasAdvancedSearch ? (
              <button
                type="button"
                onClick={() => setIsAdvancedSearchOpen(true)}
                className="inline-flex h-11 items-center gap-2 rounded-lg border border-border bg-transparent px-4 text-sm font-medium text-foreground transition hover:border-primary/40 hover:text-primary focus:border-ring focus:outline-none focus:ring-[3px] focus:ring-ring/20"
              >
                <span className="inline-flex size-5 items-center justify-center">
                  <SearchIcon />
                </span>
                <span>{advancedSearchLabel}</span>
                {activeFiltersCount > 0 && (
                  <span className="inline-flex h-6 min-w-6 items-center justify-center rounded-full bg-primary px-1.5 text-[11px] font-semibold text-primary-foreground">
                    {activeFiltersCount}
                  </span>
                )}
              </button>
            ) : null}
          </div>

          {/* Right: refresh + export + custom buttons */}
          <div className="flex shrink-0 flex-nowrap items-center gap-3 overflow-x-auto pb-1 md:justify-end md:pb-0">
            {showColumnOptions ? (
              <DataGridColumnOptions<T>
                columns={columns}
                visibleColumns={visibleColumns}
                onChange={(columnId, visible) =>
                  setVisibleColumns((current) => ({
                    ...current,
                    [columnId]: visible,
                  }))
                }
                onReset={() =>
                  setVisibleColumns(
                    columns.reduce(
                      (visible, column) => {
                        visible[getColumnId(column)] =
                          column.defaultVisible !== false;
                        return visible;
                      },
                      {} as Record<string, boolean>,
                    ),
                  )
                }
                title={columnOptionsLabel}
                resetLabel={columnOptionsResetLabel}
                visibleCountLabel={columnOptionsVisibleCountLabel}
              />
            ) : null}
            {/* No handler = nothing to refresh: hide the button rather than show a dead one. */}
            {showRefresh && onRefresh ? (
              <DataGridRefreshButton
                label={refreshLabel}
                isLoading={isRefreshing}
                onClick={async () => {
                  if (!onRefresh || isRefreshing) return;
                  try {
                    setIsRefreshing(true);
                    // Hold the skeleton briefly so a fast reload reads as a refresh, not a flicker.
                    await Promise.all([
                      onRefresh(),
                      new Promise((resolve) =>
                        setTimeout(resolve, MIN_REFRESH_SKELETON_MS),
                      ),
                    ]);
                  } finally {
                    setIsRefreshing(false);
                  }
                }}
              />
            ) : null}
            {showFullScreen ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <button
                    type="button"
                    onClick={() => setIsFullScreen((current) => !current)}
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
                    aria-label={
                      isFullScreen ? 'Exit full screen' : 'Full screen'
                    }
                    aria-pressed={isFullScreen}
                  >
                    {isFullScreen ? (
                      <Minimize2 className="size-4" />
                    ) : (
                      <Maximize2 className="size-4" />
                    )}
                  </button>
                </TooltipTrigger>
                <TooltipContent>
                  {isFullScreen ? 'Exit full screen' : 'Full screen'}
                </TooltipContent>
              </Tooltip>
            ) : null}
            {showPrint ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <button
                    type="button"
                    onClick={onPrint}
                    aria-label={printLabel}
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
                  >
                    <PrintIcon />
                  </button>
                </TooltipTrigger>
                <TooltipContent>{printLabel}</TooltipContent>
              </Tooltip>
            ) : null}
            {showPdf ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <button
                    type="button"
                    onClick={onPdf}
                    aria-label={pdfLabel}
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition hover:bg-destructive/10 hover:text-destructive"
                  >
                    <PdfIcon className="h-4 w-4" />
                  </button>
                </TooltipTrigger>
                <TooltipContent>{pdfLabel}</TooltipContent>
              </Tooltip>
            ) : null}
            {showExcel ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  <button
                    type="button"
                    onClick={onExcel}
                    aria-label={excelLabel}
                    className="inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition hover:bg-primary/10 hover:text-primary"
                  >
                    <MicrosoftExcelIcon className="h-4 w-4" />
                  </button>
                </TooltipTrigger>
                <TooltipContent>{excelLabel}</TooltipContent>
              </Tooltip>
            ) : null}
            {showExport ? (
              <Tooltip>
                <TooltipTrigger asChild>
                  {/* Was a raw <button> styled with `border-input` — that
                      token resolves to `transparent` in this theme (fine for
                      filled text inputs, but left this looking like a bare
                      label with no visible button shape). Using the shared
                      Button (outline variant, real `border`) instead, same
                      as every other real button in the toolbar. */}
                  <Button
                    type="button"
                    variant="outline"
                    onClick={onExport}
                    aria-label={exportLabel ?? 'Export'}
                    className={cn(
                      'h-11 gap-2',
                      exportLabel ? 'px-3.5' : 'w-11 px-0',
                    )}
                  >
                    <ExportIcon className="h-[18px] w-[18px]" />
                    {exportLabel ? (
                      <span className="whitespace-nowrap">{exportLabel}</span>
                    ) : null}
                  </Button>
                </TooltipTrigger>
                <TooltipContent>{exportLabel ?? 'Export'}</TooltipContent>
              </Tooltip>
            ) : null}
            {toolbarButtons.map((button) =>
              button.iconOnly ? (
                <Tooltip key={button.key}>
                  <TooltipTrigger asChild>
                    <button
                      type="button"
                      onClick={button.onClick}
                      disabled={button.disabled}
                      aria-label={button.label}
                      className={cn(
                        'inline-flex h-8 w-8 items-center justify-center rounded-md text-muted-foreground transition hover:text-primary disabled:cursor-not-allowed disabled:opacity-50',
                        button.className,
                      )}
                    >
                      <span className="inline-flex items-center justify-center">
                        {button.icon}
                      </span>
                    </button>
                  </TooltipTrigger>
                  <TooltipContent>{button.label}</TooltipContent>
                </Tooltip>
              ) : (
                <Tooltip key={button.key}>
                  <TooltipTrigger asChild>
                    <Button
                      onClick={button.onClick}
                      variant={buttonVariantMap[button.variant ?? 'primary']}
                      disabled={button.disabled}
                      className={cn(
                        'h-11 whitespace-nowrap',
                        button.className ?? 'px-4',
                      )}
                      aria-label={button.label}
                    >
                      {button.icon ? (
                        <span className="inline-flex size-[18px] shrink-0 items-center justify-center [&>svg]:size-[18px]">
                          {button.icon}
                        </span>
                      ) : null}
                      {button.label}
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>{button.label}</TooltipContent>
                </Tooltip>
              ),
            )}
          </div>
        </div>
      }

      {/* ── Advanced Search Panel ── */}
      {hasAdvancedSearch && isAdvancedSearchOpen ? (
        <aside className="fixed left-0 top-0 z-[99999] flex h-full w-full max-w-[290px] flex-col border-r border-border bg-popover text-popover-foreground shadow-2xl">
          <div className="flex items-center justify-between gap-2 border-b border-border px-5 py-5">
            <div className="flex min-w-0 items-center gap-2">
              <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-primary/10 text-primary dark:bg-sky-300/10 dark:text-sky-300">
                <SearchIcon />
              </span>
              <h3 className="text-lg font-semibold whitespace-nowrap text-foreground">
                {advancedSearchTitle ?? advancedSearchLabel}
              </h3>
            </div>

            <button
              type="button"
              onClick={() => setIsAdvancedSearchOpen(false)}
              className="inline-flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-muted text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
              aria-label="Close advanced search"
            >
              <CloseXIcon className="h-5 w-5" />
            </button>
          </div>

          {advancedSearchSummaryItems.length > 0 ? (
            <div className="border-b border-border px-5 py-4">
              <p className="mb-3 text-xs font-semibold uppercase tracking-[0.18em] text-muted-foreground">
                Applied Filters
              </p>
              <div className="flex flex-wrap gap-2">
                {advancedSearchSummaryItems.map((item) => (
                  <span
                    key={`${item.label}-${item.value}`}
                    className="inline-flex items-center gap-1 rounded-full border border-primary/20 bg-primary/10 px-3 py-1 text-xs font-medium text-primary"
                  >
                    <span className="font-semibold">{item.label}:</span>
                    <span>{item.value}</span>
                  </span>
                ))}
              </div>
            </div>
          ) : null}

          <div className="opsfusion-content-scrollbar flex-1 overflow-y-auto px-5 py-5">
            {!advancedSearchFields || advancedSearchFields.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                {advancedSearchEmptyLabel}
              </p>
            ) : (
              <div className="grid grid-cols-1 gap-4">
                {advancedSearchFields!.map((field) => (
                  <DataGridAdvancedSearchFieldRow
                    key={field.key}
                    field={field}
                  />
                ))}
              </div>
            )}
          </div>

          <div className="border-t border-border px-5 py-4">
            <div className="flex flex-col-reverse gap-3 sm:flex-row sm:items-center sm:justify-end">
              <Button
                variant="outline"
                className="h-9 px-4"
                onClick={handleResetFilters}
              >
                {resetFiltersLabel}
              </Button>
              <Button
                className="h-9 px-4"
                onClick={() => void handleApplyFilters()}
              >
                {applyFiltersLabel}
              </Button>
            </div>
          </div>
        </aside>
      ) : null}

      {/* ── Table ──
          Horizontal scroll only — the grid always renders every row in full
          and grows the page, instead of clipping rows into an internal
          vertical scroll box. */}
      <div
        ref={tableContainerRef}
        className="opsfusion-content-scrollbar w-full max-w-full overflow-x-auto overflow-y-visible"
        onClick={collapseExpandedCell}
      >
        <div style={{ width: `${effectiveTableWidth}px` }}>
          <table
            className="table-fixed border-separate border-spacing-0"
            style={{ width: `${effectiveTableWidth}px` }}
          >
            {/*
              A <colgroup> authoritatively fixes each column's width under
              table-layout: fixed. Without it, browsers derive column widths
              from the header row's cell styles, which is fragile once
              sticky/frozen cells are mixed in — resizing one column could
              make the browser reflow and shrink an unrelated column instead
              of just growing the table.
            */}
            <colgroup>
              {visibleTableColumns.map((column) => (
                <col
                  key={getColumnId(column)}
                  style={{ width: `${getColumnPxWidth(column)}px` }}
                />
              ))}
            </colgroup>
            <thead className={`sticky -top-px z-20 ${headerClasses.thead}`}>
              <tr>
                {visibleTableColumns.map((column, index) => {
                  const columnId = getColumnId(column);
                  return (
                    <th
                      key={columnId}
                      draggable={canReorderColumns}
                      onDragStart={(event) =>
                        handleColumnDragStart(event, columnId)
                      }
                      onDragOver={(event) => {
                        if (canReorderColumns) event.preventDefault();
                      }}
                      onDrop={(event) => handleColumnDrop(event, columnId)}
                      onDragEnd={() => setDraggedColumnId(null)}
                      style={{
                        ...getColumnWidthStyle(column),
                        ...getStickyCellStyle(index),
                      }}
                      className={`relative overflow-hidden border px-3 py-3 text-left align-middle ${headerClasses.cell} ${getStickyClassName(index, true)} ${
                        draggedColumnId === columnId ? 'opacity-60' : ''
                      } ${canReorderColumns ? 'cursor-grab active:cursor-grabbing' : ''}`}
                    >
                      <div
                        className="flex items-center justify-between gap-2"
                        onClick={() =>
                          column.sortable === true && handleSort(column.key)
                        }
                      >
                        <p
                          className={`truncate text-[11px] font-bold uppercase tracking-wider ${headerClasses.label}`}
                        >
                          {column.label}
                        </p>
                        {column.sortable === true ? (
                          <button
                            type="button"
                            className={`flex flex-col gap-0.5 transition-colors ${headerClasses.sort}`}
                            aria-label={sortByLabel(column.label)}
                          >
                            <AngleUpIcon
                              className={`${activeSortKey === column.key && activeSortOrder === 'asc' ? headerClasses.activeSort : ''}`}
                            />
                            <AngleDownIcon
                              className={`${activeSortKey === column.key && activeSortOrder === 'desc' ? headerClasses.activeSort : ''}`}
                            />
                          </button>
                        ) : null}
                      </div>
                      {canResizeColumns && (
                        <button
                          type="button"
                          aria-label={resizeColumnLabel(column.label)}
                          onMouseDown={(event) =>
                            handleColumnResizeStart(event, columnId)
                          }
                          className={`absolute right-0 top-0 z-10 h-full w-2 cursor-col-resize touch-none border-r-2 border-transparent transition ${headerClasses.resize}`}
                        />
                      )}
                    </th>
                  );
                })}
              </tr>
            </thead>
            <tbody>
              {isLoading ? (
                loadingRows.map((index) => (
                  <tr key={`skeleton-row-${index}`}>
                    {visibleTableColumns.map((column, columnIndex) => (
                      <td
                        key={`${getColumnId(column)}-skeleton-${columnIndex}`}
                        className="border border-border px-3 py-2.5 whitespace-nowrap"
                      >
                        <div className="animate-pulse">
                          <div
                            className={`h-3.5 rounded-full bg-muted ${
                              columnIndex === 0
                                ? 'w-3/4'
                                : columnIndex === visibleTableColumns.length - 1
                                  ? 'w-1/2'
                                  : 'w-2/3'
                            }`}
                          />
                        </div>
                      </td>
                    ))}
                  </tr>
                ))
              ) : hasNoData ? (
                <tr>
                  <td
                    colSpan={visibleTableColumns.length}
                    className="border border-border px-4 py-5 text-center text-sm text-muted-foreground"
                  >
                    {noRecordsLabel}
                  </td>
                </tr>
              ) : (
                currentData.map((row, rowIndex) => {
                  const resolvedRowKey = rowKey(row);
                  const rowId = String(resolvedRowKey);

                  return (
                    <tr
                      key={`${rowId}-${startIndex + rowIndex}`}
                      className="group transition-colors hover:bg-accent/40"
                    >
                      {visibleTableColumns.map((column, columnIndex) => {
                        const columnId = getColumnId(column);
                        const cellKey = `${rowId}::${columnId}`;
                        const isExpanded = expandedCell === cellKey;
                        const rawValue = String(row[column.key] ?? '');
                        const limit = column.textLimit;

                        const buildLimitedContent = (
                          text: string,
                        ): ReactNode =>
                          isExpanded ? (
                            <span className="flex flex-col items-start gap-1">
                              <span className="break-words">{text}</span>
                              <button
                                type="button"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  toggleCell(rowId, column.key);
                                }}
                                className="text-xs font-medium text-primary hover:text-primary/80"
                              >
                                Show less
                              </button>
                            </span>
                          ) : (
                            <>
                              <span>{text.slice(0, limit) + '…'}</span>
                              <button
                                type="button"
                                onClick={(e) => {
                                  e.stopPropagation();
                                  toggleCell(rowId, column.key);
                                }}
                                className="ml-1.5 whitespace-nowrap text-xs font-medium text-primary hover:text-primary/80"
                              >
                                Show more
                              </button>
                            </>
                          );

                        let cellContent: ReactNode;
                        if (column.render) {
                          const rendered = column.render(row);
                          if (
                            typeof rendered === 'string' &&
                            limit !== undefined &&
                            rendered.length > limit
                          ) {
                            cellContent = buildLimitedContent(rendered);
                          } else {
                            cellContent = rendered;
                          }
                        } else if (
                          limit !== undefined &&
                          rawValue.length > limit
                        ) {
                          cellContent = buildLimitedContent(rawValue);
                        } else {
                          cellContent = rawValue;
                        }

                        if (
                          cellContent === '' ||
                          cellContent === null ||
                          cellContent === undefined
                        ) {
                          cellContent = column.emptyValue ?? emptyValueLabel;
                        }

                        return (
                          <td
                            key={columnId}
                            style={{
                              ...getColumnWidthStyle(column),
                              ...getStickyCellStyle(columnIndex),
                            }}
                            className={`overflow-hidden border border-border bg-card px-3 py-2.5 text-sm font-normal text-foreground group-hover:bg-accent/40 ${getStickyClassName(columnIndex, false)} ${
                              isExpanded
                                ? 'whitespace-normal'
                                : 'overflow-hidden text-ellipsis whitespace-nowrap'
                            } ${column.className ?? ''}`}
                          >
                            {column.copyable ? (
                              <span className="inline-flex items-start">
                                <span>{cellContent}</span>
                                <CopyIconButton value={rawValue} />
                              </span>
                            ) : (
                              cellContent
                            )}
                          </td>
                        );
                      })}
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>

      {/* ── Footer: page size + pagination ── */}
      {showPagination ? (
        <div className="border-t border-border px-4 py-4 sm:py-3">
          <div className="flex min-w-0 flex-wrap items-center justify-center gap-2 md:gap-3 md:justify-between">
            <div className="flex min-w-0 flex-wrap items-center justify-center gap-2 md:gap-3">
              <p className="shrink-0 text-center text-xs font-medium text-muted-foreground md:text-left md:text-sm">
                {isLoading ? (
                  loadingEntriesLabel
                ) : (
                  <>
                    <span className="md:hidden">
                      Showing {totalItems === 0 ? 0 : startIndex + 1}-{endIndex}{' '}
                      of {totalItems} total
                    </span>
                    <span className="hidden md:inline">
                      {showingEntriesLabel({
                        start: totalItems === 0 ? 0 : startIndex + 1,
                        end: endIndex,
                        total: totalItems,
                      })}
                    </span>
                  </>
                )}
              </p>
              {showPageSizeSelector ? (
                <div className="flex items-center justify-center gap-1.5 md:gap-2">
                  <span className="text-[11px] font-medium text-muted-foreground md:hidden">
                    Rows
                  </span>
                  <span className="hidden text-sm font-medium text-muted-foreground md:inline">
                    {pageSizePrefixLabel}
                  </span>
                  <Select
                    aria-label="Rows per page"
                    value={String(itemsPerPage)}
                    clearable={false}
                    options={pageSizeOptions.map((option) => ({
                      value: String(option),
                      label: String(option),
                    }))}
                    onChange={(next) => {
                      const value = Number(next);
                      if (paginationMode === 'server') {
                        onPageChange?.(1);
                        onPageSizeChange?.(value);
                        return;
                      }
                      setInternalItemsPerPage(value);
                      setInternalCurrentPage(1);
                      onPageSizeChange?.(value);
                    }}
                    className="w-auto"
                    triggerClassName="h-8 min-w-[64px] rounded-lg bg-card px-2.5 text-sm"
                  />
                  <span className="hidden text-sm font-medium text-muted-foreground md:inline">
                    {pageSizeSuffixLabel}
                  </span>
                </div>
              ) : null}
            </div>
            <div className="flex w-auto justify-center md:justify-end">
              <DataGridPagination
                totalPages={totalPages}
                initialPage={currentPage}
                currentPage={currentPage}
                onPageChange={handlePageChange}
                variant={paginationVariant}
                className="justify-center md:justify-end"
              />
            </div>
          </div>
        </div>
      ) : null}
    </div>
  );
}