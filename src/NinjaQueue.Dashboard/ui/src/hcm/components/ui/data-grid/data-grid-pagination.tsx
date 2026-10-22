'use client';

import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';

import { cn } from '../../../utils';

export type DataGridPaginationProps = {
  totalPages: number;
  initialPage?: number;
  currentPage?: number;
  onPageChange?: (page: number) => void;
  variant?: 'text' | 'text-icon' | 'icon';
  className?: string;
};

function PrevIcon() {
  return (
    <svg
      className="fill-current"
      width="20"
      height="20"
      viewBox="0 0 20 20"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        clipRule="evenodd"
        d="M2.58203 9.99868C2.58174 10.1909 2.6549 10.3833 2.80152 10.53L7.79818 15.5301C8.09097 15.8231 8.56584 15.8233 8.85883 15.5305C9.15183 15.2377 9.152 14.7629 8.85921 14.4699L5.13911 10.7472L16.6665 10.7472C17.0807 10.7472 17.4165 10.4114 17.4165 9.99715C17.4165 9.58294 17.0807 9.24715 16.6665 9.24715L5.14456 9.24715L8.85919 5.53016C9.15199 5.23717 9.15184 4.7623 8.85885 4.4695C8.56587 4.1767 8.09099 4.17685 7.79819 4.46984L2.84069 9.43049C2.68224 9.568 2.58203 9.77087 2.58203 9.99715C2.58203 9.99766 2.58203 9.99817 2.58203 9.99868Z"
        fill=""
      />
    </svg>
  );
}

function NextIcon() {
  return (
    <svg
      className="fill-current"
      width="20"
      height="20"
      viewBox="0 0 20 20"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
    >
      <path
        fillRule="evenodd"
        clipRule="evenodd"
        d="M17.4165 9.9986C17.4168 10.1909 17.3437 10.3832 17.197 10.53L12.2004 15.5301C11.9076 15.8231 11.4327 15.8233 11.1397 15.5305C10.8467 15.2377 10.8465 14.7629 11.1393 14.4699L14.8594 10.7472L3.33203 10.7472C2.91782 10.7472 2.58203 10.4114 2.58203 9.99715C2.58203 9.58294 2.91782 9.24715 3.33203 9.24715L14.854 9.24715L11.1393 5.53016C10.8465 5.23717 10.8467 4.7623 11.1397 4.4695C11.4327 4.1767 11.9075 4.17685 12.2003 4.46984L17.1578 9.43049C17.3163 9.568 17.4165 9.77087 17.4165 9.99715C17.4165 9.99763 17.4165 9.99812 17.4165 9.9986Z"
        fill=""
      />
    </svg>
  );
}

/**
 * Re-themed port of ninja-web's `Pagination` — same page/ellipsis/mobile
 * logic, but built on OpsFusion's `bg-primary`/`border-border`/`bg-accent`
 * tokens instead of ninja-theme's `app-btn`/`brand-*` classes so it
 * re-themes with the active palette automatically.
 */
