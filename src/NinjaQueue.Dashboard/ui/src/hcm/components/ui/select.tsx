'use client';

import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { ChevronDown, Search, X } from 'lucide-react';

import { cn } from '../../utils';
import { getFloatingPortalTarget, useFloatingPosition } from '../../hooks/use-floating-position';

export type SelectOption = {
  value: string;
  label: string;
  disabled?: boolean;
};

export type SelectProps = {
  value: string;
  options: SelectOption[];
  onChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  id?: string;
  className?: string;
  triggerClassName?: string;
  menuClassName?: string;
  'aria-label'?: string;
  /** Adds a filter box above the option list. Off by default — pass per-page where the option list is long enough to need it. */
  searchable?: boolean;
  searchPlaceholder?: string;
  /**
   * Optional: notified whenever the search text changes (and with '' when the menu closes), so a page can
   * run a server-side search and feed the results back in via `options`. Local label filtering still
   * applies on top. Only used when `searchable` is on.
   */
  onSearchChange?: (search: string) => void;
  /** Text shown when no option matches. Defaults to "No matches" - e.g. pass "Searching..." while a server search is in flight. */
  noMatchesText?: string;
  /** Shows an "x" button to reset the value back to empty when one is selected. On by default across the app — pass `false` to opt a specific select out. */
  clearable?: boolean;
};

/**
 * Themed replacement for a bare `<select>` — browsers render native selects
 * (and their dropdown popup) with OS chrome that ignores our design tokens,
 * which is why they look out of place next to Input/Button/DataGrid. Built
 * on plain state + outside-click/escape handling (no @radix-ui/react-select
 * dependency yet) rather than a native <select>, mirroring the pattern
 * ninja-web's PageSizeSelect already used — re-themed here on OpsFusion's
 * border-input/bg-input-background/bg-popover tokens instead of ninja's
 * hardcoded gray and brand color classes.
 */
