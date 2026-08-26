// Единый модуль позиционирования и закрытия popover-ов (UI kit v2, раздел «Popover и dropdown»).
// Используется SearchablePopoverSelect, bell-попапом и всеми будущими popover-компонентами.
// Обработчики клика/Escape хранятся на DOM-элементе-якоре, чтобы:
// 1) не накапливать document-level слушатели при повторных открытиях,
// 2) корректно отключаться при Dispose конкретного компонента,
// 3) не держать глобальное состояние между разными popover-ами.
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

function ensureElementMap(element) {
  if (!element) return null;
  if (!element.__popoverPositioning) element.__popoverPositioning = {};
  return element.__popoverPositioning;
}

window.popoverPositioning = {
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
    if (!anchorElement) return;
    const map = ensureElementMap(anchorElement);
    if (!map || map.outsideClickHandler) return; // уже подключён

    const handler = (event) => {
      if (anchorElement.contains(event.target)) return;
      if (popoverElement && popoverElement.contains(event.target)) return;
      this.removeOutsideClickListener(anchorElement);
      try { dotnetRef.invokeMethodAsync(methodName).catch(() => {}); } catch (_) {}
    };

    map.outsideClickHandler = handler;
    document.addEventListener('click', handler, true);
  },

  removeOutsideClickListener(anchorElement) {
    if (!anchorElement) return;
    const map = anchorElement.__popoverPositioning;
    if (!map || !map.outsideClickHandler) return;
    document.removeEventListener('click', map.outsideClickHandler, true);
    delete map.outsideClickHandler;
  },

  addEscapeListener(anchorElement, dotnetRef, methodName) {
    if (!anchorElement) return;
    const map = ensureElementMap(anchorElement);
    if (!map || map.escapeHandler) return; // уже подключён

    const handler = (e) => {
      if (e.key === 'Escape') {
        this.removeEscapeListener(anchorElement);
        try { dotnetRef.invokeMethodAsync(methodName).catch(() => {}); } catch (_) {}
      }
    };

    map.escapeHandler = handler;
    document.addEventListener('keydown', handler);
  },

  removeEscapeListener(anchorElement) {
    if (!anchorElement) return;
    const map = anchorElement.__popoverPositioning;
    if (!map || !map.escapeHandler) return;
    document.removeEventListener('keydown', map.escapeHandler);
    delete map.escapeHandler;
  },

  focusElement(element) {
    if (element && element.focus) element.focus();
  }
};

window.chernika = window.chernika || {};

window.chernika.getViewportSize = () => ({
  width: window.innerWidth,
  height: window.innerHeight
});
