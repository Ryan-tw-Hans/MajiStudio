# 測試報告：ISSUE-0051 Windows 主程式設定檔完全可攜化與新資料庫免沿用舊密碼詢問

- **文件識別碼**：`TR-ISSUE-0051-v0.1`
- **關聯 Issue**：`ISSUE-0051`
- **關聯需求規格**：[RS-ISSUE-0051-v0.1.md](../Requirements/RS-ISSUE-0051-v0.1.md)
- **關聯架構評估**：[AR-ISSUE-0051-v0.1.md](../Architecture/AR-ISSUE-0051-v0.1.md)
- **關聯資料庫評估**：[DBA-ISSUE-0051-v0.1.md](../DB/DBA-ISSUE-0051-v0.1.md)
- **關聯測試計畫**：[TP-ISSUE-0051-v0.1.md](../TestPlans/TP-ISSUE-0051-v0.1.md)
- **受測版本**：`0.1.0-beta.31` (`0.1.0.31`)
- **測試時間**：`2026-10-18T08:53:04+08:00` (`Asia/Taipei`)
- **測試環境**：Microsoft Windows NT 10.0.26200.0 (X64)
- **負責角色**：測試工程師 Agent

---

## 1. 測試總結

- **總測試數（ISSUE-0051 專項）**：5 項（5 通過 / 0 失敗 / 0 未執行）
- **回歸測試套件**：4 組套件共 28 項測試全數通過（`ISSUE-0036` 7/7、`ISSUE-0038` 11/11、`ISSUE-0043` 5/5、`ISSUE-0050` 5/5）
- **單檔發布驗證**：`publish/MajiOCR.Windows/MajiOCR.Windows.exe`（FileVersion `0.1.0.31`，SHA-256 `3D3C65560198FE36AF95A3F6C7EA0EE825DB0692A90007031BD1C238276AC218`）直接執行 `--test-issue-0051`、`--test-issue-0036`、`--test-issue-0038`、`--test-issue-0043` 與 `--test-issue-0050` 全數通過。
- **證據檔案**：[ISSUE-0051_TEST_REPORT.json](evidence/ISSUE-0051/ISSUE-0051_TEST_REPORT.json)

---

## 2. 專項測試結果明細 (`--test-issue-0051`)

| 測試代號 | 對應驗收條件 | 測試描述 | 結果 | 耗時 | 驗證細節與證據 |
| --- | --- | --- | --- | --- | --- |
| **TC-0051-01** | AC-01 | 驗證 `AppSettingsService` 與 `CustomFieldCatalogService` 預設路徑跟隨主程式所在目錄而非 `AppData` | **PASS** | 3 ms | `DefaultRoot=MajiOCR.Windows, SettingsCoLocated=true, CatalogCoLocated=true` |
| **TC-0051-02** | AC-02 | 驗證主程式移至無 `contacts.sqlite3` 的新資料夾時，不遷移舊設定、不詢問舊密碼且不匯入跨目錄自訂欄位 | **PASS** | 651 ms | `LegacySettingsIgnored=true, RequiresUnlock=false, NewDbUnencrypted=true, CustomFieldsCount=0` |
| **TC-0051-03** | AC-03 | 驗證 `settings.json` 殘留 `IsPasswordProtected=true` 但無資料庫或資料庫未加密時，免密碼啟動並自動校正設定 | **PASS** | 266 ms | `ScenarioA_NoDbReset=true, ScenarioB_PlainDbReset=true, RequiresUnlock=false` |
| **TC-0051-04** | AC-04 | 驗證同目錄存在已加密 `contacts.sqlite3` 時，即使無 `settings.json` 仍強制要求主密碼解鎖 | **PASS** | 5715 ms | `EncryptedDbRequiresUnlock=true, WrongPassRejected=true, RightPassAccepted=true` |
| **TC-0051-05** | AC-04 | 驗證原工作目錄已有 `contacts.sqlite3` 時平滑遷移舊 `settings.json`，且支援同目錄 `custom-fields.json` 匯入 | **PASS** | 72 ms | `CoLocatedMigrationSuccess=true, CoLocatedCustomFieldsImported=true` |

---

## 3. 回歸測試結果摘要

| 回歸套件命令 | 涵蓋範圍 | 結果 |
| --- | --- | --- |
| `--test-issue-0036` | `AppSettingsService` 語系持久化、UTF-8 嚴格校驗、毀損回退與多執行緒併發安全 | **PASS (7/7)** |
| `--test-issue-0038` | `PasswordSecurityService`、SQLCipher AES-256 資料庫加密、修改密碼與解密還原 | **PASS (11/11)** |
| `--test-issue-0043` | `custom_field_definitions` SQLite 儲存、CRUD 與同目錄 JSON 無縫匯入 | **PASS (5/5)** |
| `--test-issue-0050` | Windows DPAPI 憑證保護、`.gitignore` 排除規則與全專案檔案無本機敏感路徑檢查 | **PASS (5/5)** |
