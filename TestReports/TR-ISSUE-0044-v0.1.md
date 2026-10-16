# 測試報告：支援 iOS / iPhone 平台運行（PWA）、Windows 端 Vercel 一鍵部署精靈與 SQLCipher-AES256 解密查詢 (ISSUE-0044)

- **對應 Issue ID**：`ISSUE-0044`
- **測試報告版本**：`v0.1`
- **關聯需求規格**：[`Requirements/RS-ISSUE-0044-v0.1.md`](../Requirements/RS-ISSUE-0044-v0.1.md)
- **關聯架構評估**：[`Architecture/AR-ISSUE-0044-v0.1.md`](../Architecture/AR-ISSUE-0044-v0.1.md)
- **關聯設計規劃**：[`DesignPlans/DP-ISSUE-0044-v0.1.md`](../DesignPlans/DP-ISSUE-0044-v0.1.md)
- **關聯資料庫評估**：[`DB/DBA-ISSUE-0044-v0.1.md`](../DB/DBA-ISSUE-0044-v0.1.md)
- **關聯測試計畫**：[`TestPlans/TP-ISSUE-0044-v0.1.md`](../TestPlans/TP-ISSUE-0044-v0.1.md)
- **受測軟體版本**：
  - `MajiOCR.Windows`：`0.1.0-beta.24` (`FileVersion: 0.1.0.24`)
  - `MajiOCR.Web`：`0.1.0-beta.1`
- **測試執行日期與環境**：`2026-10-16`（Windows 11 X64 `10.0.26200.0`、.NET 10.0 `win-x64`、Node.js `v24.14.0` WebCrypto）
- **實際執行角色**：測試工程師 Agent（與軟體工程師 Agent 同一 Agent 依序執行並產出獨立結構化證據檔）

---

## 1. 測試執行摘要與方法

1. **C# 整合與發布後單一執行檔自動化測試 (`--test-issue-0044`)**：
   - 命令：`publish\MajiOCR.Windows\MajiOCR.Windows.exe --test-issue-0044`
   - 證據檔案：[`TestReports/evidence/ISSUE-0044/ISSUE-0044_TEST_REPORT.json`](../TestReports/evidence/ISSUE-0044/ISSUE-0044_TEST_REPORT.json)
   - 結果：**6 / 6 PASS (`OverallSuccess: true`)**
2. **JavaScript (`MajiOCR.Web`) WebCrypto + 純 JS SQLite B-Tree 端到端驗證**：
   - 命令：`node verify_web_pwa.mjs`（直接載入 `MajiOCR.Web/js/zip-reader.js`、`sqlcipher-decryptor.js`、`sqlite-parser.js`，針對 `MajiOCR.Windows` 產出之真實 `SQLCipher-AES256` 加密備份 `.zip` 執行解壓、錯誤密碼攔截、WebCrypto 解密與 B-Tree 紀錄解析）
   - 證據檔案：[`TestReports/evidence/ISSUE-0044/ISSUE-0044_WEB_JS_E2E_REPORT.json`](../TestReports/evidence/ISSUE-0044/ISSUE-0044_WEB_JS_E2E_REPORT.json)
   - 結果：**PASS (`overallSuccess: true`)**
3. **既有功能回歸測試 (`--test-issue-0041`, `--test-issue-0043`)**：
   - `ISSUE-0041`（Google Drive 備份／還原與 SQLCipher 加密安全測試）：**9 / 9 PASS (`OverallSuccess: true`)**
   - `ISSUE-0043`（自訂欄位 SQLite 持久化與登入後自動更新檢查測試）：**5 / 5 PASS (`OverallSuccess: true`)**

---

## 2. 測試項目逐項結果對照表

