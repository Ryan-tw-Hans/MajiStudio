# 資料庫影響評估：iOS PWA 名片編輯寫入、EAV 關聯維護與 SQLCipher-AES256 重新加密規格 (ISSUE-0049)

- **對應 Issue ID**：`ISSUE-0049`
- **評估版本**：`v0.1`
- **DBA 角色**：DBA Agent

---

## 1. 資料庫 Schema 規範與相容性

本議題讓 `MajiOCR.Web` (iOS PWA) 具備**寫入與更新名片資料**的能力。為確保 iOS 端產出的資料庫能被 Windows 端 (`MajiOCR.Windows`) 與 Android 端 (`MajiOCR`) 100% 無損讀取與接續編輯，必須嚴格遵守以下 Schema 規範：

### 1.1 `contact_records`（聯絡人主表）
| 欄位名稱 | 型別 | 寫入規則 |
| :--- | :--- | :--- |
| `contact_id` | TEXT (PK) | 必須為標準 UUID 格式（如 `e4b3c1a2-1234-4abc-8def-0123456789ab`），若為既有名片保持不變，新增名片以 `crypto.randomUUID()` 產生。 |
| `display_name_primary` | TEXT | 主要顯示姓名，必填。 |
| `company_primary` | TEXT | 主要公司名稱，若無填空字串 `""`。 |
| `job_title_primary` | TEXT | 主要職稱，若無填空字串 `""`。 |
| `notes` | TEXT | 備註文字，若無填空字串 `""`。 |
| `created_at_utc` | TEXT | 建立時間（ISO 8601 UTC，如 `2026-10-17T07:15:00Z`）。編輯時不可變更原始建立時間。 |
| `updated_at_utc` | TEXT | 最後修改時間（ISO 8601 UTC），每次編輯儲存時更新為當前時間。 |
| `pending_delete` | INTEGER | 軟刪除標記：正常為 `0`，標記刪除為 `1`。所有查詢預設排除 `pending_delete = 1`。 |

### 1.2 `contact_values`（EAV 擴充值表）
- 用於儲存電話、電子郵件、地址、網站等欄位：
  - `value_id`：UUID 字串。
  - `contact_id`：關聯之 `contact_records.contact_id`。
  - `field_key`：固定列舉值（`phone`、`email`、`address`、`website` 等）。
  - `value_text`：原始輸入文字。
  - `normalized_value`：正規化文字（電話去空格減號、Email 轉小寫等）。
  - `sort_order`：排序權重（整數，從 0 起算）。
  - `is_primary`：是否為主要聯絡資訊（`1` 或 `0`）。
- 編輯儲存策略：編輯既有名片時，可先清空該 `contact_id` 在 `contact_values` 的舊資料，再依編輯後之最新列表整批寫入最新值。

### 1.3 `card_image_assets`（名片圖檔關聯表）
- `asset_id`：UUID 字串。
- `contact_id`：關聯之名片 ID。
- `side`：`1` 表示正面，`2` 表示反面。
- `relative_path`：`card-images/<contact_id>_front.jpg` 或 `card-images/<contact_id>_back.jpg`。
- `sha256`：圖檔二進位 SHA-256 雜湊字串（小寫 64 碼十六進位）。
- `width` / `height`：像素寬高（整數）。

### 1.4 `contact_tag_catalog` 與 `contact_record_tags`（標籤分類體系）
- 若新增未存在的標籤名稱，於 `contact_tag_catalog` 新增紀錄（`tag_id` 為 UUID，`tag_name` 與小寫去空白之 `normalized_name`）。
- 於 `contact_record_tags` 維護 `contact_id` 與 `tag_id` 的多對多關聯（`assigned_at_utc` 設為指派時間）。

---

## 2. SQLCipher-AES256 重新加密產生規格

若資料庫原本為 SQLCipher 加密，或使用者在 PWA 設定了主密碼，當前端觸發「匯出備份」或「同步至 Google Drive」時，必須執行**反向加密流程**產出與 SQLCipher 4 100% 二進位相容之檔案：

1. **基本參數**：
   - Page Size: `4096` bytes。
   - Reserve Size: `80` bytes（16-byte IV + 64-byte HMAC-SHA512）。
   - Page 1 前 16 bytes 為隨機產生的 `kdf_salt` (`window.crypto.getRandomValues(new Uint8Array(16))`)。
2. **金鑰衍生**：
   - `Encryption Key`：`PBKDF2-HMAC-SHA512(passphrase, kdf_salt, 256000)` 產出 32 bytes。
   - `HMAC Key`：`PBKDF2-HMAC-SHA512(encryption_key, kdf_salt ^ 0x3a, 2)` 產出 32 bytes。
3. **逐頁加密與 HMAC 填充**：
   - 對每個 4096-byte 的頁面 $N$ ($N \ge 1$)：
     - 產生該頁專屬之 16-byte 隨機 IV。
     - 若為 Page 1：明文資料為 `page[16..4016]`（共 4000 bytes）；執行 AES-256-CBC 加密（無 padding，長度洽為 4000 bytes）。
     - 若為 Page $N$ ($N \ge 2$)：明文資料為 `page[0..4016]`（共 4016 bytes）；執行 AES-256-CBC 加密（長度 4016 bytes）。
     - 計算 HMAC：`HMAC-SHA512(hmac_key, ciphertext || iv || little_endian_uint32(N))` 產出 64 bytes。
     - 組裝該頁密文：
       - Page 1：`kdf_salt (16B) || ciphertext (4000B) || iv (16B) || hmac (64B)` = 4096 bytes。
       - Page $N$：`ciphertext (4016B) || iv (16B) || hmac (64B)` = 4096 bytes。
4. **輸出相容性驗證**：
   - 經上述加密產出之 byte array，可直接交由 Windows 端 `SQLitePCLRaw.bundle_e_sqlcipher` 無損開啟與解密。

---

## 3. 備份包 Manifest 規範

產生備份 ZIP 時，根目錄的 `manifest.json` 必須更新為：
```json
{
  "BackupId": "MajiOCR_Backup_iOS_YYYYMMDD_HHmmss",
  "Platform": "ios",
  "CreatedAtUtc": "2026-10-17T07:15:00Z",
  "ContactCount": 42,
  "ImageCount": 50,
  "IsPasswordProtected": true,
  "EncryptionAlgorithm": "SQLCipher-AES256"
}
```
確保 Windows / Android 端下載時能正確識別為 iOS 端產生的備份版本。
