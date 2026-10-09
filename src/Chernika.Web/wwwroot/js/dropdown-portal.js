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
      /* Списки, скрытые в CSS до переноса (display:none), здесь
         показываются. Для списков с обычным display это no-op. */
      display: block !important;
      z-index: 9999 !important;
    `;
    this._activeId = dropId;
  },

  detach(dropId) {
    const el = document.getElementById(dropId);
    if (!el) return;

    // Возврат выполняется только если элемент действительно переносился в body.
    // Раньше здесь стояла ветка removeChild: если attach вышел раньше времени
    // (не нашёл якорь), переноса не было, а detach всё равно удалял элемент из
    // исходного контейнера. Blazor после этого терял свой узел: список
    // пропадал, выбор не работал, поле исчезало.
    if (el.__originalParent) {
      if (el.__originalParent !== document.body) {
        el.__originalParent.appendChild(el);
      }
      delete el.__originalParent;
    }

    if (this._activeId === dropId) this._activeId = null;
    this._clearHandler();
    this._clearEscape();
  },

  /* Фокус на первый поле внутри перенесённого списка. Нужен там, где строка
     поиска находится внутри выпадающего списка: на динамически вставленный
     элемент атрибут autofocus не действует. */
  focusFirstInput(dropId) {
    const el = document.getElementById(dropId);
    if (!el) return;
    const input = el.querySelector('input:not([type="hidden"]), select, textarea');
    if (input) input.focus();
  },

  /* ignoreId — необязательный элемент, клики по которому НЕ считаются
     «кликом вне». Нужен, когда выпадающий список вынесен в портал, а само поле
     (строка поиска, кнопка открытия) осталось снаружи: без него ввод в строку
     поиска закрывал бы список. Старый вызов с двумя аргументами работает как
     прежде. */
  onClickOutside(dropId, dotNet, ignoreId) {
    this._clearHandler();
    this._dotNet = dotNet;

    this._handler = (e) => {
      const el = document.getElementById(dropId);
      const ignore = ignoreId ? document.getElementById(ignoreId) : null;
      // Клик по самому списку и по полю, его открывшему, — не закрытие.
      if (el && (el.contains(e.target) || (ignore && ignore.contains(e.target)))) return;

      this._clearHandler();
      this._clearEscape();
      try {
        dotNet.invokeMethodAsync('CloseDropdown').catch(() => {});
      } catch (_) {}
    };

    setTimeout(() => {
      document.addEventListener('mousedown', this._handler, { capture: true });
    }, 150);
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

  clearEscape() {
    this._clearEscape();
  },

  _clearHandler() {
    if (this._handler) {
      document.removeEventListener('mousedown', this._handler, { capture: true });
      this._handler = null;
    }
    this._dotNet = null;
  },

  _clearEscape() {
    if (this._escHandler) {
      document.removeEventListener('keydown', this._escHandler);
      this._escHandler = null;
    }
  },

  cleanup() {
    this._clearHandler();
    this._clearEscape();
    if (this._activeId) {
      const el = document.getElementById(this._activeId);
      // Та же оговорка, что в detach: узел возвращается только если он
      // действительно переносился в body. Иначе Blazor потерял бы элемент.
      if (el && el.__originalParent && el.__originalParent !== document.body) {
        el.__originalParent.appendChild(el);
      }
      if (el) delete el.__originalParent;
      this._activeId = null;
    }
  }
};

window.hkPdfPreview = {
  // Предпросмотр PDF открывается в отдельной вкладке, чтобы можно было
  // продолжать заполнять форму и строки ХК рядом с документом.
  async openInNewTab(streamRef) {
    if (!streamRef) return;
    // Сначала читаем поток (через SignalR), только потом открываем вкладку:
    // window.open('about:blank') во время чтения ломает соединение Blazor.
    const buffer = typeof streamRef.arrayBuffer === 'function'
      ? await streamRef.arrayBuffer()
      : await new Response(await streamRef.stream()).arrayBuffer();
    const url = URL.createObjectURL(new Blob([buffer], { type: 'application/pdf' }));
    window.open(url, '_blank');
  }
};
