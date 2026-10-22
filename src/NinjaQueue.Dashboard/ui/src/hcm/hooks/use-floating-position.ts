'use client';

import { useEffect, useState, type RefObject } from 'react';

export type FloatingPosition = {
  /** Fixed-position top offset — only set when the popover opens downward. */
  top: number | null;
  /** Fixed-position bottom offset (distance from viewport bottom) — only set when opening upward. */
  bottom: number | null;
  left: number;
  width: number;
  openUpward: boolean;
};

/**
 * Tracks a trigger element's viewport rect so its popover (Select's listbox, DatePicker's calendar)
 * can be portaled to document.body and positioned with `position: fixed` instead of rendering as an
 * `absolute` sibling inside the trigger's own DOM position. The `absolute` approach broke down inside
 * a Dialog: DialogContent is `overflow-y-auto`, so an open popover's height was added to the dialog's
 * own scrollable content, growing a scrollbar on the dialog and clipping/pushing surrounding fields
 * instead of floating above everything like a real popover. Recomputes on scroll (capture phase, so it
 * also catches the dialog's own internal scroll container) and resize while open, so the popover keeps
 * tracking its trigger.
 */
export function useFloatingPosition(
  triggerRef: RefObject<HTMLElement | null>,
  isOpen: boolean,
  popoverHeightEstimate = 260,
) {
  const [position, setPosition] = useState<FloatingPosition | null>(null);

  useEffect(() => {
    if (!isOpen) {
      setPosition(null);
      return;
    }

    const update = () => {
      const el = triggerRef.current;
      if (!el) return;
      const rect = el.getBoundingClientRect();
      const openUpward = rect.bottom + popoverHeightEstimate > window.innerHeight;
      setPosition({
        top: openUpward ? null : rect.bottom + 4,
        bottom: openUpward ? window.innerHeight - rect.top + 4 : null,
        left: rect.left,
        width: rect.width,
        openUpward,
      });
    };

    // Capture phase also catches scroll events from containers *inside* the popover itself (e.g.
    // scrolling Select's own option list) — those never move the trigger, so recomputing on every
    // tick just fought the list's own scroll with repeated re-renders. Skip anything that isn't an
    // ancestor of the trigger (i.e. doesn't actually affect the trigger's position).
    const handleScroll = (event: Event) => {
      const el = triggerRef.current;
      if (!el) return;
      const target = event.target;
      if (target instanceof Node && target !== document && !target.contains(el)) return;
      update();
    };

    update();
    window.addEventListener('scroll', handleScroll, true);
    window.addEventListener('resize', update);
    return () => {
      window.removeEventListener('scroll', handleScroll, true);
      window.removeEventListener('resize', update);
    };
  }, [isOpen, triggerRef, popoverHeightEstimate]);

  return position;
}

/**
 * Where a floating popover (Select's listbox, DatePicker's calendar) should portal to. Prefers the
 * nearest ancestor Dialog's own content node (Radix marks it `role="dialog"`) over `document.body`.
 *
 * A modal Radix Dialog does three things to everything *outside* its own content node while open:
 * sets `document.body.style.pointerEvents = 'none'` (only its own node gets `auto` back), traps
 * focus by yanking it straight back into the dialog on any `focusin` elsewhere, and blocks `wheel`
 * scrolling via its scroll-lock (react-remove-scroll) except within its content node/registered
 * shards. A popover portaled to `document.body` as a sibling of the dialog hits all three — clicks
 * and scroll silently no-op, and a search input inside it can never hold focus to receive keystrokes.
 * Portaling inside the dialog's own content node instead makes the popover count as "inside" for all
 * three mechanisms. `position: fixed` still escapes the content node's `overflow-y-auto` regardless
 * of DOM placement (fixed elements aren't clipped by an ancestor's overflow), so this doesn't
 * reintroduce the original modal-clipping bug — it just needs a non-transformed ancestor, which
 * DialogContent is here (this repo has no tailwindcss-animate/tw-animate-css plugin installed, so its
 * `animate-in`/`zoom-in-95` classes generate no CSS and never apply a transform).
 */
export function getFloatingPortalTarget(triggerElement: HTMLElement | null): HTMLElement {
  return triggerElement?.closest<HTMLElement>('[role="dialog"]') ?? document.body;
}