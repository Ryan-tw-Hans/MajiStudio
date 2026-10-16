# 需求規格書：支援 iOS / iPhone (iPhone X 以上) 平台運行、Vercel 一鍵部署精靈與加密資料庫還原查詢 (ISSUE-0044)

- **對應 Issue ID**：`ISSUE-0044`
- **需求版本**：`v0.1`
- **建立／更新時間**：`2026-10-16T16:25:00+08:00`
- **負責窗口**：PM Agent

---

## 1. 問題背景與目標

1. **現況問題**：
   - 目前 MajiOCR 僅提供 Android 原生版 (`MajiOCR`) 與 Windows 原生桌面版 (`MajiOCR.Windows`)，無法在 iOS / iPhone 裝置上運行。
   - 使用者無 Mac 電腦且無 iOS 開發經驗，希望能在 **iPhone X (iOS 16+) 以上**裝置安裝並使用 MajiOCR。
2. **核心目標**：
   - 建立獨立的跨平台行動網頁應用程式 **`MajiOCR.Web` (iOS PWA 漸進式網頁應用)**，支援透過 Safari「加入主畫面」安裝至 iPhone X 以上主畫面，並可離線運作。
   - 於 **`MajiOCR.Windows`** 提供 **「📱 部署 iOS / Web 雲端版 (Vercel)」UI 精靈**，引導使用者註冊／登入 Vercel 並取得 Token，透過 Vercel REST API (`POST /v13/deployments`) 自動將內嵌的 `MajiOCR.Web` 靜態資產包部署至使用者的 Vercel 帳號，並即時產生專屬 HTTPS 網址與 QR Code 供 iPhone 掃描安裝。
   - 在 iPhone PWA 端同時支援 **「① 直接登入 Google Drive 雲端下載備份包 (`*.zip`)」** 與 **「② 從 iPhone 本機『檔案』App 選取備份包 (`*.zip` / `*.sqlite3`) 匯入」** 兩種載入管道。
   - 在 iPhone PWA 端完整支援讀取 **未加密標準 SQLite** 及 **主密碼加密資料庫（`SQLCipher-AES256`）**，將解壓與解密後的名片資料庫與名片圖檔安全保存於 iPhone 本機沙盒儲存空間（IndexedDB），並提供**通訊錄檢視、名片原圖檢視、複合關鍵字搜尋、分類標籤篩選、一鍵撥號／寄信／地圖導航及 vCard (`.vcf`) 匯出**功能。

---

## 2. 功能與非功能需求及驗收條件 (AC)

### 2.1 Windows 端「Vercel 雲端一鍵部署精靈」(`MajiOCR.Windows`)
* **AC-01（入口設計）**：於 `MajiOCR.Windows` 主畫面命令列選單（`MainPage.xaml` `SecondaryCommands`）及「已存名片清單」（`SavedCardsTablePanel` 匯出與雲端選單）新增 **「📱 部署 iOS / Web 雲端版 (Vercel)...」** 選項。
* **AC-02（註冊／登入引導與設定持久化）**：
  - 彈出「Vercel 雲端一鍵部署精靈」對話框（`VercelDeployDialog`），提供一鍵開啟瀏覽器前往 Vercel 註冊／登入頁（`https://vercel.com/signup`）與 Access Token 建立頁（`https://vercel.com/account/tokens`）的按鈕與步驟說明。
  - 支援輸入與儲存 **Vercel Access Token**、**自訂專案名稱**（預設 `majiocr-ios-pwa`，自動轉為小寫合法字元）及選填之 **Web 端 Google OAuth Client ID**（供 iPhone PWA 直接連線 Google Drive 使用）。
* **AC-03（內嵌 PWA 靜態包與 Vercel REST API 自動部署）**：
  - `MajiOCR.Windows.exe` 單一執行檔內嵌完整之 `MajiOCR.Web` PWA 靜態資產。
  - 點擊「🚀 一鍵導入並發布至 Vercel」後，`VercelDeploymentService` 呼叫 Vercel 官方 REST API (`GET /v2/user` 驗證 Token 身分、`POST /v13/deployments` 上傳內嵌 PWA 檔案清單並設定 `target: "production"`)。
  - 部署成功後，自動於對話框顯示正式 HTTPS 網址（`https://<project>.vercel.app`）、一鍵複製網址按鈕，以及於本機動態繪製之 **iPhone 掃描安裝 QR Code**。

