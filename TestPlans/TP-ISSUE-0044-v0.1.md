# 測試評估與測試計畫：支援 iOS / iPhone 平台運行 (PWA)、Windows Vercel 一鍵部署精靈與 SQLCipher 解密查詢 (ISSUE-0044)

- **對應 Issue ID**：`ISSUE-0044`
- **測試計畫版本**：`v0.1`
- **測試工程師角色**：測試工程師 Agent

---

## 1. 測試風險評估與回歸範圍

1. **高風險項目**：
   - **SQLCipher-AES256 跨平台二進位解密正確性**：必須確保 `MajiOCR.Windows` (`SQLitePCLRaw.bundle_e_sqlcipher`) 建立之真實加密 `contacts.sqlite3`，經過 SQLCipher 4 頁面解密演算法（PBKDF2-HMAC-SHA512 256,000 次迭代 + HMAC-SHA512 + AES-256-CBC）後，能還原為合法的標準 SQLite 檔案並完整讀出所有聯絡人、標籤、自訂欄位與圖片路徑。
   - **錯誤主密碼攔截**：輸入錯誤密碼時，Page 1 HMAC-SHA512 必須 100% 拒絕並回傳明確錯誤，不得產生假陽性或損毀既有資料。
   - **Vercel REST API 部署封包完整性**：`VercelDeploymentService` 產生之 `POST /v13/deployments` JSON Payload 必須包含完整之 `MajiOCR.Web` 檔案（`index.html`、`manifest.webmanifest`、`sw.js`、JS/CSS 模組與內嵌設定），且絕不夾帶本機私密 `contacts.sqlite3`。
2. **影響模組與回歸範圍**：
   - `MajiOCR.Web/`（新增獨立 iOS PWA 專案與 `publish/MajiOCR.Web/`）
   - `MajiOCR.Windows`（新增 `VercelDeploymentService`、`QrCodeGenerator`、`VercelDeployDialog`、`MainPage` / `SavedCardsTablePanel` 選單入口，以及 `AppSettings` Vercel 設定）
   - 回歸驗證既有 `Issue0041AutomatedTestSuite`（Google Drive 加密備份與還原）與 `Issue0043AutomatedTestSuite`。

---

## 2. 測試項目與通過標準 (`Issue0044AutomatedTestSuite`)

| 測試 ID | 測試名稱 | 前置條件與步驟 | 預期結果與通過標準 | 對應 AC |
| :--- | :--- | :--- | :--- | :--- |
| **T1** | **真實 SQLCipher-AES256 資料庫解密與標準 SQLite 讀取驗證** | 1. 使用 `StorageService` 建立受主密碼（如 `"Maji@iOS2026"`）保護之 SQLCipher 資料庫並寫入多筆含標籤、自訂欄位與圖片的名片<br>2. 執行 SQLCipher 4 標準頁面解密器將加密 bytes 轉為標準 SQLite bytes<br>3. 以無密碼標準 SQLite 讀取解密後檔案 | 1. 加密檔前 16 bytes 非 `"SQLite format 3\0"`<br>2. 解密後前 16 bytes 為 `"SQLite format 3\0"` 且 `header[20] == 48`<br>3. 無密碼連線成功讀出全部名片、標籤與自訂欄位，筆數與內容 100% 一致 | AC-07, NFR-01 |
| **T2** | **SQLCipher 錯誤主密碼 HMAC 攔截驗證** | 對 T1 建立之加密資料庫輸入錯誤密碼（如 `"WrongPass123"`）進行解密 | Page 1 HMAC-SHA512 驗證立即失敗並回傳明確密碼錯誤訊息，不會產出錯誤資料 | AC-07 |
| **T3** | **內嵌 `MajiOCR.Web` PWA 資產完整性與 Vercel 部署 Payload 驗證** | 1. 呼叫 `VercelDeploymentService.BuildDeploymentPayload(...)`（帶入自訂專案名與選填 Google Client ID）<br>2. 檢查產生之檔案清單與 JSON 結構 | 1. 必含 `index.html`, `manifest.webmanifest`, `sw.js`, `js/app.js`, `js/sqlcipher-decryptor.js`, `js/sqlite-parser.js` 等完整 PWA 檔案<br>2. 驗證絕不包含任何 `.sqlite3` 資料庫檔<br>3. 專案名稱正規化與設定注入正確 | AC-02, AC-03, NFR-02 |
| **T4** | **Vercel REST API 模擬互動與錯誤處理測試** | 使用 Mock `HttpMessageHandler` 模擬 Vercel `GET /v2/user`、`POST /v13/deployments` 與 `PATCH /v9/projects/{id}`（含成功 200 及 Token 無效 401/403 情境） | 1. 成功時正確解析並回傳 `https://<project>.vercel.app` 正式網址<br>2. 401/403 時回傳繁體中文友善錯誤訊息 | AC-03 |
| **T5** | **本機 QR Code 產生器與 AppSettings 持久化測試** | 1. 將部署網址傳入 `QrCodeGenerator` 產生 BMP/PNG 影像 bytes<br>2. 測試 `AppSettingsService` 讀寫 Vercel 專案設定 | 1. 產出有效非空之 QR Code 矩陣與影像資料<br>2. 設定可正確保存與重新載入 | AC-02, AC-03 |
| **T6** | **iOS PWA 雙管道備份 ZIP 解壓、複合搜尋與標籤篩選契約驗證** | 1. 以 `GoogleDriveBackupService` 產生加密與未加密 `MajiOCR_Backup_*.zip`<br>2. 驗證 `MajiOCR.Web` 的 ZIP / SQLite / 複合關鍵字與分類標籤篩選邏輯及獨立發布目錄 `publish/MajiOCR.Web/`、`publish/MajiOCR.Windows/MajiOCR.Windows.exe` | 1. 兩種備份 ZIP 均可正確解析 `manifest.json`、`contacts.sqlite3` 與 `card-images/`<br>2. 固定發布資料夾產物齊全、版本為 `0.1.0-beta.24` (`0.1.0.24`) 且雜湊紀錄完整 | AC-04 ~ AC-11, NFR-03 |
