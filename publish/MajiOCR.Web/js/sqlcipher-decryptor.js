/**
 * MajiOCR.Web SQLCipher 4 (SQLCipher-AES256) WebCrypto Decryptor
 * Binary-compatible with MajiOCR.Windows SQLitePCLRaw.bundle_e_sqlcipher (v2.1.10).
 * Conforms to DBA-ISSUE-0044-v0.1:
 *  - Page size: 4096 bytes
 *  - Reserve per page: 48 bytes (16-byte IV + 32-byte HMAC-SHA512)
 *  - Encryption key KDF: PBKDF2-HMAC-SHA512, 256,000 iterations, 16-byte salt (bytes 0..15 of page 1)
 *  - HMAC key KDF: PBKDF2-HMAC-SHA512, 2 iterations, hmac_salt = salt XOR 0x3a
 *  - Page HMAC: HMAC-SHA512(hmac_key, ciphertext || iv || uint32LE(page_no))
 */
(function (global) {
  'use strict';

  const SQLITE_HEADER_MAGIC = 'SQLite format 3\0';
  const PAGE_SIZE = 4096;
  const RESERVE_SIZE = 80; // 16-byte IV + 64-byte HMAC-SHA512 (SQLCipher 4 default)
  const SALT_SIZE = 16;
  const IV_SIZE = 16;
  const HMAC_SIZE = 64;
  const KDF_ITERATIONS = 256000;
  const HMAC_KDF_ITERATIONS = 2;

  function isPlainSqliteBuffer(bytes) {
    if (!bytes || bytes.length < 16) return false;
    for (let i = 0; i < 16; i++) {
      if (bytes[i] !== SQLITE_HEADER_MAGIC.charCodeAt(i)) return false;
    }
    return true;
  }

  function constantTimeEquals(a, b) {
    if (a.length !== b.length) return false;
    let diff = 0;
    for (let i = 0; i < a.length; i++) {
      diff |= a[i] ^ b[i];
    }
    return diff === 0;
  }

  function isAllZeros(slice) {
    for (let i = 0; i < slice.length; i++) {
      if (slice[i] !== 0) return false;
    }
    return true;
  }

  /**
   * Decrypts an unpadded AES-256-CBC ciphertext buffer using WebCrypto API.
   * WebCrypto's AES-CBC decrypt requires valid PKCS#7 padding at the end.
   * We synthesize the exact 16-byte encrypted PKCS#7 padding block by encrypting
   * an empty 0-byte buffer with IV = last 16 bytes of ciphertext.
   */
  async function decryptRawAes256Cbc(subtle, aesKey, iv, ciphertext) {
    const lastBlock = ciphertext.subarray(ciphertext.length - 16);
    const padBlockBuf = await subtle.encrypt(
      { name: 'AES-CBC', iv: lastBlock },
      aesKey,
      new Uint8Array(0)
    );
    const padBlock = new Uint8Array(padBlockBuf, 0, 16);
    const paddedCiphertext = new Uint8Array(ciphertext.length + 16);
    paddedCiphertext.set(ciphertext, 0);
    paddedCiphertext.set(padBlock, ciphertext.length);

    const decryptedBuf = await subtle.decrypt(
      { name: 'AES-CBC', iv },
      aesKey,
      paddedCiphertext
    );
    return new Uint8Array(decryptedBuf);
  }

  async function decryptSqlCipher4Database(encryptedBytes, passphrase, onProgress) {
    const bytes = encryptedBytes instanceof Uint8Array ? encryptedBytes : new Uint8Array(encryptedBytes);
    if (isPlainSqliteBuffer(bytes)) {
      return bytes;
    }

    if (!passphrase || passphrase.length === 0) {
      throw new Error('PASSWORD_REQUIRED: 此備份資料庫受 SQLCipher-AES256 主密碼保護，請輸入主密碼。');
    }

    if (bytes.length < PAGE_SIZE || bytes.length % PAGE_SIZE !== 0) {
      throw new Error(`資料庫檔案長度異常 (${bytes.length} bytes)，非有效的 SQLCipher 4096-byte 分頁檔案。`);
    }

    const subtle = global.crypto && global.crypto.subtle;
    if (!subtle) {
      throw new Error('目前瀏覽器環境不支援 WebCrypto API (需使用 HTTPS 或 localhost 開啟)。');
    }

    const encoder = new TextDecoder ? new TextEncoder() : null;
    const passBytes = encoder.encode(passphrase);
    const kdfSalt = bytes.slice(0, SALT_SIZE);

    if (onProgress) onProgress(10, '正在衍生 SQLCipher PBKDF2-SHA512 金鑰...');

    // 1. Derive 32-byte AES-256 key (256,000 iterations PBKDF2-HMAC-SHA512)
    const baseKey = await subtle.importKey('raw', passBytes, 'PBKDF2', false, ['deriveBits']);
    const rawEncKeyBits = await subtle.deriveBits(
      {
        name: 'PBKDF2',
        salt: kdfSalt,
        iterations: KDF_ITERATIONS,
        hash: 'SHA-512'
      },
      baseKey,
      256
    );
    const rawEncKey = new Uint8Array(rawEncKeyBits);

    // 2. Derive 32-byte HMAC key (2 iterations PBKDF2-HMAC-SHA512 with salt XOR 0x3a)
    const hmacSalt = new Uint8Array(SALT_SIZE);
    for (let i = 0; i < SALT_SIZE; i++) {
      hmacSalt[i] = kdfSalt[i] ^ 0x3a;
    }
    const encKeyAsPbkdf2 = await subtle.importKey('raw', rawEncKey, 'PBKDF2', false, ['deriveBits']);
    const rawHmacKeyBits = await subtle.deriveBits(
      {
        name: 'PBKDF2',
        salt: hmacSalt,
        iterations: HMAC_KDF_ITERATIONS,
        hash: 'SHA-512'
      },
      encKeyAsPbkdf2,
      256
    );
    const rawHmacKey = new Uint8Array(rawHmacKeyBits);

    const aesKey = await subtle.importKey('raw', rawEncKey, { name: 'AES-CBC' }, false, ['encrypt', 'decrypt']);
    const hmacKey = await subtle.importKey(
      'raw',
      rawHmacKey,
      { name: 'HMAC', hash: 'SHA-512' },
      false,
      ['sign']
    );

    const totalPages = bytes.length / PAGE_SIZE;
    const output = new Uint8Array(bytes.length);
    let activeReserveSize = RESERVE_SIZE;
    let activeHmacSize = HMAC_SIZE;

    for (let pageNo = 1; pageNo <= totalPages; pageNo++) {
      const pageStart = (pageNo - 1) * PAGE_SIZE;
      const pageSlice = bytes.subarray(pageStart, pageStart + PAGE_SIZE);

      if (isAllZeros(pageSlice)) {
        continue;
      }

      const cipherOffset = pageNo === 1 ? SALT_SIZE : 0;
      let cipherEnd = PAGE_SIZE - activeReserveSize;
      let ciphertext = pageSlice.subarray(cipherOffset, cipherEnd);
      let iv = pageSlice.subarray(cipherEnd, cipherEnd + IV_SIZE);
      let storedHmac = pageSlice.subarray(cipherEnd + IV_SIZE, cipherEnd + IV_SIZE + activeHmacSize);

      // Compute HMAC-SHA512 over (ciphertext || iv || pageNo_LE32)
      const hmacInput = new Uint8Array(ciphertext.length + IV_SIZE + 4);
      hmacInput.set(ciphertext, 0);
      hmacInput.set(iv, ciphertext.length);
      const pOffset = ciphertext.length + IV_SIZE;
      hmacInput[pOffset] = pageNo & 0xff;
      hmacInput[pOffset + 1] = (pageNo >>> 8) & 0xff;
      hmacInput[pOffset + 2] = (pageNo >>> 16) & 0xff;
      hmacInput[pOffset + 3] = (pageNo >>> 24) & 0xff;

      const fullHmacBuf = await subtle.sign('HMAC', hmacKey, hmacInput);
      const computedHmac = new Uint8Array(fullHmacBuf, 0, activeHmacSize);
      let hmacValid = constantTimeEquals(computedHmac, storedHmac);

      if (!hmacValid && pageNo === 1 && activeReserveSize === 80) {
        const altReserve = 48;
        const altHmac = 32;
        const altEnd = PAGE_SIZE - altReserve;
        const altCipher = pageSlice.subarray(cipherOffset, altEnd);
        const altIv = pageSlice.subarray(altEnd, altEnd + IV_SIZE);
        const altStoredHmac = pageSlice.subarray(altEnd + IV_SIZE, altEnd + IV_SIZE + altHmac);

        const altInput = new Uint8Array(altCipher.length + IV_SIZE + 4);
        altInput.set(altCipher, 0);
        altInput.set(altIv, altCipher.length);
        altInput[altCipher.length + IV_SIZE] = 1;
        const altFullHmacBuf = await subtle.sign('HMAC', hmacKey, altInput);
        const altComputed = new Uint8Array(altFullHmacBuf, 0, altHmac);
        if (constantTimeEquals(altComputed, altStoredHmac)) {
          activeReserveSize = altReserve;
          activeHmacSize = altHmac;
          cipherEnd = altEnd;
          ciphertext = altCipher;
          iv = altIv;
          hmacValid = true;
        }
      }

      if (!hmacValid) {
        if (pageNo === 1) {
          throw new Error('INVALID_PASSWORD: 主密碼錯誤或資料庫無法解密 (SQLCipher Page 1 HMAC 驗證失敗)。');
        }
        throw new Error(`CORRUPTED_PAGE: 資料庫第 ${pageNo} 頁完整性校驗失敗。`);
      }

      const plaintext = await decryptRawAes256Cbc(subtle, aesKey, iv, ciphertext);

      if (pageNo === 1) {
        // Restore standard 16-byte SQLite header magic on Page 1
        for (let i = 0; i < 16; i++) {
          output[i] = SQLITE_HEADER_MAGIC.charCodeAt(i);
        }
        output.set(plaintext, 16);
        output[20] = activeReserveSize;
      } else {
        output.set(plaintext, pageStart);
      }

      if (onProgress && (pageNo === 1 || pageNo % 10 === 0 || pageNo === totalPages)) {
        const pct = 20 + Math.round((pageNo / totalPages) * 70);
        onProgress(pct, `正在解密資料庫頁面 (${pageNo}/${totalPages})...`);
      }
    }

    return output;
  }

  global.MajiSqlCipher = {
    isPlainSqliteBuffer,
    decryptSqlCipher4Database
  };
})(typeof window !== 'undefined' ? window : globalThis);