export function DataGridPagination({
  totalPages,
  initialPage = 1,
  currentPage: controlledCurrentPage,
  onPageChange,
  variant = 'icon',
  className = '',
}: DataGridPaginationProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const [internalCurrentPage, setInternalCurrentPage] = useState(initialPage);
  const [isRtl, setIsRtl] = useState(false);
  const currentPage = controlledCurrentPage ?? internalCurrentPage;

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const closestDirElement = container.closest('[dir]');
    const direction =
      closestDirElement?.getAttribute('dir') ??
      document.documentElement.getAttribute('dir') ??
      'ltr';

    setIsRtl(direction.toLowerCase() === 'rtl');
  }, []);

  const handlePageChange = useCallback(
    (page: number) => {
      if (page < 1 || page > totalPages) return;
      if (controlledCurrentPage == null) {
        setInternalCurrentPage(page);
      }
      onPageChange?.(page);
    },
    [controlledCurrentPage, onPageChange, totalPages],
  );

  const renderPageNumbers = useMemo(() => {
    const pageNumbers: ReactNode[] = [];
    const maxVisiblePages = 7;

    const renderPageButton = (
      page: number,
      visibility: 'mobile' | 'desktop' | 'responsive' = 'responsive',
    ) => (
      <li
        key={`${visibility}-${page}`}
        className={
          visibility === 'mobile'
            ? 'md:hidden'
            : visibility === 'desktop'
              ? 'hidden md:list-item'
              : currentPage === page
                ? undefined
                : 'hidden md:list-item'
        }
      >
        <button
          type="button"
          onClick={() => handlePageChange(page)}
          className={cn(
            'inline-flex h-9 min-w-9 items-center justify-center rounded-lg border px-2 text-sm font-medium transition',
            currentPage === page
              ? 'border-primary bg-primary text-primary-foreground hover:bg-primary/90'
              : 'border-border bg-background text-foreground hover:bg-accent hover:text-accent-foreground',
          )}
        >
          {page}
        </button>
      </li>
    );

    const renderEllipsis = (key: string) => (
      <li key={key} className="hidden md:list-item">
        <span className="flex h-9 w-9 items-center justify-center text-sm font-medium text-muted-foreground">
          ...
        </span>
      </li>
    );

    const renderMobileEllipsis = (key: string) => (
      <li key={key} className="md:hidden">
        <span className="flex h-9 min-w-5 items-center justify-center text-sm font-medium text-muted-foreground">
          ...
        </span>
      </li>
    );

    if (totalPages <= 1) {
      return [renderPageButton(1, 'responsive')];
    }

    const mobilePageNumbers: ReactNode[] = [];
    mobilePageNumbers.push(renderPageButton(1, 'mobile'));
    if (currentPage > 2) {
      mobilePageNumbers.push(renderMobileEllipsis('mobile-ellipsis-start'));
    }
    if (currentPage !== 1 && currentPage !== totalPages) {
      mobilePageNumbers.push(renderPageButton(currentPage, 'mobile'));
    }
    if (currentPage < totalPages - 1) {
      mobilePageNumbers.push(renderMobileEllipsis('mobile-ellipsis-end'));
    }
    mobilePageNumbers.push(renderPageButton(totalPages, 'mobile'));

    if (totalPages <= maxVisiblePages) {
      for (let i = 1; i <= totalPages; i += 1) {
        pageNumbers.push(renderPageButton(i, 'desktop'));
      }
      return [...mobilePageNumbers, ...pageNumbers];
    }

    pageNumbers.push(renderPageButton(1, 'desktop'));
    if (currentPage > 3) pageNumbers.push(renderEllipsis('ellipsis-start'));

    let start = Math.max(2, currentPage - 1);
    let end = Math.min(totalPages - 1, currentPage + 1);
    if (currentPage <= 3) end = 5;
    if (currentPage >= totalPages - 2) start = totalPages - 4;

    for (let i = start; i <= end; i += 1) {
      pageNumbers.push(renderPageButton(i, 'desktop'));
    }

    if (currentPage < totalPages - 2)
      pageNumbers.push(renderEllipsis('ellipsis-end'));
    pageNumbers.push(renderPageButton(totalPages, 'desktop'));

    return [...mobilePageNumbers, ...pageNumbers];
  }, [currentPage, totalPages, handlePageChange]);

  const buttonBase =
    'inline-flex items-center justify-center rounded-lg border border-border bg-background text-foreground transition hover:bg-accent hover:text-accent-foreground disabled:cursor-not-allowed disabled:opacity-50';

  const showCompactLabel = variant === 'text';
  const showButtonText = variant !== 'icon';

  return (
    <div
      ref={containerRef}
      className={cn(
        'flex w-full max-w-full flex-wrap items-center gap-2 md:gap-3',
        className,
      )}
    >
      <button
        type="button"
        onClick={() => handlePageChange(currentPage - 1)}
        disabled={currentPage === 1}
        className={cn(buttonBase, 'h-9 gap-2 px-2.5 sm:px-3.5')}
      >
        {showButtonText ? (
          <>
            <span className="inline md:hidden">
              {isRtl ? <NextIcon /> : <PrevIcon />}
            </span>
            <span className="hidden md:inline">Previous</span>
          </>
        ) : (
          <span className={isRtl ? 'rotate-180' : ''}>
            <PrevIcon />
          </span>
        )}
      </button>

      {showCompactLabel ? (
        <span className="block text-sm font-medium text-muted-foreground md:hidden">
          Page {currentPage} of {totalPages}
        </span>
      ) : null}

      <ul className="flex max-w-full flex-wrap items-center gap-0.5 md:gap-1">
        {renderPageNumbers}
      </ul>

      <button
        type="button"
        onClick={() => handlePageChange(currentPage + 1)}
        disabled={currentPage === totalPages}
        className={cn(buttonBase, 'h-9 gap-2 px-2.5 sm:px-3.5')}
      >
        {showButtonText ? (
          <>
            <span className="hidden md:inline">Next</span>
            <span className="inline md:hidden">
              {isRtl ? <PrevIcon /> : <NextIcon />}
            </span>
          </>
        ) : (
          <span className={isRtl ? 'rotate-180' : ''}>
            <NextIcon />
          </span>
        )}
      </button>
    </div>
  );
}