# 需求規格書：Windows 主程式設定檔完全可攜化與新資料庫免沿用舊密碼詢問 (ISSUE-0051)

- **文件識別碼**：`RS-ISSUE-0051-v0.1`
- **關聯 Issue**：`ISSUE-0051`
- **需求版本**：`v0.1`
- **需求種類**：變更
- **優先級**：高
- **提出來源**：人類使用者
- **受理窗口**：PM Agent
- **負責角色**：PM / SA / DBA / 軟體工程師 / 測試工程師

---

## 1. 背景與問題說明

目前 `MajiOCR.Windows` 雖然將名片資料庫 `contacts.sqlite3`、`card-images/`、`google_oauth_config.json` 與 `UpdateSettings.json` 存放在主程式（`MajiOCR.Windows.exe`）同一目錄下，但仍有兩項設定與機制依賴系統全域 `%LocalAppData%\MajiOCR.Windows\`，導致將主程式移至其他新資料夾（無資料庫）啟動時產生非預期行為：

1. **設定檔與舊版自訂欄位未完全可攜化**：
   - `AppSettingsService` 預設將 `settings.json` 存放於 `%LocalAppData%\MajiOCR.Windows\settings.json`。
   - `CustomFieldCatalogService` 與 `StorageService.TryMigrateLegacyJson` 在資料庫尚無自訂欄位時，會從 `%LocalAppData%\MajiOCR.Windows\custom-fields.json` 自動匯入舊有自訂欄位。
2. **新資料夾（無資料庫或未加密資料庫）誤觸發舊密碼詢問**：
   - `MainPage.InitializeStorageAsync` 啟動時以 `if (settings.IsPasswordProtected || StorageService.IsDatabaseEncrypted(dbPath))` 判斷是否彈出密碼解鎖對話框。
   - 當使用者將 `MajiOCR.Windows.exe` 移至沒有 `contacts.sqlite3` 的新資料夾啟動時，程式讀到 `%LocalAppData%` 的 `settings.json`（`IsPasswordProtected == true`），仍會彈出「🔐 名片管理資料庫已加密」解鎖視窗，並在輸入密碼後將新建立的空白資料庫直接加密，且帶入 `%LocalAppData%` 的自訂欄位，讓使用者誤以為連回了舊資料夾的資料庫。

### 1.1 人類使用者已確認決策（2026-10-18）

人類使用者明確指定採行 **方向 1（完全可攜化 Portable Mode）** 與 **方向 2（新資料庫不沿用舊密碼詢問）**：
1. **完全可攜化（Portable Mode）**：將 `settings.json` 與 `custom-fields.json` 改為存放在與主程式同一路徑下（`<主程式所在目錄>\settings.json`、`<主程式所在目錄>\custom-fields.json`），不再跨資料夾共用 `%LocalAppData%\MajiOCR.Windows\` 的設定與自訂欄位。
2. **新資料庫不沿用舊密碼詢問**：啟動時以主程式同目錄下 `contacts.sqlite3` 檔案本身是否實際為 SQLCipher 加密格式（`StorageService.IsDatabaseEncrypted(dbPath)`）作為是否要求輸入主密碼的唯一依據；若同目錄下無 `contacts.sqlite3` 或該資料庫未加密，啟動時絕不要求輸入主密碼，並以標準未加密模式建立或開啟資料庫。

---

## 2. 功能與非功能需求

### 2.1 設定檔與自訂欄位完全可攜化（Portable Mode）
1. **`AppSettingsService` 預設路徑可攜化**：
   - 當未傳入自訂 `customPath` 時，預設路徑由 `%LocalAppData%\MajiOCR.Windows\settings.json` 改為 `<主程式所在目錄>\settings.json`（透過 `StoragePathHelper.GetDefaultStorageRoot()` 解析；若處於 `--gui-test-root` 測試模式則為 `<GuiVerificationRoot>\settings.json`）。
   - **原工作目錄平滑遷移（僅限同目錄已存在 `contacts.sqlite3` 時）**：若 `<主程式所在目錄>\settings.json` 尚不存在，但同目錄下**已存在 `contacts.sqlite3`**（代表這是使用者原本正在使用的程式目錄，而非剛複製到其他位置的空目錄），且 `%LocalAppData%\MajiOCR.Windows\settings.json` 存在時，自動將舊設定複製遷移至 `<主程式所在目錄>\settings.json`。
   - **新資料夾完全隔離**：若同目錄下**不存在 `contacts.sqlite3`**，且無 `<主程式所在目錄>\settings.json`，則視為全新的可攜式環境，**不得**從 `%LocalAppData%\MajiOCR.Windows\settings.json` 讀取或遷移任何設定。
2. **`CustomFieldCatalogService` 與 `StorageService.TryMigrateLegacyJson` 可攜化**：
   - `CustomFieldCatalogService` 預設路徑改為 `<主程式所在目錄>\custom-fields.json`。
   - `StorageService.TryMigrateLegacyJson` 在未指定 `_legacyJsonPathOverride` 且處於應用程式目錄模式時，僅檢查 `<_storageRoot>\custom-fields.json`（即主程式同目錄），**不再讀取 `%LocalAppData%\MajiOCR.Windows\custom-fields.json`**，確保新資料夾建立的空白資料庫不會被塞入其他資料夾的舊自訂欄位。

### 2.2 啟動密碼解鎖與資料庫實際加密狀態同步
1. **以 `contacts.sqlite3` 實際加密狀態為唯一解鎖依據**：
   - 新增可獨立驗證的啟動安全狀態協調邏輯（`StorageService.ResolveStartupSecurityState(string dbPath, IAppSettingsService settingsService)`）：
     - 若 `StorageService.IsDatabaseEncrypted(dbPath)` 為 `true`：判定需要輸入主密碼解鎖（`RequiresUnlock = true`）。
     - 若 `StorageService.IsDatabaseEncrypted(dbPath)` 為 `false`（包含同目錄下尚無 `contacts.sqlite3` 的全新環境，或同目錄下的 `contacts.sqlite3` 為標準未加密 SQLite）：判定**不需要輸入主密碼**（`RequiresUnlock = false`）。若此時 `settings.json` 中殘留 `IsPasswordProtected == true`，自動呼叫 `settingsService.UpdateSecuritySettings(false, null, null)` 將設定檔校正為未加密狀態。
2. **啟動流程與安全設定對話框一致性**：
   - `MainPage.InitializeStorageAsync` 改用 `StorageService.ResolveStartupSecurityState` 決定是否彈出 `SecurityDialogs.ShowUnlockDialogAsync`。當 `RequiresUnlock == false` 時，`passphrase` 固定為 `null`，確保新建的 `contacts.sqlite3` 為標準未加密 SQLite 資料庫。
   - `SecurityDialogs.ShowUnlockDialogAsync` 與 `ShowSecuritySettingsDialogAsync` 均以資料庫實際加密狀態（`StorageService.IsDatabaseEncrypted(dbPath)` / `storageService.IsEncrypted`）為準。

---

## 3. 角色評估與影響判定

| 角色 | 判定與建議 | 關聯文件 |
| --- | --- | --- |
| **SA 系統架構師** | **有架構影響（可採用）**：將 `settings.json` 與 `custom-fields.json` 儲存邊界統一收斂至主程式同目錄（`StoragePathHelper.GetDefaultStorageRoot()`），並以 `contacts.sqlite3` 檔案標頭作為加密與否之單一真相來源（Single Source of Truth）。 | [AR-ISSUE-0051-v0.1.md](../Architecture/AR-ISSUE-0051-v0.1.md) |
| **DBA** | **有資料庫行為影響（可採用）**：不變更 SQLite Schema，但修正新資料庫初始化時的加密觸發條件與 `TryMigrateLegacyJson` 來源路徑，防止新資料庫被誤用舊密碼加密或混入 `%LocalAppData%` 舊自訂欄位。 | [DBA-ISSUE-0051-v0.1.md](../DB/DBA-ISSUE-0051-v0.1.md) |
| **設計工程師 (UI/UX)** | **不適用**：不涉及 UI 視覺版面或新增對話框設計，僅修正啟動時解鎖對話框的觸發條件。 | 不適用 |
| **系統整合工程師** | **不適用**：純單機檔案路徑與資料庫初始化邏輯變更，無外部系統契約變更。 | 不適用 |
| **維運工程師** | **不適用**：單機可攜式桌面應用程式，無伺服器維運變更；具備原目錄設定檔平滑遷移能力。 | 不適用 |
| **測試工程師** | **適用**：新增 `Issue0051AutomatedTestSuite` (`--test-issue-0051`) 驗證可攜式路徑、新資料夾無 DB 不觸發密碼詢問與不污染自訂欄位、原目錄含 DB 平滑遷移及已加密 DB 正常解鎖，並執行相關回歸測試。 | [TP-ISSUE-0051-v0.1.md](../TestPlans/TP-ISSUE-0051-v0.1.md) |

---

## 4. 驗收條件 (Acceptance Criteria)

- **AC-01（設定檔完全可攜化）**：`AppSettingsService` 與 `CustomFieldCatalogService` 在預設模式下，讀寫路徑均位於主程式同目錄（`<主程式所在目錄>\settings.json` 與 `<主程式所在目錄>\custom-fields.json`），不再寫入 `%LocalAppData%\MajiOCR.Windows\`。
- **AC-02（新資料夾無資料庫隔離與免密碼啟動）**：當主程式位於沒有 `contacts.sqlite3` 的新資料夾啟動時：
  1. 即使系統 `%LocalAppData%\MajiOCR.Windows\settings.json` 存在且 `IsPasswordProtected == true`，**不得**遷移該設定，且啟動時**不彈出主密碼詢問視窗**。
  2. 自動於該新資料夾建立標準**未加密**的 `contacts.sqlite3`（`IsDatabaseEncrypted == false`）。
  3. 即使系統 `%LocalAppData%\MajiOCR.Windows\custom-fields.json` 存在，新資料庫的 `custom_field_definitions` 維持 0 筆，**不自動匯入**跨資料夾的舊自訂欄位。
- **AC-03（未加密資料庫與孤立密碼設定自動校正）**：若資料夾內的 `settings.json` 標記 `IsPasswordProtected == true`，但同目錄下無 `contacts.sqlite3` 或 `contacts.sqlite3` 為未加密狀態（`IsDatabaseEncrypted == false`），啟動時不要求輸入密碼，並自動將該目錄 `settings.json` 的 `IsPasswordProtected` 校正為 `false`（清除孤立 `PasswordHash` / `PasswordSalt`）。
- **AC-04（已加密資料庫正常保護與原目錄平滑遷移）**：
  1. 若資料夾內已存在 SQLCipher 加密的 `contacts.sqlite3`（`IsDatabaseEncrypted == true`），啟動時必定要求輸入主密碼解鎖；即使該資料夾尚無 `settings.json`，解鎖成功後也會自動同步寫入該資料夾的 `settings.json`。
  2. 若原工作資料夾已存在 `contacts.sqlite3` 但尚無同目錄 `settings.json`，首次啟動時可平滑遷移 `%LocalAppData%\MajiOCR.Windows\settings.json` 的偏好設定至同目錄 `settings.json`。
- **AC-05（自動化測試與發布驗證）**：新增 `--test-issue-0051` 自動化測試全數通過，既有 `--test-issue-0036`、`--test-issue-0038`、`--test-issue-0043`、`--test-issue-0050` 回歸測試全數通過，版本推進至 `0.1.0-beta.31`（`0.1.0.31`）並完成 `publish/MajiOCR.Windows/MajiOCR.Windows.exe` 發布與舊目錄遷移實證。

---

## 5. 獨立程式資料夾、固定發布資料夾與不包含項目

- **獨立程式資料夾**：`MajiOCR.Windows/`（本次修改範圍）、`MajiOCR.Web/`、`MajiOCR/`
- **固定發布資料夾**：`publish/MajiOCR.Windows/MajiOCR.Windows.exe`
- **本次版本策略**：測試版 `0.1.0-beta.31`（FileVersion `0.1.0.31`，尚未發布 `v1.0.0` 正式版）
- **不在本次範圍內的項目**：
  - 不變更「無 `contacts.sqlite3` 時於主程式同目錄自動建立全新空白未加密資料庫」之預設可攜式初始化機制。
  - 不變更 SQLite 資料表結構（Schema v2）。
