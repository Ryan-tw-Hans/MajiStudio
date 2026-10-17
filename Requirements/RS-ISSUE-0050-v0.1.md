# 需求規格書：清理 GitHub 公開儲存庫之個人與內部環境敏感資訊並強化本機憑證 DPAPI 加密保護

- **文件識別碼**：`RS-ISSUE-0050-v0.1`
- **關聯 Issue**：`ISSUE-0050`
- **需求版本**：`v0.1`
- **需求種類**：變更／資安與隱私強化
- **優先級**：P0（最高）
- **提出來源**：人類使用者（依據 `MajiStudio-公開敏感資訊檢查報告-2026-10-17.md`）
- **受理窗口**：PM Agent
- **負責角色**：PM / SA / 軟體工程師 / 測試工程師

---

## 1. 問題背景與目標

依據外部靜態資安檢查報告（`MajiStudio-公開敏感資訊檢查報告-2026-10-17.md`），本專案公開於 GitHub (`Ryan-tw-Hans/MajiStudio`) 的追蹤檔案中存在以下個人與內部環境資訊暴露風險，以及本機執行時憑證以明文儲存之安全強化需求：

1. **測試程式碼含疑似真實姓名與本機個人路徑**：`MajiOCR.Windows/Verification/Issue0035AutomatedTestSuite.cs` 寫死本機個人工作階段路徑與特定姓名；部分測試套件（`Issue0011`、`Issue0015`、`Issue0016`、`Issue0022`）使用非保留之外部商業網域作為合成測試信箱。
2. **內部 `IssueLog/IssueLog.sqlite3` 公開追蹤**：`.gitignore` 以 `!IssueLog/IssueLog.sqlite3` 允許內部資料庫被追蹤，導致歷史事件中的本機路徑、帳號與發布設定隨資料庫公開。
3. **個人 Google Drive 資源識別碼殘留**：`MajiOCR.Windows/DefaultUpdateSettings.json` 與 `CHANGELOG.txt` 仍包含舊版 Google Drive 檔案與資料夾資源 ID。
4. **正式文件含本機絕對路徑連結**：14 份規格、架構、設計與測試文件含有本機磁碟絕對路徑連結。
5. **執行時憑證以明文設定檔儲存**：`GoogleAuthService.cs`（`google_oauth_config.json` 的 `ClientSecret`）與 `AppSettingsService.cs`（`settings.json` 的 `VercelAccessToken`）直接以明文 JSON 寫入磁碟。
6. **Git 提交者個人信箱**：本機 Git `user.email` 設定為個人電子郵件信箱。

### 1.1 人類使用者已確認決策（2026-10-17）

- **決策 1（IssueLog 公開政策）**：將 `IssueLog/IssueLog.sqlite3` 移出 Git 追蹤（修改 `.gitignore` 移除 `!IssueLog/IssueLog.sqlite3` 並執行 `git rm --cached IssueLog/IssueLog.sqlite3`），本機完整保留 `IssueLog.sqlite3`，對外僅公開 `CHANGELOG.txt` 與規格文件。
- **決策 2（執行時憑證加密保護）**：本次一併實作 Windows DPAPI (`CurrentUser` 範圍) 加密保護，並支援既有明文設定檔平滑自動遷移。
- **決策 3（Git 歷史與提交者信箱）**：重寫 Git 歷史提交以徹底移除歷史樹中的敏感檔案與識別碼，並將 Git 提交者信箱統一改為 GitHub noreply 信箱（`Ryan-tw-Hans@users.noreply.github.com`）。

---

## 2. 功能與非功能需求

### 2.1 儲存庫敏感資訊與路徑清理
1. **`Issue0035AutomatedTestSuite.cs` 個資與本機路徑移除**：
   - 移除寫死的本機個人絕對路徑，改為接受可選外部路徑或使用記憶體合成測試圖檔，且不暴露任何個人目錄名稱。
   - 移除註解與輸出訊息中的特定姓名，改為明確虛構測試姓名「王小明」。
