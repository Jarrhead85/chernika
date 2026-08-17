// Единый модуль позиционирования и закрытия popover-ов (UI kit v2, раздел «Popover и dropdown»).
// Используется SearchablePopoverSelect, bell-попапом и всеми будущими popover-компонентами.
function getScrollableAncestor(element) {
  let parent = element.parentElement;
  while (parent) {
    const style = window.getComputedStyle(parent);
    if (/(auto|scroll|hidden)/.test(style.overflowY)) {
      return parent;
    }
    parent = parent.parentElement;
  }
  return null;
}

window.popoverPositioning = {
  _clickHandler: null,
  _clickAnchor: null,
  _clickPopover: null,
  _clickDotNet: null,
  _clickMethod: null,
  _escapeHandler: null,

  getPlacement(anchorElement, popoverMaxHeight) {
    if (!anchorElement) return 'bottom';
    const rect = anchorElement.getBoundingClientRect();
    const container = getScrollableAncestor(anchorElement);
    const containerBottom = container
      ? container.getBoundingClientRect().bottom
      : window.innerHeight;
    const spaceBelow = containerBottom - rect.bottom;
    return spaceBelow < popoverMaxHeight + 8 ? 'top' : 'bottom';
  },

  getAvailableHeight(anchorElement, placement) {
    if (!anchorElement) return 320;
    const rect = anchorElement.getBoundingClientRect();
    const container = getScrollableAncestor(anchorElement);
    const containerRect = container
      ? container.getBoundingClientRect()
      : { top: 0, bottom: window.innerHeight };

    const available = placement === 'top'
      ? rect.top - containerRect.top - 8
      : containerRect.bottom - rect.bottom - 8;

    return Math.max(120, Math.min(320, available));
  },

  addOutsideClickListener(anchorElement, popoverElement, dotnetRef, methodName) {
    this._closePrevious();
    this._clickAnchor = anchorElement;
    this._clickPopover = popoverElement || null;
    this._clickDotNet = dotnetRef;
    this._clickMethod = methodName;
    this._clickHandler = (event) => {
      const anchor = this._clickAnchor;
      if (!anchor) return;
      if (anchor.contains(event.target)) return;
      const popover = this._clickPopover;
      if (popover && popover.contains(event.target)) return;
      this.removeOutsideClickListener();
      try { dotnetRef.invokeMethodAsync(methodName).catch(() => {}); } catch (_) {}
    };
    document.addEventListener('click', this._clickHandler, true);
  },

  removeOutsideClickListener() {
    if (this._clickHandler) {
      document.removeEventListener('click', this._clickHandler, true);
      this._clickHandler = null;
    }
    this._clickAnchor = null;
    this._clickPopover = null;
    this._clickDotNet = null;
    this._clickMethod = null;
  },

  addEscapeListener(dotnetRef, methodName) {
    this.removeEscapeListener();
    this._escapeHandler = (e) => {
      if (e.key === 'Escape') {
        this.removeEscapeListener();
        try { dotnetRef.invokeMethodAsync(methodName).catch(() => {}); } catch (_) {}
      }
    };
    document.addEventListener('keydown', this._escapeHandler);
  },

  removeEscapeListener() {
    if (this._escapeHandler) {
      document.removeEventListener('keydown', this._escapeHandler);
      this._escapeHandler = null;
    }
  },

  _closePrevious() {
    const dotNet = this._clickDotNet;
    const method = this._clickMethod;
    this.removeOutsideClickListener();
    if (dotNet && method) {
      try { dotNet.invokeMethodAsync(method).catch(() => {}); } catch (_) {}
    }
  },

  focusElement(element) {
    if (element && element.focus) element.focus();
  }
};
