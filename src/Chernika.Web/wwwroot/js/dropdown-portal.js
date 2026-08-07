window.dropdownPortal = {
  _handler: null,
  _dotNet: null,
  _activeId: null,

  attach(dropId, anchorId) {
    const el = document.getElementById(dropId);
    const anchor = document.getElementById(anchorId);
    if (!el || !anchor) return;

    el.style.removeProperty('display');
    el.__originalParent = el.parentNode;
    document.body.appendChild(el);

    const r = anchor.getBoundingClientRect();
    const pad = 12;
    const maxH = 280;

    const spaceBelow = window.innerHeight - r.bottom - pad;
    const spaceAbove = r.top - pad;
    const openUp = spaceBelow < Math.min(maxH, 220) && spaceAbove > spaceBelow;
    const height = Math.max(140, Math.min(maxH, openUp ? spaceAbove : spaceBelow));
    const width = Math.min(Math.max(r.width, 240), window.innerWidth - pad * 2);
    let left = Math.max(pad, Math.min(r.left, window.innerWidth - width - pad));
    let top = openUp ? Math.max(pad, r.top - height - 2) : r.bottom + 2;

    el.style.cssText += `
      position: fixed !important;
      top: ${top}px !important;
      left: ${left}px !important;
      width: ${width}px !important;
      max-height: ${height}px !important;
      z-index: 9999 !important;
    `;
    this._activeId = dropId;
  },

  detach(dropId) {
    const el = document.getElementById(dropId);
    if (!el) return;

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

window.bellDropdown = {
  _escHandler: null,

  position(dropId, btnId) {
    const el = document.getElementById(dropId);
    const btn = document.getElementById(btnId);
    if (!el || !btn) return;

    const r = btn.getBoundingClientRect();
    const vh = (window.visualViewport && window.visualViewport.height) || window.innerHeight;
    const gap = 20;
    const spaceBelow = vh - r.bottom - gap;
    const openUp = spaceBelow < 220 && r.top > spaceBelow;

    el.classList.toggle('open-up', openUp);
    const max = Math.max(160, Math.min(430, openUp ? r.top - gap : spaceBelow));
    el.style.setProperty('--bell-max', max + 'px');
  },

  onEscape(dotNet) {
    this._clearEscape();
    this._escHandler = (e) => {
      if (e.key === 'Escape') {
        this._clearEscape();
        try { dotNet.invokeMethodAsync('CloseDropdown').catch(() => {}); } catch (_) {}
      }
    };
    document.addEventListener('keydown', this._escHandler);
  },

  clear() {
    this._clearEscape();
  },

  _clearEscape() {
    if (this._escHandler) {
      document.removeEventListener('keydown', this._escHandler);
      this._escHandler = null;
    }
  }
};

window.hkPdfPreview = {
  async load(streamRef, iframeId) {
    const iframe = document.getElementById(iframeId);
    if (!iframe || !streamRef) return;
    if (iframe.__blobUrl) {
      URL.revokeObjectURL(iframe.__blobUrl);
      iframe.__blobUrl = null;
    }
    try {
      const blob = await new Response(streamRef.stream).blob();
      iframe.__blobUrl = URL.createObjectURL(blob);
      iframe.src = iframe.__blobUrl;
    } catch (_) {
      iframe.src = '';
    }
  },

  clear(iframeId) {
    const iframe = document.getElementById(iframeId);
    if (!iframe) return;
    if (iframe.__blobUrl) {
      URL.revokeObjectURL(iframe.__blobUrl);
      iframe.__blobUrl = null;
    }
    iframe.src = '';
  }
};
