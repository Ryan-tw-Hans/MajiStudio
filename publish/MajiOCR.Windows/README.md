# MajiOCR.Windows 0.1.0-beta.30 (強化本機憑證 Windows DPAPI 加密保護與清理公開儲存庫敏感資訊)

目前為本機發布版，更新日期為 2026-10-17（Asia/Taipei）。
直接執行本資料夾的 `MajiOCR.Windows.exe`。維持單一免安裝、自含執行檔（Self-Contained Single-File Executable）及離線 OCR 模型，並內嵌完整 `MajiOCR.Web` (`0.2.0-beta.1`) 靜態 PWA 資產包。

### 本版更新重點 (`ISSUE-0050`)
1. **本機執行時憑證 Windows DPAPI (`CryptProtectData` CurrentUser) 加密保護 (`CredentialProtectionHelper`)**：
   - `GoogleAuthService` (`google_oauth_config.json`) 的 `ClientSecret` 與 `AppSettingsService` (`%LocalAppData%\MajiOCR.Windows\settings.json`) 的 `VercelAccessToken` 全面改以 Windows DPAPI (`dpapi:<Base64>`) 加密落盤，磁碟不再保存明文秘密，防止整個可攜式資料夾複製或分享時外洩憑證。
   - 支援既有明文設定檔（含 UTF-8 BOM 相容）首次載入時自動平滑升級為 DPAPI 密文；跨電腦或損毀密文無法解密時安全回退為空值並提示重新設定，絕不引發崩潰。
2. **儲存庫公開敏感資訊與環境資訊全面清理**：
   - 移除 `Issue0035AutomatedTestSuite.cs` 寫死的個人工作階段路徑與真實姓名，並將測試套件中的企業信箱網域統一替換為 RFC 2606 保留測試網域 `example.com`。
   - `DefaultUpdateSettings.json` 與 `CHANGELOG.txt` 移除舊版個人 Google Drive 資源識別碼，預設更新來源統一指向官方 GitHub Releases API (`https://api.github.com/repos/Ryan-tw-Hans/MajiStudio/releases/latest`)。
   - 14 份正式文件（`Architecture/`、`DesignPlans/`、`Requirements/`、`TestPlans/`、`TestReports/`）中的本機絕對路徑連結統一改為儲存庫相對路徑。
   - 將內部 `IssueLog/IssueLog.sqlite3` 移出 Git 公開追蹤（本機完整保留），並將本機 Git 提交信箱更新為 GitHub noreply 信箱。

---
- **FileVersion**：0.1.0.30 (`0.1.0-beta.30`)
- **SHA-256**：`7ED97B913F5D5952FF6AA26399AEFEA90296500860B99ECB5D792CD646D77D6A`
- **檔案大小**：299,877,923 位元組 (~286.0 MB)

本機單檔發布驗證與自動化測試套件（`ISSUE-0050` 5/5 PASS、`ISSUE-0046` 3/3 PASS、`ISSUE-0040` 4/4 PASS、`ISSUE-0036` 7/7 PASS、`ISSUE-0035` 5/5 PASS、`ISSUE-0022` 8/8 PASS）均已全數通過。
