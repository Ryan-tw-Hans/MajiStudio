/**
 * MajiOCR.Web Pure-JS ZIP Reader (Supports Store=0 & Deflate=8)
 * Unpacks MajiOCR_Backup_*.zip containing manifest.json, contacts.sqlite3, and card-images/*.
 * Works 100% offline on iPhone X (iOS 16+) without external CDN dependencies.
 */
(function (global) {
  'use strict';

  function readUint16LE(bytes, offset) {
    return bytes[offset] | (bytes[offset + 1] << 8);
  }

  function readUint32LE(bytes, offset) {
    return (
      (bytes[offset] |
        (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) |
        (bytes[offset + 3] << 24)) >>>
      0
    );
  }

  // Pure-JS RFC 1951 Raw Deflate fallback for environments without DecompressionStream('deflate-raw')
  function inflateRawPureJs(src, expectedSize) {
    const out = expectedSize > 0 ? new Uint8Array(expectedSize) : [];
    let outPos = 0;
    const writeByte = (b) => {
      if (expectedSize > 0) {
        out[outPos++] = b;
      } else {
        out.push(b);
        outPos++;
      }
    };
    const getByteAt = (idx) => out[idx];

    let bitPos = 0;
    const readBits = (n) => {
      let val = 0;
      for (let i = 0; i < n; i++) {
        const byteIdx = bitPos >> 3;
        if (byteIdx >= src.length) throw new Error('ZIP Deflate stream truncated');
        const bit = (src[byteIdx] >> (bitPos & 7)) & 1;
        val |= bit << i;
        bitPos++;
      }
      return val;
    };

    const buildHuffman = (lengths) => {
      const maxBits = Math.max(...lengths, 1);
      const blCount = new Int32Array(maxBits + 1);
      for (let i = 0; i < lengths.length; i++) {
        if (lengths[i] > 0) blCount[lengths[i]]++;
      }
      const nextCode = new Int32Array(maxBits + 1);
      let code = 0;
      for (let bits = 1; bits <= maxBits; bits++) {
        code = (code + blCount[bits - 1]) << 1;
        nextCode[bits] = code;
      }
      const map = new Map();
      for (let n = 0; n < lengths.length; n++) {
        const len = lengths[n];
        if (len !== 0) {
          map.set((len << 16) | nextCode[len], n);
          nextCode[len]++;
        }
      }
      return { map, maxBits };
    };

    const decodeSymbol = (tree) => {
      let code = 0;
      for (let len = 1; len <= tree.maxBits; len++) {
        const byteIdx = bitPos >> 3;
        const bit = (src[byteIdx] >> (bitPos & 7)) & 1;
        bitPos++;
        code = (code << 1) | bit;
        const key = (len << 16) | code;
        if (tree.map.has(key)) return tree.map.get(key);
      }
      throw new Error('Invalid Huffman code in ZIP Deflate stream');
    };

    const fixedLitLengths = new Uint8Array(288);
    for (let i = 0; i <= 143; i++) fixedLitLengths[i] = 8;
    for (let i = 144; i <= 255; i++) fixedLitLengths[i] = 9;
    for (let i = 256; i <= 279; i++) fixedLitLengths[i] = 7;
    for (let i = 280; i <= 287; i++) fixedLitLengths[i] = 8;
    const fixedDistLengths = new Uint8Array(32).fill(5);

    const lenBase = [
      3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31,
      35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258
    ];
    const lenExtra = [
      0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2,
      3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0
    ];
    const distBase = [
      1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193,
      257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145,
      8193, 12289, 16385, 24577
    ];
    const distExtra = [
      0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6,
      7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13
    ];
    const clOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

    let bfinal = 0;
    while (!bfinal) {
      bfinal = readBits(1);
      const btype = readBits(2);
      if (btype === 0) {
        bitPos = (bitPos + 7) & ~7;
        const byteIdx = bitPos >> 3;
        const len = readUint16LE(src, byteIdx);
        bitPos += 32;
        const dataStart = bitPos >> 3;
        for (let i = 0; i < len; i++) writeByte(src[dataStart + i]);
        bitPos += len * 8;
      } else if (btype === 1 || btype === 2) {
        let litTree, distTree;
        if (btype === 1) {
          litTree = buildHuffman(fixedLitLengths);
          distTree = buildHuffman(fixedDistLengths);
        } else {
          const hlit = readBits(5) + 257;
          const hdist = readBits(5) + 1;
          const hclen = readBits(4) + 4;
          const clLengths = new Uint8Array(19);
          for (let i = 0; i < hclen; i++) clLengths[clOrder[i]] = readBits(3);
          const clTree = buildHuffman(clLengths);
          const allLengths = new Uint8Array(hlit + hdist);
          let idx = 0;
          while (idx < hlit + hdist) {
            const sym = decodeSymbol(clTree);
            if (sym < 16) {
              allLengths[idx++] = sym;
            } else if (sym === 16) {
              const prev = allLengths[idx - 1];
              const rep = readBits(2) + 3;
              for (let r = 0; r < rep; r++) allLengths[idx++] = prev;
            } else if (sym === 17) {
              const rep = readBits(3) + 3;
              for (let r = 0; r < rep; r++) allLengths[idx++] = 0;
            } else if (sym === 18) {
              const rep = readBits(7) + 11;
              for (let r = 0; r < rep; r++) allLengths[idx++] = 0;
            }
          }
          litTree = buildHuffman(allLengths.subarray(0, hlit));
          distTree = buildHuffman(allLengths.subarray(hlit));
        }

        while (true) {
          const sym = decodeSymbol(litTree);
          if (sym === 256) break;
          if (sym < 256) {
            writeByte(sym);
          } else {
            const lenIdx = sym - 257;
            const length = lenBase[lenIdx] + readBits(lenExtra[lenIdx]);
            const distSym = decodeSymbol(distTree);
            const dist = distBase[distSym] + readBits(distExtra[distSym]);
            for (let i = 0; i < length; i++) {
              writeByte(getByteAt(outPos - dist));
            }
          }
        }
      } else {
        throw new Error('Unsupported Deflate block type 3');
      }
    }
    return expectedSize > 0 ? out : new Uint8Array(out);
  }

  async function decompressDeflateRaw(compressedBytes, uncompressedSize) {
    if (typeof DecompressionStream !== 'undefined') {
      try {
        const ds = new DecompressionStream('deflate-raw');
        const writer = ds.writable.getWriter();
        writer.write(compressedBytes);
        writer.close();
        const response = new Response(ds.readable);
        const ab = await response.arrayBuffer();
        return new Uint8Array(ab);
      } catch (_) {
        // Fallback to pure JS inflate
      }
    }
    return inflateRawPureJs(compressedBytes, uncompressedSize);
  }

  async function unpackZipBuffer(arrayBuffer) {
    const bytes = arrayBuffer instanceof Uint8Array ? arrayBuffer : new Uint8Array(arrayBuffer);
    if (bytes.length < 22) {
      throw new Error('檔案過小，不是有效的 ZIP 備份包。');
    }

    // Locate End of Central Directory (EOCD) signature 0x06054b50
    let eocdOffset = -1;
    const minOffset = Math.max(0, bytes.length - 65557);
    for (let i = bytes.length - 22; i >= minOffset; i--) {
      if (readUint32LE(bytes, i) === 0x06054b50) {
        eocdOffset = i;
        break;
      }
    }
    if (eocdOffset < 0) {
      throw new Error('找不到 ZIP 目錄索引 (EOCD)，請確認檔案為有效的 .zip 備份包。');
    }

    const totalEntries = readUint16LE(bytes, eocdOffset + 10);
    const cdOffset = readUint32LE(bytes, eocdOffset + 16);
    const decoder = new TextDecoder('utf-8');
    const files = new Map();

    let ptr = cdOffset;
    for (let i = 0; i < totalEntries; i++) {
      if (ptr + 46 > bytes.length || readUint32LE(bytes, ptr) !== 0x02014b50) {
        break;
      }
      const compressionMethod = readUint16LE(bytes, ptr + 10);
      const compressedSize = readUint32LE(bytes, ptr + 20);
      const uncompressedSize = readUint32LE(bytes, ptr + 24);
      const fileNameLen = readUint16LE(bytes, ptr + 28);
      const extraLen = readUint16LE(bytes, ptr + 30);
      const commentLen = readUint16LE(bytes, ptr + 32);
      const localHeaderOffset = readUint32LE(bytes, ptr + 42);

      const rawName = decoder.decode(bytes.subarray(ptr + 46, ptr + 46 + fileNameLen));
      const normalizedName = rawName.replace(/\\/g, '/');
      ptr += 46 + fileNameLen + extraLen + commentLen;

      if (normalizedName.endsWith('/')) continue;

      if (localHeaderOffset + 30 > bytes.length || readUint32LE(bytes, localHeaderOffset) !== 0x04034b50) {
        throw new Error(`ZIP 內部檔案標頭損毀: ${normalizedName}`);
      }
      const localNameLen = readUint16LE(bytes, localHeaderOffset + 26);
      const localExtraLen = readUint16LE(bytes, localHeaderOffset + 28);
      const dataStart = localHeaderOffset + 30 + localNameLen + localExtraLen;
      const compressedSlice = bytes.subarray(dataStart, dataStart + compressedSize);

      let content;
      if (compressionMethod === 0) {
        content = new Uint8Array(compressedSlice);
      } else if (compressionMethod === 8) {
        content = await decompressDeflateRaw(compressedSlice, uncompressedSize);
      } else {
        throw new Error(`不支援的 ZIP 壓縮演算法 (Method=${compressionMethod}): ${normalizedName}`);
      }

      files.set(normalizedName, content);
    }

    return files;
  }

  const crcTable = new Uint32Array(256);
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    crcTable[i] = c >>> 0;
  }
  function computeCrc32(bytes) {
    let crc = 0xffffffff;
    for (let i = 0; i < bytes.length; i++) {
      crc = crcTable[(crc ^ bytes[i]) & 0xff] ^ (crc >>> 8);
    }
    return (crc ^ 0xffffffff) >>> 0;
  }

  function packZipBuffer(entries) {
    const encoder = new TextEncoder();
    const fileRecords = [];
    let offset = 0;
    const chunks = [];

    const list = entries instanceof Map ? Array.from(entries.entries()) : entries;

    for (const [name, content] of list) {
      const data = typeof content === 'string'
        ? encoder.encode(content)
        : (content instanceof Uint8Array ? content : new Uint8Array(content));
      const normalizedName = String(name).replace(/\\/g, '/');
      const nameBytes = encoder.encode(normalizedName);
      const crc = computeCrc32(data);

      const localHeader = new Uint8Array(30 + nameBytes.length);
      const dv = new DataView(localHeader.buffer);
      dv.setUint32(0, 0x04034b50, true);
      dv.setUint16(4, 20, true);
      dv.setUint16(6, 0x0800, true);
      dv.setUint16(8, 0, true);
      dv.setUint16(10, 0, true);
      dv.setUint16(12, 0, true);
      dv.setUint32(14, crc, true);
      dv.setUint32(18, data.length, true);
      dv.setUint32(22, data.length, true);
      dv.setUint16(26, nameBytes.length, true);
      dv.setUint16(28, 0, true);
      localHeader.set(nameBytes, 30);

      fileRecords.push({ nameBytes, crc, size: data.length, offset });
      chunks.push(localHeader);
      chunks.push(data);
      offset += localHeader.length + data.length;
    }

    const cdStart = offset;
    let cdSize = 0;
    for (const rec of fileRecords) {
      const cd = new Uint8Array(46 + rec.nameBytes.length);
      const dv = new DataView(cd.buffer);
      dv.setUint32(0, 0x02014b50, true);
      dv.setUint16(4, 20, true);
      dv.setUint16(6, 20, true);
      dv.setUint16(8, 0x0800, true);
      dv.setUint16(10, 0, true);
      dv.setUint16(12, 0, true);
      dv.setUint16(14, 0, true);
      dv.setUint32(16, rec.crc, true);
      dv.setUint32(20, rec.size, true);
      dv.setUint32(24, rec.size, true);
      dv.setUint16(28, rec.nameBytes.length, true);
      dv.setUint16(30, 0, true);
      dv.setUint16(32, 0, true);
      dv.setUint16(34, 0, true);
      dv.setUint16(36, 0, true);
      dv.setUint32(38, 0, true);
      dv.setUint32(42, rec.offset, true);
      cd.set(rec.nameBytes, 46);
      chunks.push(cd);
      cdSize += cd.length;
    }

    const eocd = new Uint8Array(22);
    const dv = new DataView(eocd.buffer);
    dv.setUint32(0, 0x06054b50, true);
    dv.setUint16(4, 0, true);
    dv.setUint16(6, 0, true);
    dv.setUint16(8, fileRecords.length, true);
    dv.setUint16(10, fileRecords.length, true);
    dv.setUint32(12, cdSize, true);
    dv.setUint32(16, cdStart, true);
    dv.setUint16(20, 0, true);
    chunks.push(eocd);

    const totalLen = chunks.reduce((acc, c) => acc + c.length, 0);
    const out = new Uint8Array(totalLen);
    let pos = 0;
    for (const c of chunks) {
      out.set(c, pos);
      pos += c.length;
    }
    return out;
  }

  global.MajiZipReader = {
    unpackZipBuffer,
    packZipBuffer,
    computeCrc32,
    inflateRawPureJs
  };
})(typeof window !== 'undefined' ? window : globalThis);
