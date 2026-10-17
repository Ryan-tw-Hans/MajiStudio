# 測試計畫書：iOS PWA 名片編輯、SQLite 寫出、SQLCipher 重新加密、ZIP 打包、vCard 與 Google Drive 同步驗證 (ISSUE-0049)

- **對應 Issue ID**：`ISSUE-0049`
- **測試版本**：`v0.1`
- **測試角色**：測試工程師 Agent（由 PM Agent 分派）

---

## 1. 測試目標與範疇

驗證 `MajiOCR.Web` (iOS PWA) 在新增/編輯名片、本機 IndexedDB 快取持久化、SQLite 重建、SQLCipher-AES256 加密輸出、ZIP 備份包壓縮、vCard 產生與 Google Drive Multipart Upload 上傳之正確性與跨平台相容性。

---

## 2. 測試項目清單 (Test Cases)

| 測試編號 | 測試項目 | 驗證重點與通過標準 | 執行方式 |
| :--- | :--- | :--- | :--- |
| **T1** | **SQLite 資料庫產生與相容性** | 驗證前端能將名片資料表（`contact_records`, `contact_values`, `card_image_assets`, `contact_tag_catalog`, `contact_record_tags`）正確寫出為符合標準 SQLite format 3 之二進位 byte array，能被標準 SQLite 引擎查詢且資料完全一致。 | 自動化測試腳本 (`verify_issue_0049.mjs`) |
| **T2** | **SQLCipher-AES256 重新加密與反向解密相容性** | 驗證經 WebCrypto 重新加密之 `contacts.sqlite3` 符合 SQLCipher 4 規格（Page Size 4096, Reserve 80, PBKDF2-HMAC-SHA512 256,000 次），並能由現有之 `SqlCipherDecryptor` 100% 成功通過 HMAC 驗證並解密無誤。 | 自動化測試腳本 (`verify_issue_0049.mjs`) |
| **T3** | **ZIP 備份包打包與還原驗證** | 驗證將 `manifest.json`、`contacts.sqlite3` 及 `card-images/` 目錄圖檔打包為 ZIP 後，能被現有的 `MajiZipReader` 完整解壓縮並還原所有檔案與二進位內容。 | 自動化測試腳本 (`verify_issue_0049.mjs`) |
| **T4** | **vCard (.vcf) 格式規範驗證** | 驗證名片匯出之 vCard 格式符合 RFC 6350 (vCard 3.0 / 4.0)，包含 `BEGIN:VCARD`、`FN`、`ORG`、`TITLE`、`TEL`、`EMAIL`、`ADR`、`URL`、`NOTE` 與 `END:VCARD`，無跳行或編碼異常。 | 自動化測試腳本 (`verify_issue_0049.mjs`) |
| **T5** | **Google Drive Multipart Upload 請求建構** | 驗證上傳 Google Drive 時所構建之 `multipart/related` 請求 Payload 正確包含 JSON Metadata（檔名、`MajiOCR_Backups` 資料夾 ID、自訂屬性 `platform: 'ios'`）與 ZIP 二進位本體。 | 自動化測試腳本 (`verify_issue_0049.mjs`) |
| **T6** | **前端編輯狀態與 IndexedDB 持久化** | 驗證編輯名片後更新 IndexedDB，再次重啟或重整頁面時資料正確讀出且無任何欄位遺失。 | 程式碼檢查與端對端驗證 |

---

## 3. 回歸測試 (Regression Scope)

1. `ISSUE-0044`：既有備份 ZIP（包含 Windows/Android 產出的加密與未加密備份）下載、解壓縮、SQLCipher 解密與查詢功能不受影響。
2. `ISSUE-0047`：GitHub Pages 平台與 Vercel 平台的離線 Service Worker 與快取機制正常。