### 2.2 iPhone / iOS PWA 端：雙管道載入雲端／本機資料庫 (`MajiOCR.Web`)
* **AC-04（PWA 規格與 iPhone X 相容性）**：
  - 具備完整 `manifest.webmanifest`、`sw.js` (Service Worker 離線快取)、Apple Touch Icon 與 `apple-mobile-web-app-capable` meta 標籤，支援 iPhone X (iOS 16+) Safari「加入主畫面」全螢幕獨立視窗運行與安全區域 (`safe-area-inset`) 版面配置。
* **AC-05（管道 ①：Google Drive 雲端備份下載）**：
  - 支援在 PWA 內設定或讀取部署時帶入之 Google OAuth Client ID（或輸入授權 Token），透過 Google Identity Services / Drive REST API v3 查詢 `MajiOCR_Backups` 資料夾內的備份版本清單（顯示備份時間、平台 `WINDOWS`/`ANDROID`、名片數、圖片數，以及 `🔒 已加密 (SQLCipher)` / `🔓 未加密` 標籤），並可直接下載所選 `.zip` 備份包。
* **AC-06（管道 ②：iPhone 本機「檔案」App 匯入）**：
  - 支援點擊「📂 從手機檔案匯入備份 (.zip / .sqlite3)」，直接透過 iOS 原生檔案選擇器選取自 Google Drive App 或 AirDrop 下載之 `MajiOCR_Backup_*.zip` 或 `contacts.sqlite3` 檔案。

### 2.3 SQLCipher-AES256 主密碼解密與 iPhone 本機持久化
* **AC-07（自動偵測加密與主密碼解密驗證）**：
  - 當載入之 `contacts.sqlite3` 檔案標頭非標準 `"SQLite format 3\0"`（或 `manifest.json` 標記 `is_password_protected: true` / `SQLCipher-AES256`）時，自動彈出「🔐 輸入備份主密碼」對話框。
  - 內建瀏覽器端 WebCrypto (`crypto.subtle`) 實作之 **SQLCipher 4 標準解密引擎**（PBKDF2-HMAC-SHA512 256,000 次迭代衍生金鑰、HMAC-SHA512 頁面完整性驗證、AES-256-CBC 逐頁解密），與 `MajiOCR.Windows` (`SQLitePCLRaw.bundle_e_sqlcipher`) 產出之加密資料庫 100% 二進位相容。
  - 若密碼錯誤，HMAC 驗證立即攔截並提示「主密碼錯誤或無法解密資料庫」，不覆寫既有本機快取資料。
* **AC-08（iPhone 本機 IndexedDB 持久化與離線啟動）**：
  - 解壓縮並驗證成功後的資料庫與 `card-images/` 名片正反面圖片，完整儲存於 iPhone 瀏覽器本機 IndexedDB (`MajiOcrLocalDB`)。
  - 若該備份為加密資料庫，本機 IndexedDB 亦以使用者主密碼衍生之 AES-GCM 金鑰加密保存（或於開啟 App 時要求輸入主密碼解鎖），確保本機資料安全且下次開啟 App 免重新下載即可離線秒速查詢。

### 2.4 通訊錄資料檢視、複合篩選查詢與行動端操作
* **AC-09（通訊錄清單與複合關鍵字查詢）**：
  - 完整讀取 SQLite `contact_records`、`contact_values`、`card_image_assets`、`contact_tag_catalog`、`contact_record_tags` 與 `custom_field_definitions` 資料表（排除 `pending_delete = 1`）。
  - 支援多關鍵字空白分隔之 AND 複合搜尋（比對姓名、公司、職稱、電話、Email、地址、網站、備註、標籤及自訂欄位）與關鍵字高亮顯示。
* **AC-10（分類標籤篩選與排序）**：
  - 支援點擊分類標籤膠囊（Tag Chips）進行單選／多選篩選（含「未分類」篩選），以及依建立時間、姓名、公司排序。
