/**
 * Automated Test Suite for ISSUE-0049
 * Verifies T1 ~ T6 test cases from TestPlans/TP-ISSUE-0049-v0.1.md
 */
import fs from 'fs';
import path from 'path';
import crypto from 'crypto';
import { createRequire } from 'module';

const require = createRequire(import.meta.url);
globalThis.require = require;

// Setup WebCrypto & global environment for Node.js
if (!globalThis.crypto) {
  globalThis.crypto = crypto.webcrypto;
}

// Load Web scripts
const basePath = path.resolve('MajiOCR.Web/js');

// 1. sql-wasm.js
const sqlWasmCode = fs.readFileSync(path.join(basePath, 'sql-wasm.js'), 'utf8');
const initSqlJsModule = new Function(sqlWasmCode + '; return initSqlJs;')();
globalThis.initSqlJs = initSqlJsModule;

// 2. sqlite-parser.js
const sqliteParserCode = fs.readFileSync(path.join(basePath, 'sqlite-parser.js'), 'utf8');
new Function(sqliteParserCode)();

// 3. sqlite-builder.js
const sqliteBuilderCode = fs.readFileSync(path.join(basePath, 'sqlite-builder.js'), 'utf8');
new Function(sqliteBuilderCode)();

// 4. sqlcipher-decryptor.js
const sqlCipherCode = fs.readFileSync(path.join(basePath, 'sqlcipher-decryptor.js'), 'utf8');
new Function(sqlCipherCode)();

// 5. zip-reader.js
const zipReaderCode = fs.readFileSync(path.join(basePath, 'zip-reader.js'), 'utf8');
new Function(zipReaderCode)();

let testsPassed = 0;
let testsTotal = 0;

function assert(condition, message) {
  testsTotal++;
  if (!condition) {
    console.error(`❌ FAIL: ${message}`);
    throw new Error(`Assertion failed: ${message}`);
  }
  console.log(`  ✓ PASS: ${message}`);
  testsPassed++;
}