2. **測試套件信箱與網址去識別化**：
   - 將所有測試套件中的非保留外部商業網域統一替換為 RFC 2606 保留測試網域 `example.com`、`example.org` 或 `example.invalid`。
3. **Google Drive 資源 ID 清理**：
   - `MajiOCR.Windows/DefaultUpdateSettings.json` 的 `ReleaseManifestUrl` 改為官方 GitHub Releases API 網址 `https://api.github.com/repos/Ryan-tw-Hans/MajiStudio/releases/latest`；同時在 `UpdateService.cs` 確保載入預設設定檔時亦套用舊版 Google Drive 網址過濾防護。
   - `CHANGELOG.txt` 第 53 行的具體 Google Drive 資料夾 ID 移除。
4. **正式文件絕對路徑轉相對路徑**：
   - 將 `Architecture/`、`DesignPlans/`、`Requirements/`、`TestPlans/`、`TestReports/` 共 14 份已追蹤 Markdown 文件中的本機絕對路徑連結全部替換為相對於儲存庫或該文件的相對路徑。
5. **`IssueLog/IssueLog.sqlite3` 停止 Git 追蹤**：
   - 從 `.gitignore` 移除 `!IssueLog/IssueLog.sqlite3` 例外規則，使 `*.sqlite3` 全面忽略資料庫檔案。
   - 執行 `git rm --cached IssueLog/IssueLog.sqlite3`，保留本機工作樹中的 `IssueLog/IssueLog.sqlite3` 檔案不刪除。
6. **Git 提交者信箱隱私保護**：
   - 將本機儲存庫 `git config user.email` 設定為 `Ryan-tw-Hans@users.noreply.github.com`。

### 2.2 本機執行時憑證 Windows DPAPI 加密保護
1. **新增 `CredentialProtectionHelper`（Windows DPAPI 封裝）**：
   - 使用 Windows 原生 `crypt32.dll` (`CryptProtectData` / `CryptUnprotectData`，搭配 `CRYPTPROTECT_UI_FORBIDDEN = 0x1`，CurrentUser 範圍) 實作字串加密與解密。
   - 加密後格式採用明確前綴標記 `dpapi:<Base64CipherText>`。
   - 解密時若輸入帶 `dpapi:` 前綴則呼叫 `CryptUnprotectData` 解密；若輸入不帶 `dpapi:` 前綴（既有明文設定），直接回傳明文以支援向後相容與平滑遷移。
2. **`GoogleAuthService` (`google_oauth_config.json`) 保護**：
   - `SaveOAuthConfigAsync` 儲存時，將非空的 `ClientSecret` 以 `CredentialProtectionHelper.Protect` 加密為 `dpapi:...` 格式寫入 JSON。
   - `LoadOAuthConfigAsync` 讀取時，自動透過 `CredentialProtectionHelper.Unprotect` 解密還原 `ClientSecret`；若偵測到檔案內仍為舊版明文 `ClientSecret`，讀取成功後自動升級回寫為 `dpapi:` 密文格式。
3. **`AppSettingsService` (`settings.json`) 保護**：
   - `SaveSettings` 序列化寫入磁碟前，將非空的 `VercelAccessToken` 以 `CredentialProtectionHelper.Protect` 轉換為 `dpapi:...` 密文寫入 JSON（不改變呼叫端記憶體中的明文屬性值）。
   - `LoadSettings` 反序列化讀取後，若 `VercelAccessToken` 為 `dpapi:...` 密文則自動解密還原為明文供程式使用；若偵測到磁碟檔案為舊版明文 `VercelAccessToken`，自動觸發平滑加密回寫，確保磁碟不殘留明文 Token。

---

## 3. 角色評估與影響判定

