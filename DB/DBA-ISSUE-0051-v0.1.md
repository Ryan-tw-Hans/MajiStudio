# 資料庫影響評估書：ISSUE-0051 Windows 主程式設定檔完全可攜化與新資料庫免沿用舊密碼詢問

- **文件識別碼**：`DBA-ISSUE-0051-v0.1`
- **關聯 Issue**：`ISSUE-0051`
- **關聯需求規格**：[RS-ISSUE-0051-v0.1.md](../Requirements/RS-ISSUE-0051-v0.1.md)
- **關聯架構評估**：[AR-ISSUE-0051-v0.1.md](../Architecture/AR-ISSUE-0051-v0.1.md)
- **評估角色**：DBA Agent（單一 Agent 扮演 DBA 自查）
- **日期**：`2026-10-18T08:41:00+08:00`

---

## 1. 資料庫影響範圍判定

1. **SQLite Schema 與索引變更**：**無變更**（維持 Schema `user_version = 2`，包含 `contact_records`、`contact_values`、`card_image_assets`、`pending_delete`、`contact_tag_catalog`、`contact_tag_links`、`custom_field_definitions`）。
2. **新資料庫建立與加密模式判定（`StorageService`）**：
   - **問題根因**：先前 `MainPage.InitializeStorageAsync` 在 `contacts.sqlite3` 尚未存在時，若 `settings.IsPasswordProtected == true`，會先向使用者索取密碼並將 `passphrase` 傳入 `new StorageService(..., passphrase: passphrase)`。這導致 `BuildConnectionString` 在建立新資料庫時帶入 `Password = passphrase`，使全新的空白 `contacts.sqlite3` 在未經使用者於該資料庫啟用加密的情況下，直接被建立為 SQLCipher 加密資料庫。
   - **修正契約**：
     - 資料庫是否處於加密狀態，唯一以磁碟上 `contacts.sqlite3` 的前 16 bytes 檔案標頭（`StorageService.IsDatabaseEncrypted(dbPath)`：非 `"SQLite format 3\0"`）為準。
     - 當 `contacts.sqlite3` 不存在（全新資料庫）或為標準 SQLite 明文標頭時，`StorageService.ResolveStartupSecurityState` 必定回傳 `RequiresUnlock = false`，且傳入 `StorageService` 的 `passphrase` 為 `null`，確保新建的資料庫永遠為標準未加密 SQLite 格式。
3. **舊版自訂欄位匯入邊界（`StorageService.CustomFields.cs`）**：
   - **問題根因**：`TryMigrateLegacyJson` 先前在 `_applicationDirectoryMode == true` 時，會去讀取 `%LocalAppData%\MajiOCR.Windows\custom-fields.json`。當使用者將主程式移至其他空資料夾啟動時，新建的空白 `contacts.sqlite3`（`custom_field_definitions` 為 0 筆）會被自動寫入 `%LocalAppData%` 中的舊自訂欄位。
   - **修正契約**：`TryMigrateLegacyJson` 在 `_applicationDirectoryMode == true` 且無 `_legacyJsonPathOverride` 時，僅檢查 `Path.Combine(_storageRoot, "custom-fields.json")`（即與 `contacts.sqlite3` 同一目錄下的 `custom-fields.json`），不再跨目錄讀取 `%LocalAppData%`。

---

## 2. 資料完整性、備份與回復評估

- **已加密資料庫安全性**：當同目錄下的 `contacts.sqlite3` 確實為 SQLCipher 加密資料庫時（`IsDatabaseEncrypted(dbPath) == true`），無論同目錄下是否存在 `settings.json`，啟動時皆會強制要求輸入主密碼並透過 `StorageService.TryVerifyPassphrase(dbPath, entered, out _)` 驗證通過後才可開啟，不會降低任何既有加密資料庫的保護強度。
- **備份與還原相容性**：Google Drive 雲端備份與本機匯出／匯入完全基於 `contacts.sqlite3` 與 `card-images/`，不受 `settings.json` 改為同目錄可攜存放之負面影響。

---

## 3. DBA 評估結論

- **結論**：**可採用**。修正後可確保每個資料夾的 `contacts.sqlite3` 具備完整的生命週期獨立性與加密狀態一致性。
