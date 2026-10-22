'use client';

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, CheckCircle2, Info, X, XCircle, type LucideIcon } from 'lucide-react';

import { cn } from '../../utils';

/**
 * Inspired by ninja-web's `Notification` toast (libs/ninja-web/src/components/ui/Notification.tsx)
 * but rebuilt on OpsFusion's own tokens instead of TailAdmin's hardcoded
 * `success-500`/`warning-500`/`error-500`/`blue-light-500` palette — same
 * variant collapse used by opsfusion-web's `AlertModal`:
 *   danger  -> destructive
 *   success -> primary
 *   info    -> secondary
 *   warning -> accent
 *
 * This is a queue + portal + timers, not a copy of ninja-web's single-toast
 * component — multiple toasts can stack, each with its own auto-dismiss.
 */
export type ToastVariant = 'success' | 'info' | 'warning' | 'danger';

export type ToastPosition =
  | 'top-left'
  | 'top-center'
  | 'top-right'
  | 'bottom-left'
  | 'bottom-center'
  | 'bottom-right';

export type ToastOptions = {
  title: string;
  description?: string;
  variant?: ToastVariant;
  /** ms before auto-dismiss; pass 0 to require manual dismissal. Default 4000. */
  duration?: number;
  /** Shows a seconds-remaining label plus a shrinking progress bar along the bottom edge. Default `false`. No-op when `duration` is 0. */
  showCountdown?: boolean;
  /** An optional link-style button under the description (e.g. "Open job"); clicking it also dismisses the toast. */
  action?: ToastAction;
};

export type ToastAction = {
  label: string;
  onClick: () => void;
};

type ToastRecord = Required<Pick<ToastOptions, 'title' | 'variant' | 'duration' | 'showCountdown'>> &
  Pick<ToastOptions, 'description' | 'action'> & { id: string };

type ToastContextValue = {
  toast: (options: ToastOptions) => string;
  dismiss: (id: string) => void;
};

const ToastContext = createContext<ToastContextValue | null>(null);

const DEFAULT_DURATION = 4000;

// Base placement (top/bottom + edge) per position. Horizontal centering
// beyond `sm:` uses a translate since `items-center` alone can't center a
// `fixed` container that isn't full-width.
const POSITION_VIEWPORT_CLASSES: Record<ToastPosition, string> = {
  'top-left': 'top-4 sm:top-6 sm:left-6 sm:right-auto sm:items-start',
  'top-center': 'top-4 sm:top-6 sm:left-1/2 sm:right-auto sm:-translate-x-1/2 sm:items-center',
  'top-right': 'top-4 sm:top-6 sm:right-6 sm:items-end',
  'bottom-left': 'bottom-4 sm:bottom-6 sm:left-6 sm:right-auto sm:items-start',
  'bottom-center': 'bottom-4 sm:bottom-6 sm:left-1/2 sm:right-auto sm:-translate-x-1/2 sm:items-center',
  'bottom-right': 'bottom-4 sm:bottom-6 sm:right-6 sm:items-end',
};

// Slide direction matches the edge the viewport is anchored to.
const POSITION_ANIMATION_CLASSES: Record<ToastPosition, string> = {
  'top-left': 'slide-in-from-top-2 sm:slide-in-from-left-4',
  'top-center': 'slide-in-from-top-2',
  'top-right': 'slide-in-from-top-2 sm:slide-in-from-right-4',
  'bottom-left': 'slide-in-from-bottom-2 sm:slide-in-from-left-4',
  'bottom-center': 'slide-in-from-bottom-2',
  'bottom-right': 'slide-in-from-bottom-2 sm:slide-in-from-right-4',
};

// Same success/error/warning/info hex pairs as opsfusion-web's `Badge` (see
// badge.tsx) — `--primary`/`--accent`/`--secondary` are brand/UI tokens, not
// semantic ones, and are too dark to stay visible against the dark-mode card
// background, so semantic feedback colors are hardcoded (with dark: variants)
// instead of derived from the theme palette.
const VARIANT_STYLES: Record<
  ToastVariant,
  { icon: LucideIcon; iconWrapClassName: string; accentClassName: string; progressClassName: string }
> = {
  success: {
    icon: CheckCircle2,
    iconWrapClassName: 'bg-[#ecfdf3] text-[#039855] dark:bg-[#12b76a]/15 dark:text-[#12b76a]',
    accentClassName: 'before:bg-[#039855] dark:before:bg-[#12b76a]',
    progressClassName: 'bg-[#039855] dark:bg-[#12b76a]',
  },
  info: {
    icon: Info,
    iconWrapClassName: 'bg-[#f0f9ff] text-[#0ba5ec] dark:bg-[#0ba5ec]/15 dark:text-[#0ba5ec]',
    accentClassName: 'before:bg-[#0ba5ec]',
    progressClassName: 'bg-[#0ba5ec]',
  },
  warning: {
    icon: AlertTriangle,
    iconWrapClassName: 'bg-[#fffaeb] text-[#dc6803] dark:bg-[#f79009]/15 dark:text-[#fd853a]',
    accentClassName: 'before:bg-[#dc6803] dark:before:bg-[#fd853a]',
    progressClassName: 'bg-[#dc6803] dark:bg-[#fd853a]',
  },
  danger: {
    icon: XCircle,
    iconWrapClassName: 'bg-[#fef3f2] text-[#d92d20] dark:bg-[#f04438]/15 dark:text-[#f04438]',
    accentClassName: 'before:bg-[#d92d20] dark:before:bg-[#f04438]',
    progressClassName: 'bg-[#d92d20] dark:bg-[#f04438]',
  },
};

