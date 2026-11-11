// A hover card for a trigger that names its popover with popovertarget. Resting the mouse on the trigger shows the
// popover; leaving the trigger and the popover hides it again, so the pointer can cross into the card to copy from
// it. A click pins it open, as the keyboard and touch path always does, until it is dismissed.

/** How long the pointer rests on the trigger before the card shows, so passing over a column shows nothing. */
export const HOVER_OPEN_DELAY_MS = 350;

/** How long the card waits after the pointer leaves, so it survives the gap between trigger and card. */
export const HOVER_CLOSE_DELAY_MS = 150;

export function hoverPopover(trigger: HTMLElement): { destroy(): void } {
  const id = trigger.getAttribute('popovertarget');
  const popover = id ? document.getElementById(id) : null;
  if (!popover || typeof popover.showPopover !== 'function') {
    return { destroy() {} };
  }

  let shownByHover = false;
  let openTimer: ReturnType<typeof setTimeout> | undefined;
  let closeTimer: ReturnType<typeof setTimeout> | undefined;
  const isOpen = () => popover.matches(':popover-open');
  const fromMouse = (event: PointerEvent) => event.pointerType === 'mouse';

  function scheduleClose() {
    clearTimeout(closeTimer);
    closeTimer = setTimeout(() => {
      if (shownByHover && isOpen()) {
        popover!.hidePopover();
      }
      shownByHover = false;
    }, HOVER_CLOSE_DELAY_MS);
  }

  function enterTrigger(event: PointerEvent) {
    if (!fromMouse(event)) return;
    clearTimeout(closeTimer);
    if (isOpen()) return;
    openTimer = setTimeout(() => {
      if (!isOpen()) {
        popover!.showPopover();
        shownByHover = true;
      }
    }, HOVER_OPEN_DELAY_MS);
  }

  function leave(event: PointerEvent) {
    if (!fromMouse(event)) return;
    clearTimeout(openTimer);
    if (shownByHover) scheduleClose();
  }

  // A click on a card the hover showed pins it, rather than toggling it shut under the pointer.
  function click(event: MouseEvent) {
    clearTimeout(openTimer);
    clearTimeout(closeTimer);
    if (shownByHover && isOpen()) {
      event.preventDefault();
    }
    shownByHover = false;
  }

  function enterPopover() {
    clearTimeout(closeTimer);
  }

  function toggled(event: Event) {
    if ((event as ToggleEvent).newState === 'closed') shownByHover = false;
  }

  trigger.addEventListener('pointerenter', enterTrigger);
  trigger.addEventListener('pointerleave', leave);
  trigger.addEventListener('click', click);
  popover.addEventListener('pointerenter', enterPopover);
  popover.addEventListener('pointerleave', leave);
  popover.addEventListener('toggle', toggled);

  return {
    destroy() {
      clearTimeout(openTimer);
      clearTimeout(closeTimer);
      trigger.removeEventListener('pointerenter', enterTrigger);
      trigger.removeEventListener('pointerleave', leave);
      trigger.removeEventListener('click', click);
      popover.removeEventListener('pointerenter', enterPopover);
      popover.removeEventListener('pointerleave', leave);
      popover.removeEventListener('toggle', toggled);
    },
  };
}