export function Select({
  value,
  options,
  onChange,
  placeholder = 'Select...',
  disabled = false,
  id,
  className,
  triggerClassName,
  menuClassName,
  'aria-label': ariaLabel,
  searchable = false,
  searchPlaceholder = 'Search...',
  onSearchChange,
  noMatchesText = 'No matches',
  clearable = true,
}: SelectProps) {
  const generatedId = useId();
  const listboxId = id ?? generatedId;
  const rootRef = useRef<HTMLDivElement | null>(null);
  const menuRef = useRef<HTMLDivElement | null>(null);
  const searchInputRef = useRef<HTMLInputElement | null>(null);
  const [isOpen, setIsOpen] = useState(false);
  const [search, setSearch] = useState('');
  // 260px ≈ max dropdown height (max-h-60 = 240px + border/shadow)
  const position = useFloatingPosition(rootRef, isOpen, 260);

  useEffect(() => {
    if (!isOpen) return;

    const handleOutsideClick = (event: MouseEvent) => {
      if (!(event.target instanceof Node)) return;
      // The menu is portaled to document.body (see the `createPortal` call below) so it's outside
      // rootRef's own DOM subtree - a click inside it must not count as "outside", or picking an
      // option would close the dropdown on mousedown before the option's own onClick ever fires.
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

  useEffect(() => {
    if (!isOpen) {
      setSearch('');
      onSearchChange?.('');
      return;
    }
    if (searchable) searchInputRef.current?.focus();
    // onSearchChange is a caller callback - intentionally not a dependency, so an inline arrow doesn't
    // re-run this on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isOpen, searchable]);

  const selectedOption = useMemo(() => options.find((option) => option.value === value), [options, value]);
  const filteredOptions = useMemo(() => {
    if (!searchable || !search.trim()) return options;
    const query = search.trim().toLowerCase();
    return options.filter((option) => option.label.toLowerCase().includes(query));
  }, [options, search, searchable]);

  return (
    // min-w-0: as a grid/flex item the root would otherwise size to the full label's width (auto min-size),
    // so a long selected label would widen its container instead of truncating inside the trigger.
    <div ref={rootRef} className={cn('relative min-w-0', className)}>
      <button
        id={id}
        type="button"
        aria-haspopup="listbox"
        aria-expanded={isOpen}
        aria-controls={`${listboxId}-listbox`}
        aria-label={ariaLabel}
        disabled={disabled}
        onClick={() => setIsOpen((current) => !current)}
        className={cn(
          'border-input dark:bg-input/30 flex h-9 w-full min-w-0 items-center justify-between gap-2 rounded-md border bg-input-background px-3 py-1 text-left text-base outline-none transition-[color,box-shadow] focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-50 md:text-sm',
          triggerClassName,
        )}
      >
        {/* min-w-0 + flex-1: a flex child won't shrink below its content width without it, so a long label
            would overflow the trigger (and push the clear/chevron icons out) instead of truncating. */}
        <span
          className={cn('min-w-0 flex-1 truncate', !selectedOption && 'text-muted-foreground')}
          title={selectedOption?.label}
        >
          {selectedOption?.label ?? placeholder}
        </span>
        <span className="flex shrink-0 items-center gap-1">
          {clearable && selectedOption && !disabled ? (
            <span
              role="button"
              tabIndex={0}
              aria-label="Clear selection"
              onClick={(event) => {
                event.stopPropagation();
                onChange('');
              }}
              onKeyDown={(event) => {
                if (event.key === 'Enter' || event.key === ' ') {
                  event.preventDefault();
                  event.stopPropagation();
                  onChange('');
                }
              }}
              className="rounded-full p-0.5 text-muted-foreground transition hover:bg-accent hover:text-accent-foreground"
            >
              <X className="size-3.5" aria-hidden="true" />
            </span>
          ) : null}
          <ChevronDown
            className={cn('size-4 self-center text-muted-foreground transition-transform', isOpen && 'rotate-180')}
            aria-hidden="true"
          />
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
              style={{ position: 'fixed', top: position.top ?? undefined, bottom: position.bottom ?? undefined, left: position.left, width: position.width, pointerEvents: 'auto' }}
              className="z-[130]"
            >
              <div
                className={cn(
                  'w-full overflow-hidden rounded-md border border-border bg-popover text-popover-foreground shadow-md',
                  menuClassName,
                )}
              >
                {searchable ? (
                  <div className="flex items-center gap-2 border-b border-border px-2.5 py-1.5">
                    <Search className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
                    <input
                      ref={searchInputRef}
                      type="text"
                      value={search}
                      onChange={(event) => {
                        setSearch(event.target.value);
                        onSearchChange?.(event.target.value);
                      }}
                      onKeyDown={(event) => event.stopPropagation()}
                      placeholder={searchPlaceholder}
                      className="h-6 w-full min-w-0 bg-transparent text-sm text-foreground outline-none placeholder:text-muted-foreground"
                    />
                  </div>
                ) : null}
                <div
                  id={`${listboxId}-listbox`}
                  role="listbox"
                  aria-label={ariaLabel}
                  className="opsfusion-content-scrollbar max-h-60 w-full overflow-auto p-1"
                >
                  {filteredOptions.length === 0 ? (
                    <p className="px-2 py-1.5 text-sm text-muted-foreground">{noMatchesText}</p>
                  ) : null}
                  {filteredOptions.map((option) => {
                    const isSelected = option.value === value;
                    return (
                      <button
                        key={option.value}
                        type="button"
                        role="option"
                        aria-selected={isSelected}
                        disabled={option.disabled}
                        onClick={() => {
                          onChange(option.value);
                          setIsOpen(false);
                        }}
                        className={cn(
                          'flex w-full items-center justify-between rounded-sm px-2 py-1.5 text-left text-sm transition disabled:pointer-events-none disabled:opacity-50',
                          isSelected
                            ? 'bg-primary text-primary-foreground'
                            : 'text-foreground hover:bg-accent hover:text-accent-foreground',
                        )}
                      >
                        <span className="truncate">{option.label}</span>
                      </button>
                    );
                  })}
                </div>
              </div>
            </div>,
            getFloatingPortalTarget(rootRef.current),
          )
        : null}
    </div>
  );
}