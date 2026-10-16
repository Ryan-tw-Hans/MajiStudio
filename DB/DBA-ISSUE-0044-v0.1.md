# 資料庫影響評估：iOS PWA 跨平台 SQLite 讀取與 SQLCipher-AES256 解密規格 (ISSUE-0044)

- **對應 Issue ID**：`ISSUE-0044`
- **評估版本**：`v0.1`
- **DBA 角色**：DBA Agent

---

## 1. 資料庫 Schema 影響評估

1. **對既有 `contacts.sqlite3` Schema 之影響**：
   - **零破壞、零 Schema 變更**：本議題在 `MajiOCR.Web` (iOS PWA) 端為**唯讀解析與查詢**由 `MajiOCR.Windows` 或 `MajiOCR` (Android) 產生之 `contacts.sqlite3` 備份資料庫，不修改既有資料表定義。
   - **相容之資料表範圍（支援 `user_version = 1` 與 `user_version = 2`）**：
     - `contact_records` (`contact_id`, `display_name_primary`, `company_primary`, `job_title_primary`, `notes`, `created_at_utc`, `updated_at_utc`, `pending_delete`)
     - `contact_values` (`value_id`, `contact_id`, `field_key`, `value_text`, `normalized_value`, `sort_order`, `is_primary`)
     - `card_image_assets` (`asset_id`, `contact_id`, `side`, `relative_path`, `sha256`, `width`, `height`)
     - `contact_tag_catalog` (`tag_id`, `tag_name`, `normalized_name`, `sort_order`)（若存在則讀取，相容 v1 無此表之舊備份）
     - `contact_record_tags` (`contact_id`, `tag_id`, `assigned_at_utc`)
     - `custom_field_definitions` (`field_key`, `display_name`, `sort_order`)（若存在則讀取，相容 `ISSUE-0043` 新增之自訂欄位表）

---

## 2. SQLCipher-AES256 (`SQLCipher 4`) 二進位解密與完整性驗證規格

`MajiOCR.Windows` 使用 `SQLitePCLRaw.bundle_e_sqlcipher` (v2.1.10)，其底層為 **SQLCipher 4 預設加密契約**。為使 iPhone Safari (`MajiOCR.Web`) 能在不依賴原生 C 編譯環境下直接解密 `contacts.sqlite3`，DBA 定義瀏覽器端 `window.crypto.subtle` (及 C# 驗證端) 標準解密演算法規格如下：

| 參數項目 | SQLCipher 4 標準規格值 | 說明 |
| :--- | :--- | :--- |
| **加密識別方式** | 檔案前 16 bytes `!= "SQLite format 3\0"` | 標準未加密 SQLite 前 16 bytes 為 ASCII `"SQLite format 3\0"`；SQLCipher 加密檔案前 16 bytes 為隨機 `file_salt` |
| **Page Size (`page_sz`)** | `4096` bytes | 資料庫檔案長度必為 `4096` 之整數倍 |
| **Salt (`kdf_salt`)** | Page 1 前 `16` bytes (`dbBytes[0..16]`) | 用於衍生加密金鑰 |
| **Encryption Key 衍生** | `PBKDF2-HMAC-SHA512`，迭代 `256,000` 次，輸出 `32` bytes (256-bit) | 輸入為 UTF-8 編碼之使用者主密碼 (`passphrase`) 與 `kdf_salt` |
| **HMAC Salt (`hmac_salt`)** | `kdf_salt` 每個 byte 與 `0x3a` 進行 XOR (`b ^ 0x3a`) | 長度 `16` bytes |
| **HMAC Key 衍生** | `PBKDF2-HMAC-SHA512`，迭代 `2` 次，輸出 `32` bytes (256-bit) | 輸入為上述 32-byte `Encryption Key` 與 `hmac_salt` |
| **每頁保留區 (`reserve_sz`)** | 每頁末尾 `80` bytes = `16` bytes IV + `64` bytes HMAC-SHA512（並向下相容 `48` bytes 截斷模式） | Page 1 加密區段為 `offset 16..4016` (共 4000 bytes)；Page $N$ ($N \ge 2$) 加密區段為 `offset 0..4016` (共 4016 bytes) |
| **HMAC-SHA512 驗證公式** | `HMAC-SHA512(hmac_key, ciphertext || iv || little_endian_uint32(page_no))` | `page_no` 從 `1` 開始編號（4-byte Little-Endian）。**解密 Page 1 前必先驗證 Page 1 HMAC**，若不符代表密碼錯誤或檔案損毀，立即拒絕並中止！ |
| **AES-256-CBC 解密與標頭重建** | 使用 `Encryption Key` 與該頁之 16-byte `IV` 執行 `AES-256-CBC` (無 PKCS7 Padding) 解密 | 解密後將 Page 1 前 16 bytes 寫回 `"SQLite format 3\0"`，並確保 `decrypted[20] = 80`（告知標準 SQLite 每頁末尾保留 80 bytes），即可直接交由標準 SQLite 引擎或純 JS B-Tree 解析器解析！ |

---

## 3. 備份與回復／錯誤防護

1. **密碼錯誤安全中止**：
   - 在 iPhone PWA 載入新加密備份時，必須先在記憶體中完成 Page 1 HMAC-SHA512 驗證與全庫頁面解密，確認成功解析出 `contact_records` 後，才覆寫 IndexedDB 內的既有資料庫與名片圖檔。
   - 若密碼錯誤，既有已儲存在 iPhone IndexedDB 的名片資料與圖片保持 100% 原狀不遺失。
2. **查詢過濾契約**：
   - 所有通訊錄查詢必須強制附加 `WHERE pending_delete = 0`，確保已標記待刪除的紀錄不會顯示於 iPhone 端。