| 角色 | 判定與建議 | 關聯文件 |
| --- | --- | --- |
| **SA 系統架構師** | **有架構影響（可採用）**：變更本機憑證儲存安全邊界，導入 Windows DPAPI (`CryptProtectData` CurrentUser) 並維持單一執行檔免額外 NuGet 相依與舊版明文平滑遷移。 | [AR-ISSUE-0050-v0.1.md](../Architecture/AR-ISSUE-0050-v0.1.md) |
| **設計工程師 (UI/UX)** | **不適用**：本次變更為背景設定檔 DPAPI 加解密與儲存庫清理，無任何 UI 視覺或互動變更。 | 不適用 |
| **DBA** | **不適用**：不涉及 `contacts.sqlite3` Schema 或資料變更；`IssueLog/IssueLog.sqlite3` 僅移出 Git 追蹤並保留於本機。 | 不適用 |
| **系統整合工程師** | **不適用**：不改變對 Google OAuth / Drive / Vercel / GitHub API 的外部通訊契約。 | 不適用 |
| **維運工程師** | **不適用**：本專案為單機免安裝桌面應用與靜態 PWA，無伺服器端維運變更；DPAPI 解密失敗時具備安全回退（視為未設定憑證並提示重新輸入，不崩潰）。 | 不適用 |
| **測試工程師** | **適用**：新增 `Issue0050AutomatedTestSuite` 驗證 DPAPI 加密持久化、舊版明文平滑遷移、儲存庫無個人路徑/Drive ID/特定姓名殘留，並回歸執行既有測試套件。 | [TP-ISSUE-0050-v0.1.md](../TestPlans/TP-ISSUE-0050-v0.1.md) |

---

## 4. 驗收條件

- **AC-01**：`Issue0035AutomatedTestSuite.cs` 及所有追蹤測試程式碼不含本機個人目錄路徑、不含特定真實姓名，且測試信箱網域已改為 `example.com`、`example.org` 或 `example.invalid`。
- **AC-02**：`DefaultUpdateSettings.json` 與 `CHANGELOG.txt` 不含舊版個人 Google Drive 資源 ID。
- **AC-03**：所有已追蹤之 Markdown 文件不含本機磁碟絕對路徑連結。
- **AC-04**：`.gitignore` 已移除 `!IssueLog/IssueLog.sqlite3`，`IssueLog/IssueLog.sqlite3` 已從 Git 索引移除（`git ls-files IssueLog/IssueLog.sqlite3` 為空），且本機檔案完整保留並通過 `issue_log.py validate`。
- **AC-05**：`GoogleAuthService` 儲存 `google_oauth_config.json` 時，磁碟檔案中的 `ClientSecret` 以 `dpapi:` 密文格式保存，不再包含明文 Secret；讀取時可正確還原，且舊版明文設定檔讀取後自動升級為密文。
- **AC-06**：`AppSettingsService` 儲存 `settings.json` 時，磁碟檔案中的 `VercelAccessToken` 以 `dpapi:` 密文格式保存，不再包含明文 Token；讀取時可正確還原，且舊版明文設定檔讀取後自動升級為密文。
- **AC-07**：新增 `--test-issue-0050` 自動化測試套件且全數通過，受影響之既有自動化測試（`Issue0011`、`Issue0015`、`Issue0016`、`Issue0022`、`Issue0035`、`Issue0036`、`Issue0040`、`Issue0046`）回歸通過。
- **AC-08**：程式版本推進至 `0.1.0-beta.30`（`0.1.0.30`），完成單一執行檔發布建置至 `publish/MajiOCR.Windows/MajiOCR.Windows.exe` 並更新 `publish/MajiOCR.Windows/README.md`。

---

## 5. 獨立程式資料夾、固定發布資料夾與不包含項目

- **獨立程式資料夾**：`MajiOCR.Windows/`（本次修改範圍）、`MajiOCR.Web/`、`MajiOCR/`
- **固定發布資料夾**：`publish/MajiOCR.Windows/MajiOCR.Windows.exe`
- **本次版本策略**：測試版 `0.1.0-beta.30`（FileVersion `0.1.0.30`，尚未發布 `v1.0.0` 正式版）
- **不在本次範圍內的項目**：
  - 不改寫既有 17 筆 Git 歷史提交與既有 GitHub Release 標籤（依使用者決策 3）。
  - 不變更 UI 畫面或 SQLite 資料庫 Schema。
