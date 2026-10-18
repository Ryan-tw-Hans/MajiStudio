# 架構評估書：ISSUE-0051 Windows 主程式設定檔完全可攜化與新資料庫免沿用舊密碼詢問

## 1. 基本資料與影響判定

| 項目 | 內容 |
| --- | --- |
| Issue ID／需求版本與連結 | `ISSUE-0051` / `v0.1` ([RS-ISSUE-0051-v0.1.md](../Requirements/RS-ISSUE-0051-v0.1.md)) |
| IssueLog 索引／最近事件／文件 metadata | 本機 `IssueLog/IssueLog.sqlite3` (`ISSUE-0051`，狀態：進行中) |
| 評估版本／日期與時區 | `v0.1` / `2026-10-18T08:40:00+08:00` (`Asia/Taipei`) |
| SA 執行者／審查方式 | 單一 Agent 扮演 SA 系統架構師進行現況盤點與自查 |
| 現況依據版本 | `MajiOCR.Windows` `0.1.0-beta.30` (`d9b90ec`) |
| 類型及評估觸發原因 | 調整本機儲存邊界（將 `settings.json` 與 `custom-fields.json` 由 `%LocalAppData%\MajiOCR.Windows` 收斂至主程式所在可攜目錄）與啟動安全控制流（以 `contacts.sqlite3` 檔案標頭為單一加密真相來源） |
| 關聯 UI、DBA、測試與既有 ADR | UI: 不適用<br>DBA: [DBA-ISSUE-0051-v0.1.md](../DB/DBA-ISSUE-0051-v0.1.md)<br>測試: [TP-ISSUE-0051-v0.1.md](../TestPlans/TP-ISSUE-0051-v0.1.md) |
| 程式識別名／獨立程式資料夾 | `MajiOCR.Windows` (`MajiOCR.Windows/`) |
| 固定發布資料夾／發布方式 | `publish/MajiOCR.Windows/MajiOCR.Windows.exe`（Single-File 自包含免安裝執行檔） |
| 當前正式版／本次版本／發布類型 | 尚未發布 `v1.0.0` 正式版；本次為測試版 `0.1.0-beta.31` (`0.1.0.31`) |
| 文件狀態 | 已採用（人類使用者已確認選擇方向 1 與 2） |

---

## 2. 目標、範圍與限制

- **需要解決的問題及驗收條件**：
  1. 過去 `contacts.sqlite3`、`card-images/`、`google_oauth_config.json`、`UpdateSettings.json` 均位於主程式同一目錄，唯獨 `settings.json` 與舊版 `custom-fields.json` 存放於 `%LocalAppData%\MajiOCR.Windows\`，造成「半可攜式」狀態分裂。
  2. 當使用者將 `MajiOCR.Windows.exe` 複製／移動至其他無資料庫的資料夾啟動時，因讀取到 `%LocalAppData%\MajiOCR.Windows\settings.json` 的 `IsPasswordProtected = true` 而誤彈出密碼解鎖視窗，並在輸入密碼後將新建的空白資料庫加密、同時匯入 `%LocalAppData%\MajiOCR.Windows\custom-fields.json`，導致使用者誤以為連回原資料庫。
- **本次範圍／不包含項目**：
  - 包含：`AppSettingsService` 可攜路徑與條件式遷移、`CustomFieldCatalogService` 與 `StorageService.TryMigrateLegacyJson` 可攜路徑、`StorageService.ResolveStartupSecurityState` 啟動加密狀態單一真相判定、`MainPage` 與 `SecurityDialogs` 整合。
  - 不包含：SQLite 資料表結構變更。

---

## 3. 現況與提案架構

```mermaid
flowchart TD
    Start["MajiOCR.Windows.exe 啟動<br/>取得主程式目錄 StorageRoot"] --> CheckSettings["AppSettingsService<br/>讀取 &lt;StorageRoot&gt;/settings.json"]
    CheckSettings --> HasLocalSettings{"同目錄已有<br/>settings.json?"}
    HasLocalSettings -->|是| ResolveSec["StorageService.ResolveStartupSecurityState"]
    HasLocalSettings -->|否| HasLocalDb{"同目錄已有<br/>contacts.sqlite3?"}
    HasLocalDb -->|是 (原工作目錄升級)| MigrateLegacy["若 %LocalAppData% 有舊 settings.json<br/>平滑遷移至 &lt;StorageRoot&gt;/settings.json"]
    HasLocalDb -->|否 (移至新空資料夾)| CleanDefault["使用全新預設設定<br/>(不讀取 %LocalAppData%)"]
    MigrateLegacy --> ResolveSec
    CleanDefault --> ResolveSec

    ResolveSec --> IsDbEnc{"contacts.sqlite3 存在且<br/>SQLCipher 加密標頭?"}
    IsDbEnc -->|是 (RequiresUnlock=true)| PromptPwd["彈出主密碼解鎖視窗<br/>驗證通過後開啟加密 DB"]
    IsDbEnc -->|否 (RequiresUnlock=false)| SyncPlain["若 settings.IsPasswordProtected=true<br/>自動校正重設為 false<br/>直接開啟或建立未加密 DB"]
