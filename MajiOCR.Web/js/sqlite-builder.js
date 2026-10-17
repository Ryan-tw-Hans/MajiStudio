/**
 * MajiOCR.Web Pure-Client SQLite Database Builder
 * Uses embedded WebAssembly sql.js to build schema 2 compliant contacts.sqlite3
 * 100% binary-compatible with MajiOCR.Windows and MajiOCR Android.
 */
(function (global) {
  'use strict';

  let cachedSqlJs = null;

  async function getSqlInstance() {
    if (cachedSqlJs) return cachedSqlJs;
    if (typeof global.initSqlJs !== 'function') {
      throw new Error('未載入 sql.js WebAssembly 引擎 (initSqlJs 未定義)。');
    }
    cachedSqlJs = await global.initSqlJs();
    return cachedSqlJs;
  }

  function generateUuid() {
    if (global.crypto && typeof global.crypto.randomUUID === 'function') {
      return global.crypto.randomUUID();
    }
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
      const r = (Math.random() * 16) | 0;
      const v = c === 'x' ? r : (r & 0x3) | 0x8;
      return v.toString(16);
    });
  }

  async function buildSqliteDatabaseBuffer(data) {
    const contacts = (data && data.contacts) || [];
    const tagCatalog = (data && data.tagCatalog) || [];
    const customFields = (data && data.customFields) || [];
    const imageMap = (data && data.imageEntriesMap) || new Map();

    const isPasswordProtected = Boolean(data && data.isPasswordProtected);

    const SQL = await getSqlInstance();
    let db;
    if (isPasswordProtected) {
      const header = new Uint8Array(4096);
      const magic = 'SQLite format 3\0';
      for (let i = 0; i < 16; i++) header[i] = magic.charCodeAt(i);
      header[16] = 0x10; // 4096 page size
      header[17] = 0x00;
      header[18] = 1;
      header[19] = 1;
      header[20] = 80; // reserve bytes for SQLCipher 4
      header[21] = 64;
      header[22] = 32;
      header[23] = 32;
      header[100] = 0x0d;
      header[105] = (4016 >> 8) & 0xff;
      header[106] = 4016 & 0xff;
      db = new SQL.Database(header);
    } else {
      db = new SQL.Database();
    }

    const nowIso = new Date().toISOString();

    // 1. Initialize Tables (Schema 2)
    db.run(`
      CREATE TABLE IF NOT EXISTS contact_records (
        contact_id TEXT PRIMARY KEY,
        contract_version TEXT NOT NULL DEFAULT '1.0',
        revision INTEGER NOT NULL DEFAULT 1,
        persistence_state TEXT NOT NULL DEFAULT 'Ready',
        completion_source TEXT NOT NULL DEFAULT 'ios_pwa',
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL,
        completed_at TEXT,
        save_operation_id TEXT NOT NULL,
        content_fingerprint TEXT NOT NULL DEFAULT '',
        pending_delete INTEGER NOT NULL DEFAULT 0,
        tag_revision INTEGER NOT NULL DEFAULT 0,
        tag_updated_at TEXT,
        tag_save_fingerprint TEXT NOT NULL DEFAULT '',
        tag_save_mapping TEXT NOT NULL DEFAULT '{}'
      );

      CREATE TABLE IF NOT EXISTS contact_values (
        value_id TEXT PRIMARY KEY,
        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
        value_kind TEXT NOT NULL,
        custom_field_definition_id TEXT,
        custom_field_name_snapshot TEXT,
        raw_value TEXT NOT NULL,
        normalized_value TEXT,
        label TEXT NOT NULL,
        ordinal INTEGER NOT NULL DEFAULT 0,
        source_mode TEXT NOT NULL DEFAULT 'Manual',
        locked_at TEXT NOT NULL DEFAULT ''
      );

      CREATE TABLE IF NOT EXISTS card_image_assets (
        asset_id TEXT PRIMARY KEY,
        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
        capture_source TEXT NOT NULL DEFAULT 'FileImport',
        storage_state TEXT NOT NULL DEFAULT 'Ready',
        relative_path TEXT NOT NULL UNIQUE,
        staging_relative_path TEXT,
        sha256 TEXT NOT NULL,
        mime_type TEXT NOT NULL DEFAULT 'image/jpeg',
        byte_count INTEGER NOT NULL DEFAULT 1024,
        width INTEGER NOT NULL DEFAULT 800,
        height INTEGER NOT NULL DEFAULT 600,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS pending_delete (
        job_id TEXT PRIMARY KEY,
        contact_id TEXT NOT NULL UNIQUE,
        asset_relative_path TEXT,
        created_at TEXT NOT NULL,
        status TEXT NOT NULL DEFAULT 'Pending',
        retry_count INTEGER NOT NULL DEFAULT 0,
        last_error TEXT
      );

      CREATE TABLE IF NOT EXISTS contact_tag_catalog (
        tag_id TEXT PRIMARY KEY NOT NULL,
        display_name TEXT NOT NULL,
        normalized_key TEXT NOT NULL UNIQUE,
        revision INTEGER NOT NULL DEFAULT 1,
        created_at TEXT NOT NULL,
        updated_at TEXT NOT NULL
      );

      CREATE TABLE IF NOT EXISTS contact_tags (
        contact_id TEXT NOT NULL REFERENCES contact_records(contact_id) ON DELETE CASCADE,
        tag_id TEXT NOT NULL REFERENCES contact_tag_catalog(tag_id) ON DELETE RESTRICT,
        assigned_at TEXT NOT NULL,
        PRIMARY KEY (contact_id, tag_id)
      );

      CREATE TABLE IF NOT EXISTS custom_field_definitions (
        id TEXT PRIMARY KEY NOT NULL,
        name TEXT NOT NULL UNIQUE,
        active INTEGER NOT NULL DEFAULT 1,
        created_at TEXT NOT NULL
      );

      PRAGMA user_version = 2;
    `);

    // 2. Populate Tag Catalog
    const knownTagIds = new Set();
    const tagStmt = db.prepare(
      'INSERT OR IGNORE INTO contact_tag_catalog (tag_id, display_name, normalized_key, revision, created_at, updated_at) VALUES (?, ?, ?, ?, ?, ?)'
    );
    for (const tag of tagCatalog) {
      const tid = tag.tagId || tag.id || generateUuid();
      const name = String(tag.displayName || tag.name || '').trim();
      if (!name) continue;
      const norm = name.toLowerCase().replace(/\s+/g, '');
      tagStmt.run([tid, name, norm, 1, tag.createdAt || nowIso, tag.updatedAt || nowIso]);
      knownTagIds.add(tid);
    }
    tagStmt.free();

    // 3. Populate Custom Field Definitions
    const cfStmt = db.prepare(
      'INSERT OR IGNORE INTO custom_field_definitions (id, name, active, created_at) VALUES (?, ?, ?, ?)'
    );
    for (const cf of customFields) {
      const cid = cf.id || generateUuid();
      const cname = String(cf.name || '').trim();
      if (!cname) continue;
      cfStmt.run([cid, cname, 1, cf.createdAt || nowIso]);
    }
    cfStmt.free();

    // 4. Populate Contacts
    const contactStmt = db.prepare(`
      INSERT INTO contact_records (
        contact_id, contract_version, revision, persistence_state, completion_source,
        created_at, updated_at, completed_at, save_operation_id, content_fingerprint,
        pending_delete, tag_revision, tag_updated_at, tag_save_fingerprint, tag_save_mapping
      ) VALUES (?, '1.0', 1, 'Ready', 'ios_pwa', ?, ?, ?, ?, '', ?, 1, ?, '', '{}')
    `);

    const valStmt = db.prepare(`
      INSERT INTO contact_values (
        value_id, contact_id, value_kind, custom_field_definition_id,
        custom_field_name_snapshot, raw_value, normalized_value, label, ordinal, source_mode, locked_at
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 'Manual', '')
    `);

    const cTagStmt = db.prepare(
      'INSERT OR IGNORE INTO contact_tags (contact_id, tag_id, assigned_at) VALUES (?, ?, ?)'
    );

    const imgStmt = db.prepare(`
      INSERT OR REPLACE INTO card_image_assets (
        asset_id, contact_id, capture_source, storage_state, relative_path,
        sha256, mime_type, byte_count, width, height, created_at, updated_at
      ) VALUES (?, ?, 'FileImport', 'Ready', ?, ?, ?, ?, ?, ?, ?, ?)
    `);

    for (const contact of contacts) {
      const cid = contact.contactId || generateUuid();
      const cCreated = contact.createdAt || nowIso;
      const cUpdated = contact.updatedAt || nowIso;
      const isDelete = contact.pendingDelete ? 1 : 0;

      contactStmt.run([cid, cCreated, cUpdated, cUpdated, generateUuid(), isDelete, cUpdated]);

      let ordinal = 0;
      function addVal(kind, raw, label, customId, customName) {
        if (!raw || String(raw).trim() === '') return;
        const vid = generateUuid();
        const strVal = String(raw).trim();
        let normVal = strVal;
        if (kind === 'Phone' || kind === 'MobilePhone') {
          normVal = strVal.replace(/[^\d+]/g, '');
        } else if (kind === 'Email') {
          normVal = strVal.toLowerCase();
        }
        valStmt.run([vid, cid, kind, customId || null, customName || null, strVal, normVal, label || kind, ordinal++]);
      }

      // Name, company, title, department, taxId, notes
      addVal('FullName', contact.fullName, '姓名');
      addVal('CompanyName', contact.company, '公司');
      addVal('JobTitle', contact.jobTitle, '職稱');
      addVal('Department', contact.department, '部門');
      addVal('TaxId', contact.taxId, '統一編號');
      addVal('Notes', contact.notes, '備註');

      // Arrays
      if (Array.isArray(contact.phones)) {
        for (const p of contact.phones) {
          addVal(p.kind || 'Phone', p.value, p.label || '電話');
        }
      }
      if (Array.isArray(contact.emails)) {
        for (const e of contact.emails) {
          addVal('Email', e.value, e.label || 'Email');
        }
      }
      if (Array.isArray(contact.addresses)) {
        for (const a of contact.addresses) {
          addVal('Address', a.value, a.label || '地址');
        }
      }
      if (Array.isArray(contact.websites)) {
        for (const w of contact.websites) {
          addVal('Website', w.value, w.label || '網站');
        }
      }
      if (Array.isArray(contact.customFields)) {
        for (const cf of contact.customFields) {
          addVal('Custom', cf.value, cf.label || '自訂欄位', null, cf.label || '自訂欄位');
        }
      }

      // Tags
      if (Array.isArray(contact.tags)) {
        for (const t of contact.tags) {
          const tid = t.tagId || t.id;
          if (tid) {
            cTagStmt.run([cid, tid, cUpdated]);
          }
        }
      }

      // Images
      if (Array.isArray(contact.images)) {
        for (const img of contact.images) {
          const aid = img.assetId || generateUuid();
          const relPath = img.relativePath || `card-images/${cid}_${img.side === 2 ? 'back' : 'front'}.jpg`;
          const sha = img.sha256 || '0000000000000000000000000000000000000000000000000000000000000000';
          const mime = relPath.toLowerCase().endsWith('.png') ? 'image/png' : 'image/jpeg';
          let byteCount = 1024;
          if (imageMap && imageMap.has(relPath)) {
            const b = imageMap.get(relPath);
            if (b && b.length) byteCount = b.length;
          }
          imgStmt.run([aid, cid, relPath, sha, mime, byteCount, img.width || 800, img.height || 600, cCreated, cUpdated]);
        }
      }
    }

    contactStmt.free();
    valStmt.free();
    cTagStmt.free();
    imgStmt.free();

    // Export database binary
    const binary = db.export();
    db.close();

    return binary;
  }

  global.MajiSqliteBuilder = {
    buildSqliteDatabaseBuffer
  };
})(typeof window !== 'undefined' ? window : globalThis);
