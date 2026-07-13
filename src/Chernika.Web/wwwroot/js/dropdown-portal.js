window.dropdownPortal = {
  _handler: null,
  _dotNet: null,
  _activeId: null,

  attach(dropId, anchorId) {
    const el = document.getElementById(dropId);
    const anchor = document.getElementById(anchorId);
    if (!el || !anchor) return;

    el.style.removeProperty('display');

    // Save original parent so detach can move element back before Blazor re-render
    el.__originalParent = el.parentNode;

    document.body.appendChild(el);

    const r = anchor.getBoundingClientRect();
    el.style.cssText += `
      position: fixed !important;
      top: ${r.bottom}px !important;
      left: ${r.left}px !important;
      min-width: ${r.width}px !important;
      z-index: 9999 !important;
    `;
    this._activeId = dropId;
  },

  detach(dropId) {
    const el = document.getElementById(dropId);
    if (!el) return;

    // Move back to original parent so Blazor can find it during re-render
    if (el.__originalParent && el.__originalParent !== document.body) {
      el.__originalParent.appendChild(el);
    } else if (el.parentNode) {
      el.parentNode.removeChild(el);
    }
    delete el.__originalParent;

    if (this._activeId === dropId) this._activeId = null;
    this._clearHandler();
  },

  onClickOutside(dropId, dotNet) {
    this._clearHandler();
    this._dotNet = dotNet;

    this._handler = (e) => {
      const el = document.getElementById(dropId);
      if (!el || !el.contains(e.target)) {
        this._clearHandler();
        try {
          dotNet.invokeMethodAsync('CloseDropdown').catch(() => {});
        } catch (_) {}
      }
    };

    setTimeout(() => {
      document.addEventListener('mousedown', this._handler, { capture: true });
    }, 150);
  },

  _clearHandler() {
    if (this._handler) {
      document.removeEventListener('mousedown', this._handler, { capture: true });
      this._handler = null;
    }
    this._dotNet = null;
  },

  cleanup() {
    this._clearHandler();
    if (this._activeId) {
      const el = document.getElementById(this._activeId);
      if (el) {
        if (el.__originalParent && el.__originalParent !== document.body) {
          el.__originalParent.appendChild(el);
        } else if (el.parentNode) {
          el.parentNode.removeChild(el);
        }
        delete el.__originalParent;
      }
      this._activeId = null;
    }
  }
};
