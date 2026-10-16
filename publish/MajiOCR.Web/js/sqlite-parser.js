/**
 * MajiOCR.Web Pure-JS SQLite Format 3 B-Tree Reader & Contact Mapper
 * Directly parses decrypted/plain contacts.sqlite3 buffers (supports both Windows EAV & Android schemas)
 * with 100% offline capability and zero external WASM/CDN dependencies.
 */
(function (global) {
  'use strict';

  const utf8Decoder = new TextDecoder('utf-8');

  function readUint16BE(buf, pos) {
    return (buf[pos] << 8) | buf[pos + 1];
  }

  function readUint32BE(buf, pos) {
    return (
      ((buf[pos] << 24) |
        (buf[pos + 1] << 16) |
        (buf[pos + 2] << 8) |
        buf[pos + 3]) >>>
      0
    );
  }

  // Reads a SQLite 1-to-9-byte big-endian variable-length integer (varint)
  function readVarint(buf, pos) {
    let result = 0;
    for (let i = 0; i < 8; i++) {
      const b = buf[pos + i];
      result = result * 128 + (b & 0x7f);
      if ((b & 0x80) === 0) {
        return { value: result, length: i + 1 };
      }
    }
    const b9 = buf[pos + 8];
    result = result * 256 + b9;
    return { value: result, length: 9 };
  }

  function readSignedIntBE(buf, pos, byteLen) {
    if (byteLen === 1) {
      const v = buf[pos];
      return v & 0x80 ? v - 0x100 : v;
    }
    if (byteLen === 2) {
      const v = (buf[pos] << 8) | buf[pos + 1];
      return v & 0x8000 ? v - 0x10000 : v;
    }
    if (byteLen === 3) {
      const v = (buf[pos] << 16) | (buf[pos + 1] << 8) | buf[pos + 2];
      return v & 0x800000 ? v - 0x1000000 : v;
    }
    if (byteLen === 4) {
      return (buf[pos] << 24) | (buf[pos + 1] << 16) | (buf[pos + 2] << 8) | buf[pos + 3];
    }
    // 6 or 8 bytes: use DataView or BigInt safe number conversion
    const dv = new DataView(buf.buffer, buf.byteOffset + pos, byteLen);
    if (byteLen === 6) {
      const hi = dv.getInt16(0, false);
      const lo = dv.getUint32(2, false);
      return hi * 4294967296 + lo;
    }
    if (byteLen === 8) {
      return Number(dv.getBigInt64(0, false));
    }
    return 0;
  }

  function readFloat64BE(buf, pos) {
    const dv = new DataView(buf.buffer, buf.byteOffset + pos, 8);
    return dv.getFloat64(0, false);
  }

  function assembleCellPayload(dbBytes, pageSize, usableSize, localPayloadStart, totalPayloadSize, maxLocal, minLocal) {
    let localSize = totalPayloadSize;
    if (totalPayloadSize > maxLocal) {
      const surplus = minLocal + ((totalPayloadSize - minLocal) % (usableSize - 4));
      localSize = surplus <= maxLocal ? surplus : minLocal;
    }

    if (localSize >= totalPayloadSize) {
      return dbBytes.subarray(localPayloadStart, localPayloadStart + totalPayloadSize);
    }

    const full = new Uint8Array(totalPayloadSize);
    full.set(dbBytes.subarray(localPayloadStart, localPayloadStart + localSize), 0);
    let written = localSize;
    let nextOverflowPage = readUint32BE(dbBytes, localPayloadStart + localSize);

    while (written < totalPayloadSize && nextOverflowPage > 0) {
      const ovPageStart = (nextOverflowPage - 1) * pageSize;
      if (ovPageStart + usableSize > dbBytes.length) break;
      nextOverflowPage = readUint32BE(dbBytes, ovPageStart);
      const chunkLen = Math.min(usableSize - 4, totalPayloadSize - written);
      full.set(dbBytes.subarray(ovPageStart + 4, ovPageStart + 4 + chunkLen), written);
      written += chunkLen;
    }
    return full;
  }

  function decodeRecordPayload(payload) {
    const hdr = readVarint(payload, 0);
    const headerSize = hdr.value;
    let hdrPos = hdr.length;
    const serialTypes = [];

    while (hdrPos < headerSize) {
      const st = readVarint(payload, hdrPos);
      serialTypes.push(st.value);
      hdrPos += st.length;
    }

    let bodyPos = headerSize;
    const values = [];
    for (let i = 0; i < serialTypes.length; i++) {
      const st = serialTypes[i];
      if (st === 0) {
        values.push(null);
      } else if (st === 1) {
        values.push(readSignedIntBE(payload, bodyPos, 1));
        bodyPos += 1;
      } else if (st === 2) {
        values.push(readSignedIntBE(payload, bodyPos, 2));
        bodyPos += 2;
      } else if (st === 3) {
        values.push(readSignedIntBE(payload, bodyPos, 3));
        bodyPos += 3;
      } else if (st === 4) {
        values.push(readSignedIntBE(payload, bodyPos, 4));
        bodyPos += 4;
      } else if (st === 5) {
        values.push(readSignedIntBE(payload, bodyPos, 6));
        bodyPos += 6;
      } else if (st === 6) {
        values.push(readSignedIntBE(payload, bodyPos, 8));
        bodyPos += 8;
      } else if (st === 7) {
        values.push(readFloat64BE(payload, bodyPos));
        bodyPos += 8;
      } else if (st === 8) {
        values.push(0);
      } else if (st === 9) {
        values.push(1);
      } else if (st >= 12 && st % 2 === 0) {
        const len = (st - 12) / 2;
        values.push(payload.slice(bodyPos, bodyPos + len));
        bodyPos += len;
      } else if (st >= 13 && st % 2 === 1) {
        const len = (st - 13) / 2;
        values.push(utf8Decoder.decode(payload.subarray(bodyPos, bodyPos + len)));
        bodyPos += len;
      } else {
        values.push(null);
      }
    }
    return values;
  }

  function readTableBTreeRows(dbBytes, pageSize, usableSize, rootPageNo) {
    const rows = [];
    const maxLocal = usableSize - 35;
    const minLocal = (((usableSize - 12) * 32 / 255) | 0) - 23;
    const visitedPages = new Set();

    function traversePage(pageNo) {
      if (pageNo <= 0 || visitedPages.has(pageNo)) return;
      visitedPages.add(pageNo);

      const pageStart = (pageNo - 1) * pageSize;
      if (pageStart + pageSize > dbBytes.length) return;

      const hdrOffset = pageNo === 1 ? 100 : 0;
      const pageType = dbBytes[pageStart + hdrOffset];
      const cellCount = readUint16BE(dbBytes, pageStart + hdrOffset + 3);

      if (pageType === 0x05) {
        // Interior Table B-Tree Page
        const rightMostChild = readUint32BE(dbBytes, pageStart + hdrOffset + 8);
        const cellPtrStart = pageStart + hdrOffset + 12;
        for (let i = 0; i < cellCount; i++) {
          const cellOffset = readUint16BE(dbBytes, cellPtrStart + i * 2);
          const leftChildPage = readUint32BE(dbBytes, pageStart + cellOffset);
          traversePage(leftChildPage);
        }
        traversePage(rightMostChild);
      } else if (pageType === 0x0d) {
        // Leaf Table B-Tree Page
        const cellPtrStart = pageStart + hdrOffset + 8;
        for (let i = 0; i < cellCount; i++) {
          const cellOffset = readUint16BE(dbBytes, cellPtrStart + i * 2);
          let pos = pageStart + cellOffset;
          const pSizeVar = readVarint(dbBytes, pos);
          pos += pSizeVar.length;
          const rowIdVar = readVarint(dbBytes, pos);
          pos += rowIdVar.length;

          const payload = assembleCellPayload(
            dbBytes,
            pageSize,
            usableSize,
            pos,
            pSizeVar.value,
            maxLocal,
            minLocal
          );
          const values = decodeRecordPayload(payload);
          rows.push({ rowId: rowIdVar.value, values });
        }
      }
    }

    traversePage(rootPageNo);
    return rows;
  }

  // Extracts ordered column definitions from a SQLite CREATE TABLE statement
  function parseCreateTableColumns(createSql) {
    if (!createSql) return [];
    const openParen = createSql.indexOf('(');
    const closeParen = createSql.lastIndexOf(')');
    if (openParen < 0 || closeParen <= openParen) return [];

    const body = createSql.slice(openParen + 1, closeParen);
    // Split by top-level commas (ignoring commas inside nested parentheses like CHECK(...) or UNIQUE(...))
    const parts = [];
    let depth = 0;
    let current = '';
    let inSingleQuote = false;
    for (let i = 0; i < body.length; i++) {
      const ch = body[i];
      if (ch === "'") inSingleQuote = !inSingleQuote;
      if (!inSingleQuote) {
        if (ch === '(') depth++;
        else if (ch === ')') depth = Math.max(0, depth - 1);
        else if (ch === ',' && depth === 0) {
          parts.push(current.trim());
          current = '';
          continue;
        }
      }
      current += ch;
    }
    if (current.trim().length > 0) parts.push(current.trim());

    const columns = [];
    const constraintPrefixes = /^(PRIMARY\s+KEY|FOREIGN\s+KEY|UNIQUE|CHECK|CONSTRAINT)\b/i;

    for (const part of parts) {
      const cleaned = part.replace(/--.*$/gm, '').trim();
      if (!cleaned || constraintPrefixes.test(cleaned)) continue;

      const match = cleaned.match(/^(?:["`\[]([^"`\]]+)["`\]]|([A-Za-z_][A-Za-z0-9_]*))\s*(.*)$/s);
      if (match) {
        const colName = match[1] || match[2];
        const rest = match[3] || '';
        const isIntPk = /\bINTEGER\s+PRIMARY\s+KEY\b/i.test(rest);
        columns.push({ name: colName, isIntegerPrimaryKey: isIntPk });
      }
    }
    return columns;
  }

  function parseSqliteDatabase(dbBytes) {
    const bytes = dbBytes instanceof Uint8Array ? dbBytes : new Uint8Array(dbBytes);
    if (bytes.length < 100) {
      throw new Error('無效的 SQLite 資料庫檔案 (長度小於 100 bytes)。');
    }

    let pageSize = readUint16BE(bytes, 16);
    if (pageSize === 1) pageSize = 65536;
    const reservedBytes = bytes[20] || 0;
    const usableSize = pageSize - reservedBytes;

    // 1. Read sqlite_master (rootpage = 1)
    // Columns of sqlite_master: type (0), name (1), tbl_name (2), rootpage (3), sql (4)
    const masterRows = readTableBTreeRows(bytes, pageSize, usableSize, 1);
    const tables = new Map();

    for (const r of masterRows) {
      const [type, name, tblName, rootpage, sql] = r.values;
      if (type === 'table' && name && rootpage > 0) {
        const cols = parseCreateTableColumns(sql);
        tables.set(name, { name, tblName, rootpage, sql, columns: cols });
      }
    }

    function queryTableObjects(tableName) {
      const meta = tables.get(tableName);
      if (!meta) return [];
      const rawRows = readTableBTreeRows(bytes, pageSize, usableSize, meta.rootpage);
      return rawRows.map((r) => {
        const obj = {};
        meta.columns.forEach((col, idx) => {
          let val = r.values[idx];
          if (col.isIntegerPrimaryKey && (val === null || val === undefined)) {
            val = r.rowId;
          }
          obj[col.name] = val;
        });
        return obj;
      });
    }

    return {
      pageSize,
      reservedBytes,
      tables: Array.from(tables.keys()),
      queryTableObjects
    };
  }

  /**
   * High-level mapper: extracts MajiOCR contacts, fields, tags, customFieldDefinitions, and image assets
   * from a parsed SQLite database (compatible with both Windows and Android schemas).
   */
  function extractMajiOcrContacts(dbBytes) {
    const db = parseSqliteDatabase(dbBytes);
    const contactRecords = db.queryTableObjects('contact_records');
    const contactValues = db.queryTableObjects('contact_values');
    const imageAssets = db.queryTableObjects('card_image_assets');
    const tagCatalog = db.queryTableObjects('contact_tag_catalog');
    const contactTags = db.queryTableObjects('contact_tags');
    const pendingDeletes = db.queryTableObjects('pending_delete');
    const customFieldDefs = db.queryTableObjects('custom_field_definitions');

    const pendingDeleteSet = new Set();
    for (const pd of pendingDeletes) {
      if (pd.contact_id) pendingDeleteSet.add(pd.contact_id);
    }

    const customFieldMap = new Map();
    for (const cfd of customFieldDefs) {
      if (cfd.id && cfd.name) {
        customFieldMap.set(cfd.id, cfd.name);
      }
    }

    const tagsById = new Map();
    const sortedTagCatalog = [];
    for (const t of tagCatalog) {
      if (!t.tag_id || !t.display_name) continue;
      const tagItem = {
        tagId: t.tag_id,
        displayName: String(t.display_name),
        normalizedKey: String(t.normalized_key || t.display_name).toLowerCase(),
        updatedAt: t.updated_at || ''
      };
      tagsById.set(tagItem.tagId, tagItem);
      sortedTagCatalog.push(tagItem);
    }
    sortedTagCatalog.sort((a, b) => a.displayName.localeCompare(b.displayName, 'zh-Hant'));

    const tagsByContact = new Map();
    for (const ct of contactTags) {
      if (!ct.contact_id || !ct.tag_id) continue;
      const tagObj = tagsById.get(ct.tag_id);
      if (!tagObj) continue;
      if (!tagsByContact.has(ct.contact_id)) tagsByContact.set(ct.contact_id, []);
      tagsByContact.get(ct.contact_id).push(tagObj);
    }

    const imagesByContact = new Map();
    for (const img of imageAssets) {
      if (!img.contact_id || !img.relative_path) continue;
      if (img.storage_state && img.storage_state === 'PendingDelete') continue;
      if (!imagesByContact.has(img.contact_id)) imagesByContact.set(img.contact_id, []);
      imagesByContact.get(img.contact_id).push({
        assetId: img.asset_id || '',
        relativePath: String(img.relative_path).replace(/\\/g, '/'),
        mimeType: img.mime_type || 'image/jpeg',
        width: img.width || 0,
        height: img.height || 0
      });
    }

    // Group contact_values by contact_id (detecting Windows EAV vs Android wide schema)
    const valuesByContact = new Map();
    for (const cv of contactValues) {
      const cid = cv.contact_id;
      if (!cid) continue;
      if (!valuesByContact.has(cid)) valuesByContact.set(cid, []);
      valuesByContact.get(cid).push(cv);
    }

    const contacts = [];
    for (const rec of contactRecords) {
      const cid = rec.contact_id;
      if (!cid) continue;
      if (rec.pending_delete === 1 || pendingDeleteSet.has(cid)) continue;

      const rawVals = valuesByContact.get(cid) || [];
      let fullName = '';
      let jobTitle = '';
      let company = '';
      let department = '';
      let taxId = '';
      let notes = '';
      const phones = [];
      const emails = [];
      const addresses = [];
      const websites = [];
      const customFields = [];
      const allFields = [];

      // Check if Android wide schema (has full_name column) or Windows EAV schema (has value_kind column)
      if (rawVals.length > 0 && 'full_name' in rawVals[0] && !('value_kind' in rawVals[0])) {
        const w = rawVals[0];
        fullName = w.full_name || '';
        company = w.company || '';
        jobTitle = w.job_title || '';
        if (w.mobile_phone) phones.push({ label: '手機', value: w.mobile_phone });
        if (w.business_phone) phones.push({ label: '公司電話', value: w.business_phone });
        if (w.email) emails.push({ label: 'Email', value: w.email });
        if (w.address) addresses.push({ label: '地址', value: w.address });
        if (w.website) websites.push({ label: '網站', value: w.website });
        notes = w.note || '';
        if (w.custom_fields_json) {
          try {
            const parsed = JSON.parse(w.custom_fields_json);
            if (Array.isArray(parsed)) {
              for (const cf of parsed) {
                const cfLabel = cf.name || cf.label || '自訂欄位';
                const cfVal = cf.value || '';
                if (cfVal) customFields.push({ label: cfLabel, value: cfVal });
              }
            }
          } catch (_) {}
        }
      } else {
        // Windows EAV schema: sort by ordinal
        rawVals.sort((a, b) => (a.ordinal || 0) - (b.ordinal || 0));
        for (const item of rawVals) {
          const kind = String(item.value_kind || '');
          const val = String(item.raw_value || '').trim();
          if (!val) continue;
          const label = String(item.label || kind);

          if (kind === 'Name' || kind === 'FullName') {
            if (!fullName) fullName = val;
            allFields.push({ kind, label: '姓名', value: val });
          } else if (kind === 'Title' || kind === 'JobTitle') {
            if (!jobTitle) jobTitle = val;
            allFields.push({ kind, label: '職稱', value: val });
          } else if (kind === 'Company' || kind === 'CompanyName') {
            if (!company) company = val;
            allFields.push({ kind, label: '公司', value: val });
          } else if (kind === 'Department') {
            if (!department) department = val;
            allFields.push({ kind, label: '部門', value: val });
          } else if (kind === 'Phone' || kind === 'MobilePhone' || kind === 'WorkPhone' || kind === 'Fax') {
            const phoneLabel = kind === 'MobilePhone' ? '手機' : kind === 'Fax' ? '傳真' : (label || '電話');
            phones.push({ label: phoneLabel, value: val, kind });
            allFields.push({ kind, label: phoneLabel, value: val });
          } else if (kind === 'Email') {
            emails.push({ label: label || 'Email', value: val });
            allFields.push({ kind, label: 'Email', value: val });
          } else if (kind === 'Address') {
            addresses.push({ label: label || '地址', value: val });
            allFields.push({ kind, label: '地址', value: val });
          } else if (kind === 'Website') {
            websites.push({ label: label || '網站', value: val });
            allFields.push({ kind, label: '網站', value: val });
          } else if (kind === 'TaxId') {
            if (!taxId) taxId = val;
            allFields.push({ kind, label: '統一編號', value: val });
          } else if (kind === 'Note' || kind === 'Notes') {
            notes = notes ? `${notes}\n${val}` : val;
            allFields.push({ kind, label: '備註', value: val });
          } else if (kind === 'Custom' || item.custom_field_definition_id || item.custom_field_name_snapshot) {
            const cfName =
              item.custom_field_name_snapshot ||
              customFieldMap.get(item.custom_field_definition_id) ||
              label ||
              '自訂欄位';
            customFields.push({ label: cfName, value: val });
            allFields.push({ kind: 'Custom', label: cfName, value: val });
          } else {
            customFields.push({ label, value: val });
            allFields.push({ kind, label, value: val });
          }
        }
      }

      const contactTagsList = tagsByContact.get(cid) || [];
      const contactImages = imagesByContact.get(cid) || [];

      const searchableChunks = [
        fullName,
        jobTitle,
        company,
        department,
        taxId,
        notes,
        ...phones.map((p) => p.value),
        ...emails.map((e) => e.value),
        ...addresses.map((a) => a.value),
        ...websites.map((w) => w.value),
        ...customFields.map((c) => `${c.label} ${c.value}`),
        ...contactTagsList.map((t) => t.displayName)
      ];

      contacts.push({
        contactId: cid,
        fullName: fullName || '(未命名名片)',
        jobTitle,
        company,
        department,
        taxId,
        notes,
        phones,
        emails,
        addresses,
        websites,
        customFields,
        allFields,
        tags: contactTagsList,
        images: contactImages,
        createdAt: rec.created_at || '',
        updatedAt: rec.updated_at || '',
        searchIndexText: searchableChunks.join(' ').toLowerCase()
      });
    }

    // Default sort: newest created_at first
    contacts.sort((a, b) => (b.createdAt || '').localeCompare(a.createdAt || ''));

    return {
      contacts,
      tagCatalog: sortedTagCatalog,
      customFields: Array.from(customFieldMap.entries()).map(([id, name]) => ({ id, name })),
      tables: db.tables
    };
  }

  global.MajiSqliteParser = {
    parseSqliteDatabase,
    extractMajiOcrContacts
  };
})(typeof window !== 'undefined' ? window : globalThis);