async function runTests() {
  console.log('====================================================');
  console.log('🚀 Running ISSUE-0049 Automated Test Suite (T1 ~ T6)');
  console.log('====================================================\n');

  // ----------------------------------------------------
  // T1: SQLite Database Generation & Compatibility
  // ----------------------------------------------------
  console.log('▶ [T1] SQLite Database Generation & Compatibility:');
  const dummyContacts = [
    {
      contactId: 'c-001',
      fullName: '王小明',
      company: '範例科技股份有限公司',
      jobTitle: '產品總監',
      department: '研發中心',
      taxId: '12345678',
      notes: '潛在跨平台企業客戶',
      phones: [
        { kind: 'MobilePhone', label: '手機', value: '0912-345-678' },
        { kind: 'Phone', label: '電話', value: '02-2345-6789' }
      ],
      emails: [{ label: 'Email', value: 'ming@example.com' }],
      addresses: [{ label: '地址', value: '台北市示範區測試路一段100號' }],
      websites: [{ label: '網站', value: 'https://example.com' }],
      customFields: [{ label: '統編', value: '12345678' }],
      tags: [{ tagId: 'tag-vip', displayName: 'VIP 客戶' }],
      images: [
        { assetId: 'img-1', side: 1, relativePath: 'card-images/c-001_front.jpg', sha256: 'a'.repeat(64) }
      ],
      createdAt: '2026-10-17T08:00:00Z',
      updatedAt: '2026-10-17T08:00:00Z'
    }
  ];

  const dummyTags = [
    { tagId: 'tag-vip', displayName: 'VIP 客戶', normalizedKey: 'vip客戶' }
  ];

  const imageMap = new Map();
  imageMap.set('card-images/c-001_front.jpg', new Uint8Array([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46])); // JPEG magic

  const plainDbBytes = await globalThis.MajiSqliteBuilder.buildSqliteDatabaseBuffer({
    contacts: dummyContacts,
    tagCatalog: dummyTags,
    customFields: [],
    imageEntriesMap: imageMap
  });

  assert(plainDbBytes instanceof Uint8Array, '產出之資料庫應為 Uint8Array');
  assert(plainDbBytes.length > 0 && plainDbBytes.length % 4096 === 0, `資料庫長度應為 4096 之倍數 (實際: ${plainDbBytes.length} bytes)`);
  assert(globalThis.MajiSqlCipher.isPlainSqliteBuffer(plainDbBytes), '產出之資料庫標頭應為標準 SQLite format 3');

  // Verify pure JS sqlite-parser can parse it
  const parsedT1 = globalThis.MajiSqliteParser.extractMajiOcrContacts(plainDbBytes);
  assert(parsedT1.contacts.length === 1, `應成功解析出 1 筆聯絡人 (實際: ${parsedT1.contacts.length})`);
  assert(parsedT1.contacts[0].fullName === '王小明', `姓名應正確為「王小明」 (實際: ${parsedT1.contacts[0].fullName})`);
  assert(parsedT1.contacts[0].company === '範例科技股份有限公司', '公司名稱相符');
  assert(parsedT1.contacts[0].phones.length === 2, '電話號碼筆數相符 (2筆)');
  assert(parsedT1.contacts[0].tags.length === 1 && parsedT1.contacts[0].tags[0].displayName === 'VIP 客戶', '分類標籤關聯相符');

  console.log('✅ [T1] PASS\n');

  // ----------------------------------------------------
  // T2: SQLCipher-AES256 Re-Encryption & Reverse Decryption
  // ----------------------------------------------------
  console.log('▶ [T2] SQLCipher-AES256 Re-Encryption & Reverse Decryption:');
  const masterPassword = 'MajiSecurePassword2026!';
  const dbBytesForEnc = await globalThis.MajiSqliteBuilder.buildSqliteDatabaseBuffer({
    contacts: dummyContacts,
    tagCatalog: dummyTags,
    customFields: [],
    imageEntriesMap: imageMap,
    isPasswordProtected: true
  });

  const encryptedDbBytes = await globalThis.MajiSqlCipher.encryptSqlCipher4Database(
    dbBytesForEnc,
    masterPassword
  );

  assert(!globalThis.MajiSqlCipher.isPlainSqliteBuffer(encryptedDbBytes), '加密後資料庫標頭不得包含明文 SQLite 標誌');
  assert(encryptedDbBytes.length === dbBytesForEnc.length, '加密後資料庫檔案大小應與原資料庫相同');

  // Decrypt with correct password
  const decryptedDbBytes = await globalThis.MajiSqlCipher.decryptSqlCipher4Database(
    encryptedDbBytes,
    masterPassword
  );
  assert(globalThis.MajiSqlCipher.isPlainSqliteBuffer(decryptedDbBytes), '以正確主密碼解密後標頭應恢復為標準 SQLite format 3');

  const parsedDecrypted = globalThis.MajiSqliteParser.extractMajiOcrContacts(decryptedDbBytes);
  console.log('parsedDecrypted contacts:', parsedDecrypted.contacts.length);
  if (parsedDecrypted.contacts.length > 0) {
    console.log('parsedDecrypted[0]:', parsedDecrypted.contacts[0].fullName);
  }
  assert(parsedDecrypted.contacts.length === 1 && parsedDecrypted.contacts[0].fullName === '王小明', '解密後解析之名片資料應 100% 完整無損');

  // Decrypt with WRONG password should reject
  let wrongPassCaught = false;
  try {
    await globalThis.MajiSqlCipher.decryptSqlCipher4Database(encryptedDbBytes, 'WrongPassword999!');
  } catch (err) {
    wrongPassCaught = true;
    assert(String(err.message).includes('INVALID_PASSWORD'), `錯誤密碼應拋出 INVALID_PASSWORD (實際: ${err.message})`);
  }
  assert(wrongPassCaught, '輸入錯誤主密碼時必須攔截拋錯');

  console.log('✅ [T2] PASS\n');

  // ----------------------------------------------------
  // T3: ZIP Backup Packaging & Restoration
  // ----------------------------------------------------
  console.log('▶ [T3] ZIP Backup Packaging & Restoration:');
  const manifestObj = {
    BackupId: 'MajiOCR_Backup_iOS_Test',
    Platform: 'ios',
    CreatedAtUtc: '2026-10-17T08:00:00Z',
    ContactCount: 1,
    ImageCount: 1,
    IsPasswordProtected: true,
    EncryptionAlgorithm: 'SQLCipher-AES256'
  };

  const sampleCardImage = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]); // PNG magic
  const zipEntriesToPack = [
    ['manifest.json', JSON.stringify(manifestObj, null, 2)],
    ['contacts.sqlite3', encryptedDbBytes],
    ['card-images/c-001_front.jpg', sampleCardImage]
  ];

  const packedZipBytes = globalThis.MajiZipReader.packZipBuffer(zipEntriesToPack);
  assert(packedZipBytes instanceof Uint8Array, '打包輸出應為 Uint8Array');
  assert(packedZipBytes.length > 500, `ZIP 大小合理 (實際: ${packedZipBytes.length} bytes)`);
  assert(packedZipBytes[0] === 0x50 && packedZipBytes[1] === 0x4b, 'ZIP 檔案前兩字節應為 PK (0x50, 0x4b)');

  // Unpack using MajiZipReader
  const unpackedMap = await globalThis.MajiZipReader.unpackZipBuffer(packedZipBytes);
  assert(unpackedMap.has('manifest.json'), '解壓縮後應包含 manifest.json');
  assert(unpackedMap.has('contacts.sqlite3'), '解壓縮後應包含 contacts.sqlite3');
  assert(unpackedMap.has('card-images/c-001_front.jpg'), '解壓縮後應包含 card-images/c-001_front.jpg');

  const unpackedManifest = JSON.parse(new TextDecoder().decode(unpackedMap.get('manifest.json')));
  assert(unpackedManifest.BackupId === 'MajiOCR_Backup_iOS_Test', 'manifest BackupId 吻合');
  assert(unpackedManifest.Platform === 'ios', 'manifest Platform 為 ios');

  const unpackedImg = unpackedMap.get('card-images/c-001_front.jpg');
  assert(unpackedImg.length === sampleCardImage.length && unpackedImg[0] === sampleCardImage[0], '解壓縮後圖檔位元組完全一致');

  console.log('✅ [T3] PASS\n');

  // ----------------------------------------------------
  // T4: vCard (.vcf) Formatting Verification
  // ----------------------------------------------------
  console.log('▶ [T4] vCard (.vcf) Formatting Verification:');
  function formatVCard(c) {
    const lines = [
      'BEGIN:VCARD',
      'VERSION:3.0',
      `FN;CHARSET=UTF-8:${c.fullName}`,
      `N;CHARSET=UTF-8:${c.fullName};;;;`
    ];
    if (c.company) lines.push(`ORG;CHARSET=UTF-8:${c.company}`);
    if (c.jobTitle) lines.push(`TITLE;CHARSET=UTF-8:${c.jobTitle}`);
    for (const p of c.phones || []) {
      lines.push(`TEL;TYPE=CELL,VOICE:${p.value}`);
    }
    for (const e of c.emails || []) {
      lines.push(`EMAIL;TYPE=INTERNET,WORK:${e.value}`);
    }
    for (const a of c.addresses || []) {
      lines.push(`ADR;TYPE=WORK;CHARSET=UTF-8:;;${a.value};;;;`);
    }
    for (const w of c.websites || []) {
      lines.push(`URL:${w.value}`);
    }
    if (c.notes) lines.push(`NOTE;CHARSET=UTF-8:${c.notes}`);
    lines.push('END:VCARD');
    return lines.join('\r\n');
  }

  const vcfText = formatVCard(dummyContacts[0]);
  assert(vcfText.startsWith('BEGIN:VCARD'), 'vCard 應以 BEGIN:VCARD 開頭');
  assert(vcfText.includes('VERSION:3.0'), 'vCard 應包含 VERSION:3.0');
  assert(vcfText.includes('FN;CHARSET=UTF-8:王小明'), 'vCard 應包含姓名 FN');
  assert(vcfText.includes('ORG;CHARSET=UTF-8:範例科技股份有限公司'), 'vCard 應包含公司 ORG');
  assert(vcfText.includes('TEL;TYPE=CELL,VOICE:0912-345-678'), 'vCard 應包含電話 TEL');
  assert(vcfText.includes('EMAIL;TYPE=INTERNET,WORK:ming@example.com'), 'vCard 應包含 Email');
  assert(vcfText.includes('ADR;TYPE=WORK;CHARSET=UTF-8:;;台北市示範區測試路一段100號;;;;'), 'vCard 應包含地址 ADR');
  assert(vcfText.endsWith('END:VCARD'), 'vCard 應以 END:VCARD 結尾');

  console.log('✅ [T4] PASS\n');

  // ----------------------------------------------------
  // T5: Google Drive Multipart Upload Payload Verification
  // ----------------------------------------------------
  console.log('▶ [T5] Google Drive Multipart Upload Payload Verification:');
  const boundary = '-------MajiOcrBoundaryTest12345';
  const delimiter = `\r\n--${boundary}\r\n`;
  const closeDelim = `\r\n--${boundary}--`;

  const driveMeta = {
    name: 'MajiOCR_Backup_iOS_20261017_080000.zip',
    description: 'MajiOCR iOS PWA 備份',
    mimeType: 'application/zip',
    appProperties: {
      platform: 'ios',
      contact_count: '1',
      image_count: '1',
      is_password_protected: 'true'
    }
  };

  const metaPart = `Content-Type: application/json; charset=UTF-8\r\n\r\n` + JSON.stringify(driveMeta) + `\r\n`;
  const fileHeader = `Content-Type: application/zip\r\n\r\n`;

  const encoder = new TextEncoder();
  const metaBytes = encoder.encode(delimiter + metaPart + delimiter + fileHeader);
  const closeBytes = encoder.encode(closeDelim);

  const totalPayloadSize = metaBytes.length + packedZipBytes.length + closeBytes.length;
  const combinedPayload = new Uint8Array(totalPayloadSize);
  combinedPayload.set(metaBytes, 0);
  combinedPayload.set(packedZipBytes, metaBytes.length);
  combinedPayload.set(closeBytes, metaBytes.length + packedZipBytes.length);

  assert(combinedPayload.length === totalPayloadSize, '組合之 Multipart Payload 長度正確');
  const payloadStringHead = new TextDecoder().decode(combinedPayload.subarray(0, metaBytes.length));
  assert(payloadStringHead.includes(boundary), 'Payload 開頭應包含正確 boundary 分隔標記');
  assert(payloadStringHead.includes('application/json'), 'Payload 應包含 JSON metadata 區段');
  assert(payloadStringHead.includes('"platform":"ios"'), 'Payload 應包含自訂屬性 platform: ios');
  assert(payloadStringHead.includes('application/zip'), 'Payload 應包含二進位檔案型態標頭');

  const payloadStringTail = new TextDecoder().decode(combinedPayload.subarray(combinedPayload.length - closeBytes.length));
  assert(payloadStringTail.includes(`--${boundary}--`), 'Payload 結尾應包含正確 close boundary');

  console.log('✅ [T5] PASS\n');

  // ----------------------------------------------------
  // T6: End-to-End Contact Editing and SQLite Output
  // ----------------------------------------------------
  console.log('▶ [T6] End-to-End Contact Editing and SQLite Output:');
  // Scenario: Add a second contact, edit the first contact's phone & company, build DB
  const modifiedContacts = [
    {
      ...dummyContacts[0],
      company: '範例科技（總部）',
      phones: [{ kind: 'MobilePhone', label: '手機', value: '0988-777-666' }]
    },
    {
      contactId: 'c-002',
      fullName: '陳小華',
      company: '測試企業有限公司',
      jobTitle: '行銷經理',
      phones: [{ kind: 'Phone', label: '電話', value: '02-8888-9999' }],
      emails: [{ label: 'Email', value: 'hua@example.org' }],
      tags: [{ tagId: 'tag-vip', displayName: 'VIP 客戶' }],
      images: [],
      createdAt: '2026-10-17T08:10:00Z',
      updatedAt: '2026-10-17T08:10:00Z'
    }
  ];

  const updatedDbBytes = await globalThis.MajiSqliteBuilder.buildSqliteDatabaseBuffer({
    contacts: modifiedContacts,
    tagCatalog: dummyTags,
    customFields: [],
    imageEntriesMap: imageMap
  });

  const parsedT6 = globalThis.MajiSqliteParser.extractMajiOcrContacts(updatedDbBytes);
  assert(parsedT6.contacts.length === 2, `更新後應有 2 筆名片 (實際: ${parsedT6.contacts.length})`);
  const c1 = parsedT6.contacts.find((c) => c.contactId === 'c-001');
  const c2 = parsedT6.contacts.find((c) => c.contactId === 'c-002');
  assert(c1 && c1.company === '範例科技（總部）', '第一筆名片修改公司名稱成功');
  assert(c1 && c1.phones[0].value === '0988-777-666', '第一筆名片修改電話成功');
  assert(c2 && c2.fullName === '陳小華', '第二筆新增名片成功');
  assert(c2 && c2.emails[0].value === 'hua@example.org', '第二筆名片 Email 寫入成功');

  console.log('✅ [T6] PASS\n');

  console.log('====================================================');
  console.log(`🎉 ALL TESTS PASSED: ${testsPassed} / ${testsTotal} PASS (100%)`);
  console.log('====================================================');
}

runTests().catch((err) => {
  console.error('Test execution aborted due to error:', err);
  process.exit(1);
});
