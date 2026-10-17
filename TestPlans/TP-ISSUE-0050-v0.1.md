# 測試計畫書：ISSUE-0050 清理 GitHub 公開儲存庫敏感資訊與本機憑證 Windows DPAPI 加密保護

- **文件識別碼**：`TP-ISSUE-0050-v0.1`
- **關聯 Issue**：`ISSUE-0050`
- **關聯需求規格**：[RS-ISSUE-0050-v0.1.md](../Requirements/RS-ISSUE-0050-v0.1.md)
- **關聯架構評估**：[AR-ISSUE-0050-v0.1.md](../Architecture/AR-ISSUE-0050-v0.1.md)
- **測試版本**：`0.1.0-beta.30` (`0.1.0.30`)
- **負責角色**：測試工程師 Agent

---

## 1. 測試風險與範圍評估

1. **測試目標**：
   - 驗證 `GoogleAuthService` 與 `AppSettingsService` 的敏感憑證（`ClientSecret`、`VercelAccessToken`）寫入磁碟時已透過 Windows DPAPI (`dpapi:`) 加密，不再以明文落盤，且讀取時能正確解密還原。
   - 驗證舊版明文設定檔在首次讀取時能正常回傳明文並自動平滑升級為 `dpapi:` 密文格式，且損毀或無法解密的密文能安全回退不崩潰。
   - 驗證 `Issue0035AutomatedTestSuite.cs`、`.gitignore`、`DefaultUpdateSettings.json`、`CHANGELOG.txt` 及 14 份正式文件已完全清除個人絕對路徑、真實姓名與舊版 Google Drive 資源 ID。
   - 驗證修改後的既有測試套件（`Issue0011`、`Issue0015`、`Issue0016`、`Issue0022`、`Issue0035`、`Issue0036`、`Issue0040`、`Issue0046`）全數回歸通過。
2. **受影響模組**：
   - `MajiOCR.Windows/Services/CredentialProtectionHelper.cs`（新增）
   - `MajiOCR.Windows/Services/GoogleAuthService.cs`
   - `MajiOCR.Windows/Services/AppSettingsService.cs`
   - `MajiOCR.Windows/Services/Update/UpdateService.cs` 與 `DefaultUpdateSettings.json`
   - `MajiOCR.Windows/Verification/Issue0011AutomatedTestSuite.cs`、`Issue0015`、`Issue0016`、`Issue0022`、`Issue0035`、`Issue0040`、`Issue0046`、`Issue0050AutomatedTestSuite.cs`

---

## 2. 測試項目定義 (`--test-issue-0050`)

| 測試代號 | 對應驗收條件 | 測試名稱與步驟 | 預期結果與通過標準 |
| --- | --- | --- | --- |
| **TC-0050-01** | AC-05 | **GoogleAuthService OAuth ClientSecret DPAPI 加密儲存與讀取驗證**：呼叫 `SaveOAuthConfigAsync` 寫入測試 ClientId 與 ClientSecret，檢查磁碟 JSON 原文與 `LoadOAuthConfigAsync` 回傳值 | 磁碟 JSON 中的 `ClientSecret` 必須以 `dpapi:` 開頭且不包含原始明文；`LoadOAuthConfigAsync` 回傳值與原始明文完全一致 |
| **TC-0050-02** | AC-06 | **AppSettingsService VercelAccessToken DPAPI 加密儲存與讀取驗證**：呼叫 `UpdateVercelSettings` 儲存測試 Token，檢查磁碟 `settings.json` 原文與 `LoadSettings()` 回傳值 | 磁碟 JSON 中的 `vercelAccessToken` 必須以 `dpapi:` 開頭且不包含原始明文；`LoadSettings().VercelAccessToken` 回傳值與原始明文完全一致 |
| **TC-0050-03** | AC-05, AC-06 | **舊版明文設定檔平滑自動遷移與無效密文安全回退驗證**：手動寫入含明文 `ClientSecret` 與明文 `vercelAccessToken` 的舊版 JSON，呼叫讀取方法後再次檢查磁碟檔案；另寫入損毀的 `dpapi:INVALID` 驗證容錯 | 首次讀取正確回傳明文，且磁碟檔案已自動升級為 `dpapi:` 密文；損毀密文不拋出例外並安全回退為空值 |
| **TC-0050-04** | AC-01, AC-02 | **程式碼與設定檔敏感資訊清除驗證**：以合成測試資料驗證敏感特徵偵測器，並掃描 `Issue0035AutomatedTestSuite.cs`、`DefaultUpdateSettings.json`、`CHANGELOG.txt` 及執行 `UpdateService.LoadSettings()` | 檔案中不含本機個人使用者目錄路徑、不含特定真實姓名、不含舊版雲端硬碟資源連結；預設更新網址為 GitHub Releases API |
| **TC-0050-05** | AC-03, AC-04, AC-08 | **正式文件相對路徑、`.gitignore` 規則與版本號 `0.1.0.30` 一致性驗證**：檢查 `.gitignore` 不含 `!IssueLog/IssueLog.sqlite3`、追蹤 Markdown 文件不含本機磁碟絕對連結，且 `VERSION.txt` / `CHANGELOG.txt` / `MajiOCR.Windows.csproj` 版本同步為 `0.1.0.30` (`0.1.0-beta.30`) | 所有檢查條件 100% 符合 |

---

## 3. 回歸測試與發布驗證

1. 執行 `--test-issue-0050` 並產出 `TestReports/evidence/ISSUE-0050/ISSUE-0050_TEST_REPORT.json`。
2. 執行受影響之回歸測試：`--test-issue-0035`、`--test-issue-0036`、`--test-issue-0040`、`--test-issue-0046`、`--test-issue-0015`、`--test-issue-0016`、`--test-issue-0022`。
3. 執行單一執行檔發布建置至 `publish/MajiOCR.Windows/MajiOCR.Windows.exe`，並以發布後之單一 `.exe` 直接執行 `--test-issue-0050` 驗證獨立執行能力與版本雜湊。
