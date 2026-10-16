/**
 * MajiOCR.Web Main Application Controller (iOS / iPhone X+ PWA)
 * Conforms to RS-ISSUE-0044-v0.1, AR-ISSUE-0044-v0.1, DP-ISSUE-0044-v0.1, and DBA-ISSUE-0044-v0.1.
 */
(function () {
  'use strict';

  const IDB_NAME = 'MajiOcrLocalDB';
  const IDB_VERSION = 1;
  const STORE_STATE = 'app_state';
  const STORE_IMAGES = 'card_images';

  const state = {
    contacts: [],
    tagCatalog: [],
    customFields: [],
    manifest: null,
    imageBlobUrls: new Map(), // relativePath -> objectURL
    searchQuery: '',
    selectedTagId: '__ALL__', // '__ALL__' | '__UNTAGGED__' | tagId
    sortBy: 'created_desc', // 'created_desc' | 'name_asc' | 'company_asc'
    activeContact: null,
    pendingEncryptedContext: null, // { dbBytes, manifest, imageEntriesMap, sourceLabel }
    driveAccessToken: sessionStorage.getItem('majiocr_drive_token') || '',
    googleClientId:
      localStorage.getItem('majiocr_google_client_id') ||
      (window.MAJIOCR_WEB_CONFIG && window.MAJIOCR_WEB_CONFIG.googleClientId) ||
      ''
  };

  // --- IndexedDB Persistence ---
  function openLocalDb() {
    return new Promise((resolve, reject) => {
      const req = indexedDB.open(IDB_NAME, IDB_VERSION);
      req.onupgradeneeded = (e) => {
        const db = e.target.result;
        if (!db.objectStoreNames.contains(STORE_STATE)) {
          db.createObjectStore(STORE_STATE);
        }
        if (!db.objectStoreNames.contains(STORE_IMAGES)) {
          db.createObjectStore(STORE_IMAGES);
        }
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }

  async function saveToIndexedDb(parsedData, manifest, imageEntriesMap) {
    const db = await openLocalDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction([STORE_STATE, STORE_IMAGES], 'readwrite');
      const stateStore = tx.objectStore(STORE_STATE);
      const imgStore = tx.objectStore(STORE_IMAGES);

      stateStore.put(
        {
          contacts: parsedData.contacts,
          tagCatalog: parsedData.tagCatalog,
          customFields: parsedData.customFields,
          manifest: manifest || {
            backupId: 'Local_Import',
            createdAtUtc: new Date().toISOString(),
            contactCount: parsedData.contacts.length,
            imageCount: imageEntriesMap ? imageEntriesMap.size : 0
          },
          savedAtUtc: new Date().toISOString()
        },
        'current_snapshot'
      );

      imgStore.clear();
      if (imageEntriesMap) {
        for (const [relPath, uint8Bytes] of imageEntriesMap.entries()) {
          imgStore.put(uint8Bytes, relPath);
        }
      }

      tx.oncomplete = () => {
        db.close();
        resolve();
      };
      tx.onerror = () => {
        db.close();
        reject(tx.error);
      };
    });
  }

  async function loadFromIndexedDb() {
    try {
      const db = await openLocalDb();
      return await new Promise((resolve, reject) => {
        const tx = db.transaction([STORE_STATE, STORE_IMAGES], 'readonly');
        const stateStore = tx.objectStore(STORE_STATE);
        const imgStore = tx.objectStore(STORE_IMAGES);

        const snapReq = stateStore.get('current_snapshot');
        const imgKeysReq = imgStore.getAllKeys();
        const imgValsReq = imgStore.getAll();

        tx.oncomplete = () => {
          db.close();
          const snapshot = snapReq.result;
          if (!snapshot) {
            resolve(null);
            return;
          }
          const imgMap = new Map();
          const keys = imgKeysReq.result || [];
          const vals = imgValsReq.result || [];
          for (let i = 0; i < keys.length; i++) {
            imgMap.set(keys[i], vals[i]);
          }
          resolve({ snapshot, imgMap });
        };
        tx.onerror = () => {
          db.close();
          reject(tx.error);
        };
      });
    } catch (err) {
      console.warn('IndexedDB load warning:', err);
      return null;
    }
  }

  function rebuildImageObjectUrls(imageEntriesMap) {
    for (const url of state.imageBlobUrls.values()) {
      URL.revokeObjectURL(url);
    }
    state.imageBlobUrls.clear();
    if (!imageEntriesMap) return;

    for (const [relPath, bytes] of imageEntriesMap.entries()) {
      const lower = relPath.toLowerCase();
      const mime = lower.endsWith('.png') ? 'image/png' : 'image/jpeg';
      const blob = new Blob([bytes], { type: mime });
      const url = URL.createObjectURL(blob);
      state.imageBlobUrls.set(relPath.replace(/\\/g, '/'), url);
      // Also index by basename for resilient lookup
      const baseName = relPath.split('/').pop();
      if (baseName) {
        state.imageBlobUrls.set(`card-images/${baseName}`, url);
        state.imageBlobUrls.set(baseName, url);
      }
    }
  }

  function resolveContactImageUrl(relPath) {
    if (!relPath) return null;
    const norm = String(relPath).replace(/\\/g, '/');
    if (state.imageBlobUrls.has(norm)) return state.imageBlobUrls.get(norm);
    const base = norm.split('/').pop();
    if (base && state.imageBlobUrls.has(base)) return state.imageBlobUrls.get(base);
    return null;
  }

  // --- HTML Escaping & Keyword Highlighting ---
  function escapeHtml(str) {
    return String(str || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  function tokenizeSearchQuery(query) {
    return String(query || '')
      .trim()
      .toLowerCase()
      .split(/\s+/)
      .filter(Boolean);
  }

  function highlightText(text, tokens) {
    const raw = String(text || '');
    if (!raw || !tokens || tokens.length === 0) return escapeHtml(raw);

    const escapedTokens = tokens
      .map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
      .filter(Boolean);
    if (escapedTokens.length === 0) return escapeHtml(raw);

    const regex = new RegExp(`(${escapedTokens.join('|')})`, 'gi');
    const parts = raw.split(regex);
    return parts
      .map((part) => {
        if (regex.test(part)) {
          regex.lastIndex = 0;
          return `<mark class="kw-mark">${escapeHtml(part)}</mark>`;
        }
        return escapeHtml(part);
      })
      .join('');
  }

  // --- Filtering & Sorting ---
  function getFilteredContacts() {
    const tokens = tokenizeSearchQuery(state.searchQuery);
    const tagFilter = state.selectedTagId;

    const filtered = state.contacts.filter((c) => {
      if (tagFilter === '__UNTAGGED__') {
        if (c.tags && c.tags.length > 0) return false;
      } else if (tagFilter !== '__ALL__') {
        if (!c.tags || !c.tags.some((t) => t.tagId === tagFilter)) return false;
      }

      if (tokens.length > 0) {
        for (const tk of tokens) {
          if (!c.searchIndexText.includes(tk)) return false;
        }
      }
      return true;
    });

    filtered.sort((a, b) => {
      if (state.sortBy === 'name_asc') {
        return (a.fullName || '').localeCompare(b.fullName || '', 'zh-Hant');
      }
      if (state.sortBy === 'company_asc') {
        return (a.company || '').localeCompare(b.company || '', 'zh-Hant');
      }
      return (b.createdAt || '').localeCompare(a.createdAt || '');
    });

    return filtered;
  }

  // --- UI Rendering ---
  function renderApp() {
    const totalCountEl = document.getElementById('headerTotalCount');
    const statusBannerEl = document.getElementById('dbStatusBanner');
    const tagBarEl = document.getElementById('tagChipsBar');
    const listContainerEl = document.getElementById('contactsListContainer');
    const emptyStateEl = document.getElementById('emptyStateContainer');
    const filterSummaryEl = document.getElementById('filterResultSummary');

    totalCountEl.textContent = `${state.contacts.length} 筆`;

    if (state.manifest) {
      const encBadge = state.manifest.isPasswordProtected
        ? '<span class="badge badge-encrypted">🔒 SQLCipher 已解密</span>'
        : '<span class="badge badge-unencrypted">🔓 標準 SQLite</span>';
      const platformBadge = state.manifest.platform
        ? `<span class="badge badge-platform">${escapeHtml(String(state.manifest.platform).toUpperCase())}</span>`
        : '';
      statusBannerEl.innerHTML = `
        <div class="status-banner-inner">
          <div>${platformBadge} ${encBadge} <span class="status-meta">共 ${state.contacts.length} 筆名片 · 本機離線就緒</span></div>
          <button type="button" class="btn-link" id="exportAllVcfBtn">匯出全部 .vcf</button>
        </div>
      `;
      statusBannerEl.classList.remove('hidden');
      const expBtn = document.getElementById('exportAllVcfBtn');
      if (expBtn) expBtn.onclick = () => exportContactsToVcf(state.contacts, 'MajiOCR_All_Contacts.vcf');
    } else {
      statusBannerEl.classList.add('hidden');
    }

    // Render Tag Filter Chips
    const untaggedCount = state.contacts.filter((c) => !c.tags || c.tags.length === 0).length;
    const tagCounts = new Map();
    for (const c of state.contacts) {
      for (const t of c.tags || []) {
        tagCounts.set(t.tagId, (tagCounts.get(t.tagId) || 0) + 1);
      }
    }

    const chipsHtml = [
      `<button type="button" class="tag-chip ${state.selectedTagId === '__ALL__' ? 'active' : ''}" data-tag="__ALL__">全部 (${state.contacts.length})</button>`,
      `<button type="button" class="tag-chip ${state.selectedTagId === '__UNTAGGED__' ? 'active' : ''}" data-tag="__UNTAGGED__">未分類 (${untaggedCount})</button>`
    ];

    for (const tag of state.tagCatalog) {
      const cnt = tagCounts.get(tag.tagId) || 0;
      const activeCls = state.selectedTagId === tag.tagId ? 'active' : '';
      chipsHtml.push(
        `<button type="button" class="tag-chip ${activeCls}" data-tag="${escapeHtml(tag.tagId)}">🏷️ ${escapeHtml(tag.displayName)} (${cnt})</button>`
      );
    }
    tagBarEl.innerHTML = chipsHtml.join('');
    tagBarEl.querySelectorAll('.tag-chip').forEach((btn) => {
      btn.onclick = () => {
        state.selectedTagId = btn.getAttribute('data-tag') || '__ALL__';
        renderApp();
      };
    });

    if (state.contacts.length === 0) {
      emptyStateEl.classList.remove('hidden');
      listContainerEl.innerHTML = '';
      filterSummaryEl.textContent = '';
      return;
    }

    emptyStateEl.classList.add('hidden');
    const filtered = getFilteredContacts();
    const tokens = tokenizeSearchQuery(state.searchQuery);

    filterSummaryEl.textContent =
      filtered.length === state.contacts.length
        ? `顯示全部 ${filtered.length} 筆名片`
        : `篩選結果：符合 ${filtered.length} 筆（共 ${state.contacts.length} 筆）`;

    if (filtered.length === 0) {
      listContainerEl.innerHTML = `
        <div class="no-match-box">
          <div class="no-match-icon">🔍</div>
          <div class="no-match-title">找不到符合條件的名片</div>
          <div class="no-match-sub">請嘗試調整搜尋關鍵字或切換上方的分類標籤</div>
        </div>
      `;
      return;
    }

    listContainerEl.innerHTML = filtered
      .map((c) => {
        const primaryImg = c.images && c.images.length > 0 ? resolveContactImageUrl(c.images[0].relativePath) : null;
        const initial = (c.fullName || '名').replace(/[()]/g, '').trim().charAt(0) || '名';
        const avatarHtml = primaryImg
          ? `<img src="${primaryImg}" alt="${escapeHtml(c.fullName)}" class="card-thumb" loading="lazy" />`
          : `<div class="card-avatar">${escapeHtml(initial)}</div>`;

        const primaryPhone = c.phones && c.phones.length > 0 ? c.phones[0].value : '';
        const primaryEmail = c.emails && c.emails.length > 0 ? c.emails[0].value : '';
        const subtitleParts = [c.jobTitle, c.department].filter(Boolean).join(' · ');

        const tagsHtml = (c.tags || [])
          .map((t) => `<span class="mini-tag">${highlightText(t.displayName, tokens)}</span>`)
          .join('');

        const customHtml = (c.customFields || [])
          .slice(0, 2)
          .map((cf) => `<span class="mini-custom">${highlightText(`${cf.label}: ${cf.value}`, tokens)}</span>`)
          .join('');

        const dialBtnHtml = primaryPhone
          ? `<a href="tel:${escapeHtml(primaryPhone)}" class="quick-dial-btn" title="撥打 ${escapeHtml(primaryPhone)}" onclick="event.stopPropagation();">📞</a>`
          : '';

        return `
          <article class="contact-card" data-cid="${escapeHtml(c.contactId)}">
            <div class="contact-card-left">${avatarHtml}</div>
            <div class="contact-card-body">
              <div class="contact-name-row">
                <span class="contact-name">${highlightText(c.fullName, tokens)}</span>
                ${subtitleParts ? `<span class="contact-title">${highlightText(subtitleParts, tokens)}</span>` : ''}
              </div>
              ${c.company ? `<div class="contact-company">🏢 ${highlightText(c.company, tokens)}</div>` : ''}
              <div class="contact-meta-list">
                ${primaryPhone ? `<span class="contact-meta-item">📱 ${highlightText(primaryPhone, tokens)}</span>` : ''}
                ${primaryEmail ? `<span class="contact-meta-item">✉️ ${highlightText(primaryEmail, tokens)}</span>` : ''}
              </div>
              ${tagsHtml || customHtml ? `<div class="contact-badges-row">${tagsHtml}${customHtml}</div>` : ''}
            </div>
            <div class="contact-card-right">
              ${dialBtnHtml}
              <span class="chevron-icon">›</span>
            </div>
          </article>
        `;
      })
      .join('');

    listContainerEl.querySelectorAll('.contact-card').forEach((cardEl) => {
      cardEl.onclick = () => {
        const cid = cardEl.getAttribute('data-cid');
        const target = state.contacts.find((x) => x.contactId === cid);
        if (target) openContactDetailModal(target);
      };
    });
  }

  // --- Contact Detail Bottom Sheet ---
  function openContactDetailModal(contact) {
    state.activeContact = contact;
    const modal = document.getElementById('detailModal');
    const contentEl = document.getElementById('detailSheetBody');

    const images = (contact.images || [])
      .map((img, idx) => ({
        url: resolveContactImageUrl(img.relativePath),
        label: idx === 0 ? '名片正面' : `名片圖檔 ${idx + 1}`,
        relPath: img.relativePath
      }))
      .filter((x) => x.url);

    const imagesGalleryHtml =
      images.length > 0
        ? `
        <div class="detail-images-carousel">
          ${images
            .map(
              (img) => `
            <div class="detail-image-card">
              <img src="${img.url}" alt="${escapeHtml(img.label)}" class="detail-card-img" data-zoom-url="${img.url}" />
              <div class="detail-image-caption">${escapeHtml(img.label)}（點擊放大）</div>
            </div>
          `
            )
            .join('')}
        </div>`
        : `<div class="detail-no-image">📇 此名片未附帶圖片檔案或備份包未包含該圖檔</div>`;

    const firstPhone = contact.phones && contact.phones.length > 0 ? contact.phones[0].value : '';
    const firstEmail = contact.emails && contact.emails.length > 0 ? contact.emails[0].value : '';
    const firstAddr = contact.addresses && contact.addresses.length > 0 ? contact.addresses[0].value : '';

    const fieldRows = [];
    const pushField = (icon, label, value, actionHref) => {
      if (!value) return;
      const valHtml = actionHref
        ? `<a href="${actionHref}" class="detail-field-link">${escapeHtml(value)}</a>`
        : `<span class="detail-field-text">${escapeHtml(value)}</span>`;
      fieldRows.push(`
        <div class="detail-field-row">
          <div class="detail-field-main">
            <div class="detail-field-label">${icon} ${escapeHtml(label)}</div>
            <div class="detail-field-value">${valHtml}</div>
          </div>
          <button type="button" class="btn-copy-field" data-copy="${escapeHtml(value)}">複製</button>
        </div>
      `);
    };

    pushField('👤', '姓名', contact.fullName);
    pushField('💼', '職稱', contact.jobTitle);
    pushField('🏢', '公司名稱', contact.company);
    pushField('🏛️', '部門', contact.department);
    pushField('🔢', '統一編號', contact.taxId);

    for (const p of contact.phones || []) {
      pushField('📞', p.label || '電話', p.value, `tel:${encodeURIComponent(p.value)}`);
    }
    for (const e of contact.emails || []) {
      pushField('✉️', e.label || 'Email', e.value, `mailto:${encodeURIComponent(e.value)}`);
    }
    for (const a of contact.addresses || []) {
      pushField(
        '📍',
        a.label || '地址',
        a.value,
        `https://maps.google.com/?q=${encodeURIComponent(a.value)}`
      );
    }
    for (const w of contact.websites || []) {
      const url = /^https?:\/\//i.test(w.value) ? w.value : `https://${w.value}`;
      pushField('🌐', w.label || '網站', w.value, url);
    }
    for (const cf of contact.customFields || []) {
      pushField('🔖', cf.label || '自訂欄位', cf.value);
    }
    pushField('📝', '備註', contact.notes);

    const tagsHtml =
      contact.tags && contact.tags.length > 0
        ? `<div class="detail-tags-wrap">${contact.tags
            .map((t) => `<span class="mini-tag">🏷️ ${escapeHtml(t.displayName)}</span>`)
            .join('')}</div>`
        : '';

    contentEl.innerHTML = `
      <div class="detail-hero">
        <div class="detail-hero-name">${escapeHtml(contact.fullName)}</div>
        ${
          contact.jobTitle || contact.company
            ? `<div class="detail-hero-sub">${escapeHtml(
                [contact.jobTitle, contact.company].filter(Boolean).join(' · ')
              )}</div>`
            : ''
        }
        ${tagsHtml}
      </div>

      <div class="detail-quick-actions">
        ${
          firstPhone
            ? `<a href="tel:${escapeHtml(firstPhone)}" class="action-pill"><span>📞</span><span>撥打電話</span></a>`
            : `<button type="button" class="action-pill disabled" disabled><span>📞</span><span>無電話</span></button>`
        }
        ${
          firstEmail
            ? `<a href="mailto:${escapeHtml(firstEmail)}" class="action-pill"><span>✉️</span><span>發送郵件</span></a>`
            : `<button type="button" class="action-pill disabled" disabled><span>✉️</span><span>無信箱</span></button>`
        }
        ${
          firstAddr
            ? `<a href="https://maps.google.com/?q=${encodeURIComponent(firstAddr)}" target="_blank" rel="noopener" class="action-pill"><span>📍</span><span>地圖導航</span></a>`
            : `<button type="button" class="action-pill disabled" disabled><span>📍</span><span>無地址</span></button>`
        }
        <button type="button" class="action-pill" id="detailExportVcfBtn"><span>📇</span><span>存入手機聯絡人</span></button>
      </div>

      ${imagesGalleryHtml}

      <div class="detail-fields-card">
        ${fieldRows.join('')}
      </div>
    `;

    contentEl.querySelectorAll('.btn-copy-field').forEach((btn) => {
      btn.onclick = async () => {
        const val = btn.getAttribute('data-copy') || '';
        try {
          await navigator.clipboard.writeText(val);
          btn.textContent = '已複製 ✓';
          setTimeout(() => {
            btn.textContent = '複製';
          }, 1500);
        } catch (_) {
          showToast('複製成功');
        }
      };
    });

    contentEl.querySelectorAll('.detail-card-img').forEach((imgEl) => {
      imgEl.onclick = () => {
        const zoomUrl = imgEl.getAttribute('data-zoom-url');
        if (zoomUrl) openImageLightbox(zoomUrl);
      };
    });

    const vcfBtn = document.getElementById('detailExportVcfBtn');
    if (vcfBtn) {
      vcfBtn.onclick = () => exportContactsToVcf([contact], `${contact.fullName || 'contact'}.vcf`);
    }

    modal.classList.remove('hidden');
  }

  function openImageLightbox(url) {
    const lb = document.getElementById('imageLightbox');
    const img = document.getElementById('lightboxImg');
    img.src = url;
    lb.classList.remove('hidden');
  }

  // --- vCard (.vcf) Export for iOS Contacts ---
  function escapeVCardValue(val) {
    return String(val || '')
      .replace(/\\/g, '\\\\')
      .replace(/\n/g, '\\n')
      .replace(/;/g, '\\;')
      .replace(/,/g, '\\,');
  }

  function exportContactsToVcf(contactsList, fileName) {
    if (!contactsList || contactsList.length === 0) {
      showToast('沒有可匯出的名片資料');
      return;
    }
    const cards = contactsList.map((c) => {
      const lines = [
        'BEGIN:VCARD',
        'VERSION:3.0',
        `FN;CHARSET=UTF-8:${escapeVCardValue(c.fullName)}`,
        `N;CHARSET=UTF-8:${escapeVCardValue(c.fullName)};;;;`
      ];
      if (c.company || c.department) {
        lines.push(`ORG;CHARSET=UTF-8:${escapeVCardValue(c.company || '')};${escapeVCardValue(c.department || '')}`);
      }
      if (c.jobTitle) {
        lines.push(`TITLE;CHARSET=UTF-8:${escapeVCardValue(c.jobTitle)}`);
      }
      for (const p of c.phones || []) {
        const type = p.kind === 'MobilePhone' ? 'CELL' : p.kind === 'Fax' ? 'FAX' : 'WORK,VOICE';
        lines.push(`TEL;TYPE=${type}:${escapeVCardValue(p.value)}`);
      }
      for (const e of c.emails || []) {
        lines.push(`EMAIL;TYPE=INTERNET,WORK:${escapeVCardValue(e.value)}`);
      }
      for (const a of c.addresses || []) {
        lines.push(`ADR;TYPE=WORK;CHARSET=UTF-8:;;${escapeVCardValue(a.value)};;;;`);
      }
      for (const w of c.websites || []) {
        lines.push(`URL:${escapeVCardValue(w.value)}`);
      }
      const noteParts = [];
      if (c.taxId) noteParts.push(`統編: ${c.taxId}`);
      if (c.notes) noteParts.push(c.notes);
      for (const cf of c.customFields || []) {
        noteParts.push(`${cf.label}: ${cf.value}`);
      }
      if (noteParts.length > 0) {
        lines.push(`NOTE;CHARSET=UTF-8:${escapeVCardValue(noteParts.join('\n'))}`);
      }
      lines.push('END:VCARD');
      return lines.join('\r\n');
    });

    const vcfBlob = new Blob([cards.join('\r\n')], { type: 'text/vcard;charset=utf-8' });
    const url = URL.createObjectURL(vcfBlob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName || 'majiocr_contacts.vcf';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    setTimeout(() => URL.revokeObjectURL(url), 3000);
    showToast(`已匯出 ${contactsList.length} 筆聯絡人 (.vcf)`);
  }

  // --- Backup ZIP & SQLite Loading Pipeline (Supports Plain & SQLCipher-AES256) ---
  async function processLoadedDatabaseAndImages(rawDbBytes, manifest, imageEntriesMap, passphrase) {
    let plainDbBytes = rawDbBytes;
    const isEncrypted = !window.MajiSqlCipher.isPlainSqliteBuffer(rawDbBytes);

    if (isEncrypted) {
      if (!passphrase) {
        // Prompt user for master password
        state.pendingEncryptedContext = {
          dbBytes: rawDbBytes,
          manifest: Object.assign({}, manifest || {}, {
            isPasswordProtected: true,
            encryptionAlgorithm: 'SQLCipher-AES256'
          }),
          imageEntriesMap
        };
        openPasswordUnlockModal(state.pendingEncryptedContext.manifest);
        return false;
      }

      plainDbBytes = await window.MajiSqlCipher.decryptSqlCipher4Database(
        rawDbBytes,
        passphrase,
        (pct, msg) => setPasswordModalStatus(msg, false)
      );
    }

    const parsed = window.MajiSqliteParser.extractMajiOcrContacts(plainDbBytes);
    const effectiveManifest = Object.assign(
      {
        backupId: 'MajiOCR_Backup',
        platform: 'windows',
        createdAtUtc: new Date().toISOString(),
        contactCount: parsed.contacts.length,
        imageCount: imageEntriesMap ? imageEntriesMap.size : 0,
        isPasswordProtected: isEncrypted,
        encryptionAlgorithm: isEncrypted ? 'SQLCipher-AES256' : null
      },
      manifest || {},
      {
        contactCount: parsed.contacts.length,
        isPasswordProtected: isEncrypted || Boolean(manifest && manifest.isPasswordProtected)
      }
    );

    state.contacts = parsed.contacts;
    state.tagCatalog = parsed.tagCatalog;
    state.customFields = parsed.customFields;
    state.manifest = effectiveManifest;
    state.selectedTagId = '__ALL__';

    rebuildImageObjectUrls(imageEntriesMap);
    await saveToIndexedDb(parsed, effectiveManifest, imageEntriesMap);
    renderApp();

    document.getElementById('syncModal').classList.add('hidden');
    document.getElementById('passwordModal').classList.add('hidden');
    showToast(`✅ 成功載入 ${parsed.contacts.length} 筆名片與 ${imageEntriesMap ? imageEntriesMap.size : 0} 張圖片！`);
    return true;
  }

  async function handleFileImport(file) {
    if (!file) return;
    setSyncStatus(`正在讀取檔案「${file.name}」...`, false);
    try {
      const ab = await file.arrayBuffer();
      const bytes = new Uint8Array(ab);
      const lowerName = file.name.toLowerCase();

      // Check if ZIP archive (starts with PK\x03\x04)
      if (
        lowerName.endsWith('.zip') ||
        (bytes.length > 4 && bytes[0] === 0x50 && bytes[1] === 0x4b && bytes[2] === 0x03 && bytes[3] === 0x04)
      ) {
        await handleBackupZipBuffer(bytes, file.name);
      } else {
        // Direct .sqlite3 file
        await processLoadedDatabaseAndImages(
          bytes,
          { backupId: file.name, platform: 'local' },
          new Map(),
          null
        );
      }
    } catch (err) {
      setSyncStatus(`載入失敗：${err.message || err}`, true);
    }
  }

  async function handleBackupZipBuffer(zipBytes, sourceName) {
    setSyncStatus('正在解壓縮備份包 (.zip)...', false);
    const entries = await window.MajiZipReader.unpackZipBuffer(zipBytes);

    let manifest = null;
    if (entries.has('manifest.json')) {
      try {
        const text = new TextDecoder('utf-8').decode(entries.get('manifest.json'));
        const j = JSON.parse(text);
        manifest = {
          backupId: j.BackupId || j.backup_id || sourceName || 'Backup',
          platform: j.Platform || j.platform || 'windows',
          createdAtUtc: j.CreatedAtUtc || j.created_at || '',
          contactCount: j.ContactCount || j.contact_count || 0,
          imageCount: j.ImageCount || j.image_count || 0,
          isPasswordProtected: Boolean(j.IsPasswordProtected || j.is_password_protected),
          encryptionAlgorithm: j.EncryptionAlgorithm || j.encryption_algorithm || null
        };
      } catch (_) {}
    }

    let dbBytes = entries.get('contacts.sqlite3');
    if (!dbBytes) {
      for (const [k, v] of entries.entries()) {
        if (k.endsWith('.sqlite3') || k.endsWith('.db')) {
          dbBytes = v;
          break;
        }
      }
    }
    if (!dbBytes) {
      throw new Error('備份壓縮包內找不到 contacts.sqlite3 資料庫檔案。');
    }

    const imageEntries = new Map();
    for (const [k, v] of entries.entries()) {
      const lower = k.toLowerCase();
      if (
        lower.startsWith('card-images/') &&
        (lower.endsWith('.jpg') || lower.endsWith('.jpeg') || lower.endsWith('.png'))
      ) {
        imageEntries.set(k, v);
      }
    }

    await processLoadedDatabaseAndImages(dbBytes, manifest, imageEntries, null);
  }

  // --- Password Unlock Modal ---
  function openPasswordUnlockModal(manifest) {
    const modal = document.getElementById('passwordModal');
    const descEl = document.getElementById('passwordModalDesc');
    const inputEl = document.getElementById('masterPasswordInput');
    const statusEl = document.getElementById('passwordModalStatus');

    const titleName = (manifest && manifest.backupId) || 'contacts.sqlite3';
    descEl.textContent = `所選備份「${titleName}」已使用 SQLCipher AES-256 加密保護。請輸入原備份時設定的主密碼以進行本機解密與完整性校驗：`;
    inputEl.value = '';
    statusEl.textContent = '';
    statusEl.className = 'modal-status hidden';
    modal.classList.remove('hidden');
    setTimeout(() => inputEl.focus(), 100);
  }

  function setPasswordModalStatus(msg, isError) {
    const statusEl = document.getElementById('passwordModalStatus');
    if (!msg) {
      statusEl.className = 'modal-status hidden';
      return;
    }
    statusEl.textContent = msg;
    statusEl.className = isError ? 'modal-status error' : 'modal-status info';
  }

  async function submitMasterPassword() {
    const inputEl = document.getElementById('masterPasswordInput');
    const btnEl = document.getElementById('confirmPasswordBtn');
    const pass = inputEl.value;
    if (!pass) {
      setPasswordModalStatus('請輸入主密碼（不可為空白）。', true);
      return;
    }
    const ctx = state.pendingEncryptedContext;
    if (!ctx) return;

    btnEl.disabled = true;
    setPasswordModalStatus('正在執行 PBKDF2-SHA512 與 HMAC 驗證...', false);
    try {
      await processLoadedDatabaseAndImages(ctx.dbBytes, ctx.manifest, ctx.imageEntriesMap, pass);
      state.pendingEncryptedContext = null;
    } catch (err) {
      const msg = String(err.message || err);
      if (msg.includes('INVALID_PASSWORD')) {
        setPasswordModalStatus('❌ 主密碼錯誤或無法解密資料庫，請重新確認密碼。', true);
      } else {
        setPasswordModalStatus(`❌ 解密失敗：${msg}`, true);
      }
    } finally {
      btnEl.disabled = false;
    }
  }

  // --- Google Drive Cloud Backup Listing & Download ---
  function setSyncStatus(msg, isError) {
    const el = document.getElementById('syncStatusBox');
    if (!msg) {
      el.className = 'modal-status hidden';
      return;
    }
    el.textContent = msg;
    el.className = isError ? 'modal-status error' : 'modal-status info';
  }

  function startGoogleOAuthPopup() {
    const clientIdInput = document.getElementById('googleClientIdInput');
    const clientId = (clientIdInput.value || '').trim();
    if (!clientId) {
      setSyncStatus('請先填入 Web 端 Google OAuth Client ID，或直接使用上方「從 iPhone 檔案匯入備份 (.zip)」。', true);
      return;
    }
    state.googleClientId = clientId;
    localStorage.setItem('majiocr_google_client_id', clientId);

    const redirectUri = window.location.origin + window.location.pathname;
    const scope = encodeURIComponent('https://www.googleapis.com/auth/drive.readonly');
    const authUrl =
      `https://accounts.google.com/o/oauth2/v2/auth` +
      `?client_id=${encodeURIComponent(clientId)}` +
      `&redirect_uri=${encodeURIComponent(redirectUri)}` +
      `&response_type=token` +
      `&scope=${scope}` +
      `&include_granted_scopes=true` +
      `&prompt=select_account`;

    window.location.href = authUrl;
  }

  function checkOAuthRedirectFragment() {
    if (!window.location.hash || !window.location.hash.includes('access_token=')) return;
    const params = new URLSearchParams(window.location.hash.substring(1));
    const token = params.get('access_token');
    if (token) {
      state.driveAccessToken = token;
      sessionStorage.setItem('majiocr_drive_token', token);
      history.replaceState(null, '', window.location.pathname + window.location.search);
      document.getElementById('syncModal').classList.remove('hidden');
      fetchGoogleDriveBackups();
    }
  }

  async function fetchGoogleDriveBackups() {
    const token = state.driveAccessToken.trim();
    if (!token) {
      setSyncStatus('請先點擊「使用 Google 帳號登入授權」以讀取雲端備份清單。', true);
      return;
    }

    const listEl = document.getElementById('driveBackupList');
    setSyncStatus('正在查詢 Google Drive 上的 MajiOCR_Backups 備份版本...', false);
    listEl.innerHTML = '';

    try {
      const q = encodeURIComponent(
        "name contains 'MajiOCR_Backup_' and mimeType != 'application/vnd.google-apps.folder' and trashed = false"
      );
      const url = `https://www.googleapis.com/drive/v3/files?q=${q}&orderBy=createdTime desc&pageSize=20&fields=files(id,name,size,createdTime,description,appProperties)`;
      const resp = await fetch(url, {
        headers: { Authorization: `Bearer ${token}` }
      });

      if (!resp.ok) {
        if (resp.status === 401 || resp.status === 403) {
          state.driveAccessToken = '';
          sessionStorage.removeItem('majiocr_drive_token');
          throw new Error('Google 授權已過期或權限不足，請重新點擊「使用 Google 帳號登入授權」。');
        }
        throw new Error(`Google Drive API 回應錯誤 (HTTP ${resp.status})`);
      }

      const data = await resp.json();
      const files = data.files || [];
      if (files.length === 0) {
        setSyncStatus('您的 Google Drive 尚無 MajiOCR_Backup_*.zip 備份檔。', false);
        return;
      }

      setSyncStatus(`找到 ${files.length} 個雲端備份版本，請點選要載入至 iPhone 的版本：`, false);
      listEl.innerHTML = files
        .map((f) => {
          const props = f.appProperties || {};
          let isEnc =
            props.is_password_protected === 'true' ||
            props.is_password_protected === true ||
            (f.description && f.description.includes('已加密'));
          let platform = (props.platform || (f.name.toLowerCase().includes('android') ? 'android' : 'windows')).toUpperCase();
          let contactCount = props.contact_count || '?';
          let imageCount = props.image_count || '?';
          const sizeKb = Math.max(1, Math.round(Number(f.size || 0) / 1024));
          const dateStr = f.createdTime ? new Date(f.createdTime).toLocaleString('zh-TW') : '';

          const encBadge = isEnc
            ? '<span class="badge badge-encrypted">🔒 已加密 (SQLCipher)</span>'
            : '<span class="badge badge-unencrypted">🔓 未加密</span>';

          return `
            <div class="drive-version-card">
              <div class="drive-version-top">
                <span class="badge badge-platform">${escapeHtml(platform)}</span>
                ${encBadge}
                <span class="drive-file-name">${escapeHtml(f.name)}</span>
              </div>
              <div class="drive-version-meta">
                📅 ${escapeHtml(dateStr)} · 📇 名片: ${escapeHtml(contactCount)} 筆 · 🖼️ 圖片: ${escapeHtml(imageCount)} 張 · 💾 ${sizeKb} KB
              </div>
              <button type="button" class="btn-primary btn-download-drive" data-fid="${escapeHtml(f.id)}" data-fname="${escapeHtml(f.name)}">
                ⬇️ 下載並載入此備份至 iPhone
              </button>
            </div>
          `;
        })
        .join('');

      listEl.querySelectorAll('.btn-download-drive').forEach((btn) => {
        btn.onclick = () => {
          const fid = btn.getAttribute('data-fid');
          const fname = btn.getAttribute('data-fname');
          downloadAndRestoreFromDrive(fid, fname);
        };
      });
    } catch (err) {
      setSyncStatus(`讀取 Google Drive 失敗：${err.message || err}`, true);
    }
  }

  async function downloadAndRestoreFromDrive(fileId, fileName) {
    const token = state.driveAccessToken.trim();
    if (!token || !fileId) return;

    setSyncStatus(`正在從 Google Drive 下載「${fileName}」...`, false);
    try {
      const url = `https://www.googleapis.com/drive/v3/files/${encodeURIComponent(fileId)}?alt=media`;
      const resp = await fetch(url, {
        headers: { Authorization: `Bearer ${token}` }
      });
      if (!resp.ok) {
        throw new Error(`下載失敗 (HTTP ${resp.status})`);
      }
      const ab = await resp.arrayBuffer();
      await handleBackupZipBuffer(new Uint8Array(ab), fileName);
    } catch (err) {
      setSyncStatus(`雲端備份下載失敗：${err.message || err}`, true);
    }
  }

  // --- Toast Helper ---
  function showToast(msg) {
    const toast = document.getElementById('appToast');
    if (!toast) return;
    toast.textContent = msg;
    toast.classList.remove('hidden');
    clearTimeout(showToast._timer);
    showToast._timer = setTimeout(() => {
      toast.classList.add('hidden');
    }, 2800);
  }

  // --- Initialization & Event Wiring ---
  async function init() {
    if ('serviceWorker' in navigator && (location.protocol === 'https:' || location.hostname === 'localhost')) {
      navigator.serviceWorker.register('./sw.js').catch(() => {});
    }

    const searchInput = document.getElementById('searchInput');
    const clearSearchBtn = document.getElementById('clearSearchBtn');
    const sortSelect = document.getElementById('sortSelect');
    const openSyncBtn = document.getElementById('openSyncModalBtn');
    const emptyOpenSyncBtn = document.getElementById('emptyOpenSyncBtn');
    const emptyPickFileBtn = document.getElementById('emptyPickFileBtn');
    const closeSyncBtn = document.getElementById('closeSyncModalBtn');
    const fileInput = document.getElementById('backupFileInput');
    const pickFileBtn = document.getElementById('triggerFilePickerBtn');
    const googleClientIdInput = document.getElementById('googleClientIdInput');
    const googleLoginBtn = document.getElementById('googleOAuthLoginBtn');
    const refreshDriveBtn = document.getElementById('refreshDriveListBtn');
    const closeDetailBtn = document.getElementById('closeDetailModalBtn');
    const closePasswordBtn = document.getElementById('cancelPasswordBtn');
    const confirmPasswordBtn = document.getElementById('confirmPasswordBtn');
    const masterPasswordInput = document.getElementById('masterPasswordInput');
    const togglePassBtn = document.getElementById('togglePasswordVisBtn');
    const imageLightbox = document.getElementById('imageLightbox');

    googleClientIdInput.value = state.googleClientId;
    document.getElementById('currentOriginHint').textContent = window.location.origin;

    searchInput.addEventListener('input', () => {
      state.searchQuery = searchInput.value;
      clearSearchBtn.classList.toggle('hidden', !state.searchQuery);
      renderApp();
    });

    clearSearchBtn.onclick = () => {
      searchInput.value = '';
      state.searchQuery = '';
      clearSearchBtn.classList.add('hidden');
      renderApp();
    };

    sortSelect.onchange = () => {
      state.sortBy = sortSelect.value;
      renderApp();
    };

    const showSyncModal = () => {
      setSyncStatus('', false);
      document.getElementById('syncModal').classList.remove('hidden');
      if (state.driveAccessToken) {
        fetchGoogleDriveBackups();
      }
    };

    openSyncBtn.onclick = showSyncModal;
    if (emptyOpenSyncBtn) emptyOpenSyncBtn.onclick = showSyncModal;
    if (emptyPickFileBtn) emptyPickFileBtn.onclick = () => fileInput.click();
    closeSyncBtn.onclick = () => document.getElementById('syncModal').classList.add('hidden');

    pickFileBtn.onclick = () => fileInput.click();
    fileInput.onchange = (e) => {
      const f = e.target.files && e.target.files[0];
      if (f) handleFileImport(f);
      fileInput.value = '';
    };

    googleLoginBtn.onclick = startGoogleOAuthPopup;
    refreshDriveBtn.onclick = fetchGoogleDriveBackups;

    closeDetailBtn.onclick = () => document.getElementById('detailModal').classList.add('hidden');
    document.getElementById('detailModalBackdrop').onclick = () =>
      document.getElementById('detailModal').classList.add('hidden');

    closePasswordBtn.onclick = () => {
      state.pendingEncryptedContext = null;
      document.getElementById('passwordModal').classList.add('hidden');
    };
    confirmPasswordBtn.onclick = submitMasterPassword;
    masterPasswordInput.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') {
        e.preventDefault();
        submitMasterPassword();
      }
    });
    togglePassBtn.onclick = () => {
      const isPass = masterPasswordInput.type === 'password';
      masterPasswordInput.type = isPass ? 'text' : 'password';
      togglePassBtn.textContent = isPass ? '🙈' : '👁️';
    };

    imageLightbox.onclick = () => imageLightbox.classList.add('hidden');

    // Load cached database from iPhone IndexedDB on startup
    const cached = await loadFromIndexedDb();
    if (cached && cached.snapshot) {
      state.contacts = cached.snapshot.contacts || [];
      state.tagCatalog = cached.snapshot.tagCatalog || [];
      state.customFields = cached.snapshot.customFields || [];
      state.manifest = cached.snapshot.manifest || null;
      rebuildImageObjectUrls(cached.imgMap);
    }

    renderApp();
    checkOAuthRedirectFragment();
  }

  document.addEventListener('DOMContentLoaded', init);
})();
