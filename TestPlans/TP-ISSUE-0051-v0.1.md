# 測試計畫書：ISSUE-0051 Windows 主程式設定檔完全可攜化與新資料庫免沿用舊密碼詢問

- **文件識別碼**：`TP-ISSUE-0051-v0.1`
- **關聯 Issue**：`ISSUE-0051`
- **關聯需求規格**：[RS-ISSUE-0051-v0.1.md](../Requirements/RS-ISSUE-0051-v0.1.md)
- **關聯架構評估**：[AR-ISSUE-0051-v0.1.md](../Architecture/AR-ISSUE-0051-v0.1.md)
- **關聯資料庫評估**：[DBA-ISSUE-0051-v0.1.md](../DB/DBA-ISSUE-0051-v0.1.md)
- **測試版本**：`0.1.0-beta.31` (`0.1.0.31`)
- **負責角色**：測試工程師 Agent

---

## 1. 測試風險與範圍評估

1. **測試目標**：
   - 驗證 `AppSettingsService` 與 `CustomFieldCatalogService` 的預設檔案路徑已改為跟隨主程式所在目錄（`<StorageRoot>\settings.json` 與 `<StorageRoot>\custom-fields.json`）。
   - 驗證將主程式移至沒有 `contacts.sqlite3` 的新資料夾時，不會從舊設定檔（`%LocalAppData%\MajiOCR.Windows\`）繼承 `settings.json` 或 `custom-fields.json`，且新建的 `contacts.sqlite3` 為未加密狀態、自訂欄位為 0 筆。
   - 驗證啟動安全狀態判定 `StorageService.ResolveStartupSecurityState`：當同目錄無資料庫或資料庫未加密時，即使 `settings.json` 記載 `IsPasswordProtected = true`，也不會要求輸入密碼（`RequiresUnlock = false`），並自動將 `settings.json` 的 `IsPasswordProtected` 校正為 `false`。
   - 驗證當同目錄已存在加密的 `contacts.sqlite3` 時，無論有無 `settings.json`，`ResolveStartupSecurityState` 必定要求密碼解鎖（`RequiresUnlock = true`），且密碼驗證通過後自動同步寫入同目錄 `settings.json`。
   - 驗證原工作目錄（同目錄已存在 `contacts.sqlite3`，但尚無同目錄 `settings.json`）首次啟動時，可將舊設定檔平滑遷移至同目錄 `settings.json`。
2. **受影響模組**：
   - `MajiOCR.Windows/Services/AppSettingsService.cs`
   - `MajiOCR.Windows/Services/CustomFieldCatalogService.cs`
   - `MajiOCR.Windows/Services/Storage/StorageService.cs`
   - `MajiOCR.Windows/Services/Storage/StorageService.CustomFields.cs`
   - `MajiOCR.Windows/MainPage.xaml.cs`
   - `MajiOCR.Windows/SecurityDialogs.cs`
   - `MajiOCR.Windows/ViewModels/MainViewModel.cs`

---

## 2. 測試項目定義 (`--test-issue-0051`)

| 測試代號 | 對應驗收條件 | 測試名稱與步驟 | 預期結果與通過標準 |
| --- | --- | --- | --- |
| **TC-0051-01** | AC-01 | **設定檔與自訂欄位目錄預設路徑可攜化驗證**：以無參數建構 `AppSettingsService()` 與 `CustomFieldCatalogService()`，檢查 `SettingsFilePath` 與 `CatalogFilePath` 所在目錄 | 兩者所在目錄均等於 `StoragePathHelper.GetDefaultStorageRoot()`（主程式所在目錄），且不包含 `AppData\Local` |
| **TC-0051-02** | AC-02 | **新資料夾（無 `contacts.sqlite3`）完全隔離與不沿用舊設定／舊自訂欄位驗證**：模擬主程式移至空資料夾（無 `contacts.sqlite3`），即使傳入模擬的舊 `%LocalAppData%` 路徑含有 `IsPasswordProtected=true` 的 `settings.json` 與 `custom-fields.json`，執行設定載入、`ResolveStartupSecurityState` 與 `StorageService` 初始化 | 1. 不遷移舊 `settings.json`；2. `RequiresUnlock == false`（不詢問密碼）；3. 新建的 `contacts.sqlite3` 為未加密狀態（`IsEncrypted == false`）；4. 新資料庫 `GetCustomFieldDefinitions()` 為 0 筆（不匯入跨目錄舊自訂欄位） |
| **TC-0051-03** | AC-03 | **孤立密碼設定（`IsPasswordProtected=true` 但無資料庫或資料庫未加密）自動校正與免密碼啟動驗證**：在目錄下放置 `IsPasswordProtected=true` 的 `settings.json`，分別測試「尚無 `contacts.sqlite3`」與「已有未加密 `contacts.sqlite3`」兩種情境下呼叫 `StorageService.ResolveStartupSecurityState` | 兩種情境下 `RequiresUnlock` 均為 `false`，且 `settings.json` 中的 `IsPasswordProtected` 自動被重設為 `false`、`PasswordHash` 與 `PasswordSalt` 清除為 `null` |
| **TC-0051-04** | AC-04 | **已加密 `contacts.sqlite3` 強制解鎖保護與設定檔自動重建驗證**：在目錄下建立 SQLCipher 加密的 `contacts.sqlite3`，並刪除同目錄 `settings.json`（或設為 `IsPasswordProtected=false`），呼叫 `ResolveStartupSecurityState` 與密碼驗證 | `RequiresUnlock` 必為 `true`；透過 `TryVerifyPassphrase` 驗證正確密碼通過、錯誤密碼拒絕，且解鎖後可正常讀寫加密資料庫 |
| **TC-0051-05** | AC-04 | **原工作目錄（已存在 `contacts.sqlite3`）舊設定平滑遷移驗證**：在已有 `contacts.sqlite3` 但尚無 `settings.json` 的目錄下，提供含自訂 OCR 語系與 DPAPI Token 的舊設定檔來源，初始化 `AppSettingsService` | 同目錄下自動生成遷移後的 `settings.json`，且完整保留 OCR 語系與憑證設定 |

---

## 3. 回歸測試與發布驗證

1. 執行 `--test-issue-0051` 並產出 `TestReports/evidence/ISSUE-0051/ISSUE-0051_TEST_REPORT.json`。
2. 執行相關回歸測試：`--test-issue-0036`（設定檔讀寫）、`--test-issue-0038`（SQLCipher 加密與密碼管理）、`--test-issue-0043`（自訂欄位 SQLite 儲存與同目錄遷移）、`--test-issue-0050`（DPAPI 憑證保護與儲存庫敏感資訊檢查）。
3. 推進版本至 `0.1.0-beta.31`（`0.1.0.31`），發布單一執行檔至 `publish/MajiOCR.Windows/MajiOCR.Windows.exe`，並以發布後之 `.exe` 直接執行 `--test-issue-0051` 驗證通過。