export function ToastProvider({
  children,
  position = 'top-right',
}: {
  children: ReactNode;
  /** Where toasts render on screen. Default `top-right`. */
  position?: ToastPosition;
}) {
  const [toasts, setToasts] = useState<ToastRecord[]>([]);

  const dismiss = useCallback((id: string) => {
    setToasts((prev) => prev.filter((item) => item.id !== id));
  }, []);

  const toast = useCallback((options: ToastOptions) => {
    const id = crypto.randomUUID();
    setToasts((prev) => [
      ...prev,
      {
        id,
        variant: options.variant ?? 'info',
        duration: options.duration ?? DEFAULT_DURATION,
        showCountdown: options.showCountdown ?? true,
        title: options.title,
        description: options.description,
        action: options.action,
      },
    ]);
    return id;
  }, []);

  const value = useMemo(() => ({ toast, dismiss }), [toast, dismiss]);

  return (
    <ToastContext.Provider value={value}>
      {children}
      <ToastViewport toasts={toasts} onDismiss={dismiss} position={position} />
    </ToastContext.Provider>
  );
}

export function useToast() {
  const ctx = useContext(ToastContext);
  if (!ctx) {
    throw new Error('useToast must be used within a <ToastProvider>');
  }
  return ctx;
}

function ToastViewport({
  toasts,
  onDismiss,
  position,
}: {
  toasts: ToastRecord[];
  onDismiss: (id: string) => void;
  position: ToastPosition;
}) {
  const [mounted, setMounted] = useState(false);
  useEffect(() => setMounted(true), []);

  if (!mounted || toasts.length === 0) return null;

  return createPortal(
    <div
      className={cn(
        'pointer-events-none fixed inset-x-4 z-[200] flex flex-col items-stretch gap-2 sm:inset-x-auto',
        POSITION_VIEWPORT_CLASSES[position],
      )}
    >
      {toasts.map((item) => (
        <ToastItem key={item.id} toast={item} position={position} onDismiss={onDismiss} />
      ))}
    </div>,
    document.body,
  );
}

function ToastItem({
  toast,
  position,
  onDismiss,
}: {
  toast: ToastRecord;
  position: ToastPosition;
  onDismiss: (id: string) => void;
}) {
  const { id, title, description, action, variant, duration, showCountdown } = toast;
  const { icon: Icon, iconWrapClassName, accentClassName, progressClassName } = VARIANT_STYLES[variant];

  const [remainingMs, setRemainingMs] = useState(duration);
  const [barExpired, setBarExpired] = useState(false);

  useEffect(() => {
    if (!duration) return;
    const timer = window.setTimeout(() => onDismiss(id), duration);
    return () => window.clearTimeout(timer);
  }, [id, duration, onDismiss]);

  useEffect(() => {
    if (!duration || !showCountdown) return;
    const start = Date.now();
    const interval = window.setInterval(() => {
      setRemainingMs(Math.max(duration - (Date.now() - start), 0));
    }, 250);
    // Starts the bar's CSS width transition on the next frame, after the
    // "100% width" initial paint has committed — flipping it in the same
    // tick would skip straight to 0% with no visible transition.
    const raf = requestAnimationFrame(() => setBarExpired(true));
    return () => {
      window.clearInterval(interval);
      cancelAnimationFrame(raf);
    };
  }, [duration, showCountdown]);

  const secondsLeft = Math.max(Math.ceil(remainingMs / 1000), 0);

  return (
    <div
      role="status"
      className={cn(
        "pointer-events-auto relative flex w-full max-w-sm items-start gap-3 overflow-hidden rounded-xl border border-border bg-card p-4 pr-3 text-foreground shadow-lg animate-in fade-in-0 before:absolute before:inset-y-0 before:left-0 before:w-1 before:content-['']",
        POSITION_ANIMATION_CLASSES[position],
        accentClassName,
      )}
    >
      <span className={cn('flex size-9 shrink-0 items-center justify-center rounded-full', iconWrapClassName)}>
        <Icon className="size-4" />
      </span>
      <div className="min-w-0 flex-1 pt-0.5">
        <p className="text-sm font-semibold text-foreground">{title}</p>
        {description ? <p className="mt-0.5 text-sm text-muted-foreground">{description}</p> : null}
        {action ? (
          <button
            type="button"
            onClick={() => {
              action.onClick();
              onDismiss(id);
            }}
            className="mt-1.5 text-sm font-medium text-primary underline-offset-2 hover:underline dark:text-sky-300"
          >
            {action.label}
          </button>
        ) : null}
      </div>
      {showCountdown && duration ? (
        <span className="mt-1 shrink-0 text-xs font-medium tabular-nums text-muted-foreground">
          {secondsLeft}s
        </span>
      ) : null}
      <button
        type="button"
        onClick={() => onDismiss(id)}
        aria-label="Dismiss notification"
        className="shrink-0 rounded-md p-1 text-muted-foreground transition-colors hover:bg-muted hover:text-foreground"
      >
        <X className="size-4" />
      </button>
      {showCountdown && duration ? (
        <div className="absolute inset-x-0 bottom-0 h-[3px] bg-border/50" aria-hidden="true">
          <div
            className={cn('h-full', progressClassName)}
            style={{
              width: barExpired ? '0%' : '100%',
              transitionProperty: 'width',
              transitionDuration: `${duration}ms`,
              transitionTimingFunction: 'linear',
            }}
          />
        </div>
      ) : null}
    </div>
  );
}