```

| 模組／介面 | 職責與契約 | 相容性與錯誤行為 | 本次變更 |
| --- | --- | --- | --- |
| `AppSettingsService` | 管理 `<StorageRoot>/settings.json` 讀寫 | 預設路徑改為 `Path.Combine(StoragePathHelper.GetDefaultStorageRoot(), "settings.json")`；僅當同目錄下已存在 `contacts.sqlite3` 且尚無 `settings.json` 時，才嘗試從 `%LocalAppData%\MajiOCR.Windows\settings.json` 單次遷移 | 修改建構子與新增 `TryMigrateLegacyAppDataSettingsIfCoLocatedWithDb` |
| `CustomFieldCatalogService` / `StorageService.CustomFields` | 管理自訂欄位定義與舊版 `custom-fields.json` 匯入 | 預設路徑與 `TryMigrateLegacyJson` 均改為檢查 `<StorageRoot>/custom-fields.json`，不再讀取 `%LocalAppData%\MajiOCR.Windows\custom-fields.json` | 修改 `CustomFieldCatalogService.cs` 與 `StorageService.CustomFields.cs` |
| `StorageService.ResolveStartupSecurityState` | 判定啟動時是否需要彈出密碼解鎖視窗並同步 `settings.json` | 僅當 `StorageService.IsDatabaseEncrypted(dbPath)` 為 `true` 時回傳 `RequiresUnlock = true`；若為 `false` 且 `settings.IsPasswordProtected` 為 `true`，自動呼叫 `UpdateSecuritySettings(false, null, null)` | 新增於 `StorageService.cs` |
| `MainPage` / `SecurityDialogs` | 啟動儲存空間初始化與安全設定對話框 | `MainPage` 支援 `--gui-test-root` 下隔離 `settings.json`，並以 `ResolveStartupSecurityState` 決定是否詢問密碼；`SecurityDialogs` 以 `storageService.IsEncrypted` 為加密狀態真相來源 | 修改 `MainPage.xaml.cs` 與 `SecurityDialogs.cs` |

---

## 4. 非功能需求與驗證

| ID | 需求／目標與來源 | 方法／環境／通過標準 | 責任角色 | 關聯測試項目 |
| --- | --- | --- | --- | --- |
| NFR-01 | 跨資料夾 100% 可攜隔離 | 在無 `contacts.sqlite3` 的新資料夾初始化 `AppSettingsService` 與 `StorageService`，驗證完全不受 `%LocalAppData%` 影響 | 軟體工程師 / 測試工程師 | `TP-ISSUE-0051` TC-01, TC-02 |
| NFR-02 | 加密狀態單一真相來源 (SSOT) | 當 `settings.json` 設為 `IsPasswordProtected = true` 但無 DB 或 DB 未加密時，`ResolveStartupSecurityState` 回傳 `RequiresUnlock = false` 並自動修正 `settings.json` | 軟體工程師 / 測試工程師 | `TP-ISSUE-0051` TC-03 |
| NFR-03 | 既有工作目錄無縫升級 | 當目錄下已有 `contacts.sqlite3` 但無 `settings.json` 時，可從舊設定路徑平滑遷移至同目錄 `settings.json`，且已加密 DB 仍強制要求密碼解鎖 | 軟體工程師 / 測試工程師 | `TP-ISSUE-0051` TC-04, TC-05 |

---

## 5. 方案與取捨

| 面向 | A：設定檔收斂至主程式同目錄 + 僅在同目錄已有 DB 時遷移舊設定 + 以 DB 標頭為解鎖唯一真相來源 | B：維持 `settings.json` 在 `%LocalAppData%`，僅修改密碼判斷條件 |
| --- | --- | --- |
| 需求符合度與限制 | 完全符合使用者選擇之「1（完全可攜化）+ 2（新資料庫不沿用舊密碼詢問）」，所有資料與設定皆跟隨主程式目錄 | 不同資料夾的主程式仍會互相覆蓋 OCR 語系、Vercel 設定與密碼雜湊，不具備可攜隔離性 |
| 遷移與回復風險 | 原工作目錄（已有 `contacts.sqlite3`）自動平滑遷移舊 `settings.json`；新空目錄則完全乾淨隔離 | 無需遷移檔案，但跨目錄狀態污染問題仍在 |

- **推薦方案及依據**：採用 **方案 A**。徹底解決設定檔與資料庫分離造成的跨目錄污染與誤提示密碼問題，同時保障原工作目錄無縫升級。

---

## 6. 風險、遷移與回復

| 風險／故障情境 | 影響 | 緩解與驗證方式 | 責任角色 | 剩餘風險與決策狀態 |
| --- | --- | --- | --- | --- |
| 使用者升級主程式後，原本在 `%LocalAppData%\MajiOCR.Windows\settings.json` 的設定（如 Vercel Token、OCR 語系、密碼雜湊）未出現在主程式目錄 | 若不遷移，原工作目錄升級後需重新設定偏好 | 當 `<StorageRoot>/settings.json` 不存在、但同目錄 `<StorageRoot>/contacts.sqlite3` 已存在時，自動從 `%LocalAppData%\MajiOCR.Windows\settings.json` 複製遷移至同目錄；同時發布時也同步將現有設定遷移至 `publish/MajiOCR.Windows/settings.json` | 軟體工程師 | 已緩解 |

---

## 7. 評估結論與決策紀錄

- **SA 結論**：**可採用**。
- **人類決策與核准**：人類使用者於 2026-10-18 明確指示採行「1和2」（完全可攜化 + 新資料庫不沿用舊密碼詢問）。

---

## 8. 實作後架構符合性

- 受評最終版本：`0.1.0-beta.31` (`0.1.0.31`，單一執行檔 `publish/MajiOCR.Windows/MajiOCR.Windows.exe`，SHA-256: `3D3C65560198FE36AF95A3F6C7EA0EE825DB0692A90007031BD1C238276AC218`)。

| 架構決策／條件 | 實作或測試證據 | 結果 | 偏差與處理 |
| --- | --- | --- | --- |
| `settings.json` 與 `custom-fields.json` 預設路徑跟隨主程式所在目錄 | `AppSettingsService.cs`、`CustomFieldCatalogService.cs`、`StorageService.CustomFields.cs`；測試證據 `TestReports/evidence/ISSUE-0051/ISSUE-0051_TEST_REPORT.json` (`TC-0051-01`, `TC-0051-02`) | 符合 | 無偏差 |
| 啟動密碼解鎖以同目錄 `contacts.sqlite3` 實際加密標頭為唯一真相來源，無 DB 或未加密 DB 自動重設孤立設定 | `StorageService.ResolveStartupSecurityState`、`MainPage.xaml.cs`、`SecurityDialogs.cs`；測試證據 `TC-0051-02`, `TC-0051-03`, `TC-0051-04` | 符合 | 無偏差 |
| 原工作目錄（同目錄已存在 `contacts.sqlite3`）平滑遷移舊版設定 | `AppSettingsService.TryMigrateLegacySettingsIfCoLocatedWithDatabase`；測試證據 `TC-0051-05` | 符合 | 無偏差 |

- **SA 檢查結論**：**通過（符合已採用之可攜式架構決策與單檔免安裝發布契約）**。
