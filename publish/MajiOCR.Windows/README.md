# MajiOCR.Windows 0.1.0-beta.31 (設定檔完全可攜化與新資料庫免沿用舊密碼詢問)

目前為本機發布版，更新日期為 2026-10-18（Asia/Taipei）。
直接執行本資料夾的 `MajiOCR.Windows.exe`。維持單一免安裝、自含執行檔（Self-Contained Single-File Executable）及離線 OCR 模型，並內嵌完整 `MajiOCR.Web` (`0.2.0-beta.1`) 靜態 PWA 資產包。

### 本版更新重點 (`ISSUE-0051`)
1. **設定檔與自訂欄位完全可攜化（Portable Mode）**：
   - `AppSettingsService`（`settings.json`）與 `CustomFieldCatalogService` / `StorageService.TryMigrateLegacyJson`（`custom-fields.json`）預設路徑全面改為與主程式同一目錄下（`<主程式所在目錄>\settings.json`、`<主程式所在目錄>\custom-fields.json`），不再跨目錄共用 `%LocalAppData%\MajiOCR.Windows\` 的全域設定與舊自訂欄位。
   - 支援原工作目錄（同目錄下已存在 `contacts.sqlite3`）首次啟動時，自動將舊版設定檔平滑遷移至主程式同目錄。
2. **新資料庫免沿用舊密碼詢問（以 `contacts.sqlite3` 實際加密標頭為唯一依據）**：
   - 新增 `StorageService.ResolveStartupSecurityState`：啟動時僅在同目錄下的 `contacts.sqlite3` 存在且實際為 SQLCipher 加密格式（`IsDatabaseEncrypted(dbPath) == true`）時，才彈出「🔐 名片管理資料庫已加密」解鎖對話框。
   - 當主程式移至沒有 `contacts.sqlite3` 的新資料夾，或同目錄下為未加密資料庫時，啟動時絕不詢問舊密碼，並自動將孤立的 `IsPasswordProtected` 校正為 `false`，確保新建資料庫為乾淨未加密狀態。

---
- **FileVersion**：0.1.0.31 (`0.1.0-beta.31`)
- **SHA-256**：`3D3C65560198FE36AF95A3F6C7EA0EE825DB0692A90007031BD1C238276AC218`
- **檔案大小**：299,882,019 位元組 (~286.0 MB)

本機單檔發布驗證與自動化測試套件（`ISSUE-0051` 5/5 PASS、`ISSUE-0050` 5/5 PASS、`ISSUE-0043` 5/5 PASS、`ISSUE-0038` 11/11 PASS、`ISSUE-0036` 7/7 PASS）均已全數通過。
