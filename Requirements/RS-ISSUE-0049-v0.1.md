# 需求規格書：iOS PWA 支援名片資料編輯、雙向同步 Google Drive 與外部檔案匯出 (ZIP / vCard) (ISSUE-0049)

- **對應 Issue ID**：`ISSUE-0049`
- **需求版本**：`v0.1`
- **建立／更新時間**：`2026-10-17T15:15:00+08:00`
- **負責窗口**：PM Agent

---

## 1. 問題背景與目標

1. **現況問題**：
   - 在 `ISSUE-0044` 中，系統成功構建了支援 iPhone X (iOS 16+) 離線運行的 `MajiOCR.Web` PWA 與 Windows 端一鍵部署精靈。
   - 然而目前的 PWA 僅提供「唯讀瀏覽與搜尋」功能，使用者若外出攜帶 iPhone，無法直接在 iPhone 上修訂名片欄位、補充備註、新增名片或更新照片；亦無法將 iPhone 本機修改後的成果寫回 Google Drive 雲端或匯出成備份包給其他裝置（Windows / Android）使用。
2. **核心目標**：
   - **名片資料編輯與新增**：在 `MajiOCR.Web` iOS PWA 支援直接編輯名片各項欄位（姓名、公司、職稱、電話、Email、地址、網站、備註、分類標籤、自訂欄位），支援名片正反面圖檔新增/更換/刪除，並支援名片軟刪除與復原。
   - **外部檔案匯出 (Export)**：
     - **管道 A（完整備份 ZIP）**：支援將本機最新修改後的名片庫與圖片打包為標準 `MajiOCR_Backup_iOS_YYYYMMDD_HHmmss.zip`，透過 iOS 原生分享選單 (`navigator.share`) 儲存至 iPhone「檔案」、iCloud Drive 或透過 AirDrop 傳送至電腦。
     - **管道 B（通訊錄 vCard .vcf 匯出）**：支援單筆或批次匯出標準 vCard (`.vcf`)，點擊後可一鍵將聯絡人匯入 iPhone 內建的「電話／通訊錄 (Apple Contacts)」。
   - **雙向同步 Google Drive (Sync to Cloud)**：
     - 在 PWA 介面提供「📤 同步至 Google Drive」功能，將最新修改打包並透過 Google Drive REST API 直接上傳至 `MajiOCR_Backups` 資料夾，維持多端（Windows / Android / iOS）可互相讀取的去中心化備份模型。
   - **SQLCipher-AES256 雙向相容**：
     - 若原始備份包有啟用主密碼保護，在打包匯出或同步至 Google Drive 時，能自動重新以 SQLCipher 4 規格（PBKDF2-HMAC-SHA512 + AES-256-CBC）加密，確保雲端與匯出檔案的資料安全性。

---

## 2. 功能與非功能需求及驗收條件 (AC)

### 2.1 iPhone / iOS PWA 端：名片編輯與新增功能 (`MajiOCR.Web`)
* **AC-01（名片編輯表單）**：
  - 於名片詳情對話框（`detailModal`）新增「✏️ 編輯名片」操作按鈕。
  - 點擊後開啟名片編輯對話框，支援編輯以下欄位：
    - 核心基本欄位：姓名（`display_name_primary`）、公司（`company_primary`）、職稱（`job_title_primary`）、備註（`notes`）。
    - 聯絡值清單：電話、電子郵件、地址、網站等欄位（支援新增多筆、刪除、設為主要聯絡資訊）。
    - 分類標籤：支援勾選既有標籤或直接輸入新增自訂標籤。
    - 名片圖片：支援點擊拍照或從手機相簿選取新的正面／反面名片照片。
  - 儲存後立即更新本機 IndexedDB 快取與搜尋索引，列表與詳情頁即時反映修改內容。
* **AC-02（新增名片）**：
  - 於主畫面頂部或浮動操作列提供「➕ 新增名片」按鈕。
  - 支援手動輸入名片資料並選取/拍攝正反面照片，新增成功後自動產生標準 UUID `contact_id` 並存入 IndexedDB。
* **AC-03（刪除與復原）**：
  - 支援對名片標記待刪除 (`pending_delete = 1`)，或從設定中檢視待刪除名片進行「永久刪除」或「復原」。

