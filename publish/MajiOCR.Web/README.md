# MajiOCR.Web 0.1.0-beta.1 (iOS / iPhone 跨平台行動通訊錄 PWA 發布包)

目前為靜態 PWA 發布版，更新日期為 2026-10-16（Asia/Taipei）。
支援 **iPhone X (iOS 16+) 以上** Safari 瀏覽器開啟並透過「分享 ➔ 加入主畫面 (Add to Home Screen)」安裝為獨立全螢幕 App，亦可由 `MajiOCR.Windows` (`v0.1.0-beta.24+`) 內建之「📱 部署 iOS / Web 雲端版 (Vercel)...」精靈一鍵發布至個人 Vercel (`https://*.vercel.app`)。

### 本版核心功能
1. **雙管道載入名片資料庫（100% 客戶端本機處理，零雲端資料庫落地）**：
   - **管道 ①（Google Drive 雲端直接下載）**：支援 Google OAuth 2.0 授權或存取權杖連線個人 Google Drive `MajiOCR_Backups` 資料夾，列出並下載 `MajiOCR_Backup_*.zip` 備份包。
   - **管道 ②（iPhone 本機檔案匯入）**：支援直接從 iPhone 內建「檔案 (Files)」App 選取 `.zip` 備份包或 `.sqlite3` 資料庫檔案離線載入。
2. **瀏覽器端 `SQLCipher-AES256` (SQLCipher 4) 主密碼解密 (`js/sqlcipher-decryptor.js`)**：
   - 採用 WebCrypto API (`crypto.subtle`) 實作 `PBKDF2-HMAC-SHA512` (256,000 次迭代)、`HMAC-SHA512` 頁面完整性驗證与 `AES-256-CBC` 解密。
   - 輸入錯誤主密碼時於 Page 1 HMAC 立即攔截並保護既有 IndexedDB 資料不遺失。
3. **純 JS SQLite Format 3 B-Tree 引擎與 IndexedDB 離線持久化 (`js/sqlite-parser.js` & `js/app.js`)**：
   - 零外部 WASM/CDN 依賴，直接解析 Windows EAV 與 Android 結構之 `contacts.sqlite3`、`contact_tag_catalog`、`contact_tags`、`custom_field_definitions` 與 `card_image_assets`。
   - 解密後自動存入 iPhone 本機 `IndexedDB` (`MajiOcrLocalDB`)，離線秒開。
4. **行動端通訊錄檢視、多關鍵字複合搜尋、分類標籤篩選與 vCard (`.vcf`) 匯出**：
   - 支援多關鍵字空白分隔 AND 即時搜尋與 `<mark>` 高亮、橫向分類標籤膠囊篩選、名片正反面圖片全螢幕縮放檢視、一鍵撥號／寄信／開啟地圖，以及匯出 `.vcf` 加入 iPhone 系統通訊錄。