| 測試 ID | 對應需求 / 驗收條件 | 測試內容 | 執行結果 | 關鍵證據數據 |
| :--- | :--- | :--- | :---: | :--- |
| **T1** (`T1-SQLCIPHER-DECRYPT-PARITY`) | AC-07, NFR-01 | 真實 `SQLCipher-AES256` 加密資料庫由 SQLCipher 4 分頁解密器 100% 還原為標準 SQLite，並以無密碼標準 SQLite 讀出名片、自訂欄位與分類標籤 | **PASS** | `rawEncrypted=True, decPlain=True, reserve=80, quickCheck=ok, contacts=1, name=王大明, tag=VIP客戶` (`2424 ms`) |
| **T2** (`T2-SQLCIPHER-WRONG-PASSWORD-REJECT`) | AC-07 | 輸入錯誤主密碼或空密碼時，於 Page 1 HMAC-SHA512 驗證立即攔截並回傳明確錯誤 | **PASS** | `wrongRejected=True (INVALID_PASSWORD), emptyRejected=True` (`161 ms`) |
| **T3** (`T3-EMBEDDED-PWA-ASSETS-AND-PAYLOAD`) | AC-02, AC-12, NFR-02, NFR-03 | 驗證單一執行檔內嵌 10 項 `MajiOCR.Web` 靜態資產齊全、`googleClientId` 正確注入且絕不夾帶本機 `.sqlite3` 資料庫 | **PASS** | `normalized=my-majiocr-ios-app-2026, filesCount=10, hasAllCoreFiles=True, noSqliteIncluded=True, clientIdInjected=True` (`28 ms`) |
| **T4** (`T4-VERCEL-REST-API-DEPLOY-FLOW`) | AC-01, AC-02 | 驗證 `VercelDeploymentService` 正確執行 `GET /v2/user`、`POST /v13/deployments`、`PATCH /v9/projects/{id}` (`ssoProtection: null`) 及 401/403 錯誤攔截 | **PASS** | `unauthBlocked=True, okSuccess=True, url=https://majiocr-ios-pwa.vercel.app, calls=(1,1,1)` (`11 ms`) |
| **T5** (`T5-QRCODE-AND-SETTINGS-PERSISTENCE`) | AC-02, AC-03 | 驗證 `QrCodeBmpGenerator` 輸出合法 BMP 影像、通過 ISO/IEC 18004 Format BCH 雙副本校驗與 Reed-Solomon 解碼還原網址，且 `AppSettingsService` 正確持久化 Vercel 設定 | **PASS** | `qrDimension=29x29, decoded1=https://majiocr-ios-pwa.vercel.app, decoded2=https://majiocr-pwa-demo.example.com, bmpBytes=197190` (`60 ms`) |
| **T6** (`T6-BACKUP-ZIP-DUAL-PIPELINE-COMPATIBILITY`) | AC-04 ~ AC-11 | 驗證加密備份 `.zip` 內含 `manifest.json`、SQLCipher `contacts.sqlite3` 與 `card-images/`，並通過 C# 與 `MajiOCR.Web` JavaScript (WebCrypto + B-Tree) 雙端完整解壓、解密與欄位解析 | **PASS** | `isProtected=True, algo=SQLCipher-AES256, contacts=1, hasImages=True, decryptedValid=True, webJsE2E=True` (`777 ms`) |

---

## 3. 結論與未完成／剩餘風險評估

- **測試結論**：**全部通過（6/6 PASS + WebCrypto E2E PASS + 14/14 回歸測試 PASS）**，滿足 `RS-ISSUE-0044-v0.1` 全數驗收條件（AC-01 ~ AC-12）與非功能需求（NFR-01 ~ NFR-03）。
- **剩餘風險與使用提醒**：
  - 若使用者在 iPhone PWA 使用「管道 ①：Google Drive 雲端直接下載」，需在 Google Cloud Console 建立「Web 應用程式」類型的 OAuth Client ID 並將部署後的 `https://*.vercel.app` 網址加入 `Authorized JavaScript origins`；若不想設定 Google Cloud Console，隨時可直接使用「管道 ②：從 iPhone 檔案 App 選取備份 `.zip` 匯入」，零設定即可離線解密並使用完整功能。