* **AC-11（名片詳情、正反面原圖檢視與 iPhone 行動快捷操作）**：
  - 點擊名片展開詳情頁，檢視完整固定欄位、自訂欄位、分類標籤，以及名片正反面原圖（支援點擊放大檢視）。
  - 支援一鍵撥打電話 (`tel:`)、發送郵件 (`mailto:`)、開啟地圖導航、複製欄位文字，以及匯出單筆／全體聯絡人為通用 `vCard (.vcf)` 供 iPhone 直接加入系統通訊錄。

---

## 3. 角色評估與跨領域判定

| 角色 | 適用性與結論摘要 | 關聯正式文件 |
| :--- | :--- | :--- |
| **SA 系統架構師** | **有架構影響（有條件採用）**：新增獨立 `MajiOCR.Web` 前端 PWA 程式資料夾，並於 `MajiOCR.Windows` 新增 `VercelDeploymentService`、QR Code 產生器與內嵌 PWA 打包資源。 | [`Architecture/AR-ISSUE-0044-v0.1.md`](../Architecture/AR-ISSUE-0044-v0.1.md) |
| **系統整合工程師** | **適用（已併入架構評估）**：涵蓋 Windows 端對 Vercel REST API (`v13/deployments`) 的介面契約，以及 iPhone PWA 端對 Google Drive REST API v3 / OAuth 2.0 與 iOS Safari WebCrypto / IndexedDB 沙盒整合。 | [`Architecture/AR-ISSUE-0044-v0.1.md`](../Architecture/AR-ISSUE-0044-v0.1.md) |
| **維運工程師** | **不適用**：Vercel 為無狀態靜態 CDN 託管，資料庫與圖片均保存在使用者個人 Google Drive 與 iPhone 本機 IndexedDB，無後端伺服器或長期運行服務需監控維運。 | 記錄於本規格書 |
| **設計工程師** | **適用**：規劃 Windows 端「Vercel 雲端一鍵部署精靈」對話框與 QR Code 呈現，以及 iPhone X 響應式 PWA 通訊錄、篩選列、雲端／本機匯入抽屜與密碼解鎖視窗。 | [`DesignPlans/DP-ISSUE-0044-v0.1.md`](../DesignPlans/DP-ISSUE-0044-v0.1.md) |
| **DBA** | **適用**：確認 `contacts.sqlite3` 跨平台唯讀查詢 Schema 相容性，並定義 `SQLCipher-AES256` (SQLCipher 4) 於瀏覽器 WebCrypto 之解密與 HMAC 驗證規格。 | [`DB/DBA-ISSUE-0044-v0.1.md`](../DB/DBA-ISSUE-0044-v0.1.md) |
| **測試工程師** | **適用**：涵蓋 SQLCipher 跨平台二進位解密驗證、Vercel API 打包與請求契約測試、ZIP 解壓與查詢篩選自動化測試（`Issue0044AutomatedTestSuite`）。 | [`TestPlans/TP-ISSUE-0044-v0.1.md`](../TestPlans/TP-ISSUE-0044-v0.1.md) |

---

## 4. 獨立程式資料夾、固定發布資料夾與版本策略

| 程式識別名 | 獨立程式資料夾 | 固定發布資料夾 | 本次目標版本 |
| :--- | :--- | :--- | :--- |
| `MajiOCR.Web` | `MajiOCR.Web/` | `publish/MajiOCR.Web/`（並同步內嵌至 `MajiOCR.Windows`） | `0.1.0-beta.1` |
| `MajiOCR.Windows` | `MajiOCR.Windows/` | `publish/MajiOCR.Windows/MajiOCR.Windows.exe` | `0.1.0-beta.24` (`0.1.0.24`) |
| `MajiOCR` (Android) | `MajiOCR/` | `publish/MajiOCR/` | 本次不變更（維持 `0.2.0-beta.3`） |

---

## 5. 不在本次範圍內的項目 (Out of Scope)

1. Android 版 (`MajiOCR`) 內建 Vercel 部署 UI（依使用者決策，本次先以 Windows 版支援即可）。
2. 原生 `.ipa` 編譯與 Apple Developer 付費帳號 TestFlight 上架。
3. 在 iPhone PWA 端直接進行相機即時框選 OCR 並回寫覆蓋 Google Drive 備份（本次聚焦於雲端／本機資料庫載入、SQLCipher 解密、通訊錄檢視、篩選查詢與聯絡人匯出）。