### 2.2 iPhone / iOS PWA 端：外部檔案匯出 (`MajiOCR.Web`)
* **AC-04（匯出完整備份包 ZIP）**：
  - 於「載入/同步」面板或設定選單新增「📦 匯出備份壓縮包 (.zip)」功能。
  - 前端將本機修改後的 SQLite 資料庫（含 `manifest.json`、`contacts.sqlite3` 與 `card-images/` 目錄圖檔）打包為 `MajiOCR_Backup_iOS_YYYYMMDD_HHmmss.zip`。
  - 呼叫 iOS 原生 Web Share API（`navigator.share({ files: [...] })`）或 HTML5 下載：
    - 在 iPhone 上自動彈出系統原生分享面板，支援「儲存到檔案」、「iCloud Drive」、「AirDrop」或發送至通訊軟體。
* **AC-05（匯出 vCard .vcf 至 iPhone 通訊錄）**：
  - 於名片詳情頁提供「📇 匯出至手機通訊錄 (.vcf)」，以及在清單提供「批次匯出 vCard」。
  - 產生符合 RFC 6350 (vCard 4.0 / 3.0) 規格之 `.vcf` 檔案，在 iOS Safari 點擊開啟即可觸發 iOS 內建「加入聯絡資訊」原生對話框。

### 2.3 iPhone / iOS PWA 端：Google Drive 雙向同步上傳 (`MajiOCR.Web`)
* **AC-06（OAuth 寫入授權）**：
  - Google OAuth 登入授權範圍擴充包含 `https://www.googleapis.com/auth/drive.file`。
* **AC-07（一鍵上傳備份至 Google Drive）**：
  - 於同步面板提供「📤 上傳最新備份至 Google Drive」功能。
  - 點擊後前端自動打包最新備份 ZIP，呼叫 Google Drive REST API Multipart Upload，上傳至雲端 `MajiOCR_Backups` 資料夾。
  - 上傳成功後自動重新整理備份版本清單，顯示剛上傳的 iOS 備份檔（標記 `[IOS]` 徽章）。

### 2.4 資料庫與密碼學相容性 (DB & Cryptography)
* **AC-08（標準 SQLite 重建與 SQLCipher-AES256 加密輸出）**：
  - 若原載入資料庫為未加密標準 SQLite，匯出或上傳時產生標準 `SQLite format 3\0` 資料庫。
  - 若原載入資料庫為加密資料庫（或使用者設定了主密碼），匯出或上傳時自動利用 WebCrypto 重新計算 PBKDF2-HMAC-SHA512 金鑰、重新加密每頁資料並計算 HMAC-SHA512，確保產出之檔案可在 Windows 端與 Android 端無損解密。

---

## 3. 角色分工與交接邊界

| 角色 | 主要職責與交付物 |
| :--- | :--- |
| **PM Agent** | 受理需求、開立 `ISSUE-0049`、產出需求規格書 (`RS-ISSUE-0049-v0.1.md`)、維護 IssueLog 歷程與最終驗收回報。 |
| **SA 系統架構師** | 評估 PWA 前端 SQLite 寫入引擎（SQLite Wasm vs. 結構化重組器）、ZIP 打包效能、Web Share API 與 Google Drive Multipart Upload 協議，產出架構評估書 (`AR-ISSUE-0049-v0.1.md`)。 |
| **設計工程師** | 規劃 iPhone X 上的名片編輯/新增表單 UI、按鈕位置、照片上傳互動、匯出/同步選單與 Toast 狀態回饋，產出設計規劃書 (`DP-ISSUE-0049-v0.1.md`)。 |
| **DBA Agent** | 評估前端編輯資料寫入時之 Schema 完整性（EAV 關聯、標籤關聯、UUID 格式）與 SQLCipher 重新加密規格，產出 DBA 評估書 (`DBA-ISSUE-0049-v0.1.md`)。 |
| **測試工程師** | 制定測試計畫 (`TP-ISSUE-0049-v0.1.md`)，驗證名片編輯/新增、本機 IndexedDB 持久化、ZIP 打包、vCard 格式、Google Drive 上傳協議與回歸相容性，產出測試報告 (`TR-ISSUE-0049-v0.1.md`)。 |
