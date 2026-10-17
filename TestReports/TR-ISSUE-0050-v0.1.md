# 測試報告：ISSUE-0050 清理 GitHub 公開儲存庫敏感資訊與本機憑證 Windows DPAPI 加密保護

- **文件識別碼**：`TR-ISSUE-0050-v0.1`
- **關聯 Issue**：`ISSUE-0050`
- **關聯需求規格**：[RS-ISSUE-0050-v0.1.md](../Requirements/RS-ISSUE-0050-v0.1.md)
- **關聯架構評估**：[AR-ISSUE-0050-v0.1.md](../Architecture/AR-ISSUE-0050-v0.1.md)
- **關聯測試計畫**：[TP-ISSUE-0050-v0.1.md](../TestPlans/TP-ISSUE-0050-v0.1.md)
- **受測版本**：`0.1.0-beta.30` (`0.1.0.30`)
- **測試時間**：`2026-10-17T16:47:27+08:00` (`Asia/Taipei`)
- **測試環境**：Microsoft Windows NT 10.0.26200.0 (X64)
- **負責角色**：測試工程師 Agent

---

## 1. 測試總結

- **總測試數（ISSUE-0050 專項）**：5 項（5 通過 / 0 失敗 / 0 未執行）
- **回歸測試套件**：6 組套件共 27 項測試全數通過（`ISSUE-0046` 3/3、`ISSUE-0040` 4/4、`ISSUE-0036` 7/7、`ISSUE-0035` 5/5、`ISSUE-0022` 8/8）
- **單檔發布驗證**：`publish/MajiOCR.Windows/MajiOCR.Windows.exe`（FileVersion `0.1.0.30`，SHA-256 `7ED97B913F5D5952FF6AA26399AEFEA90296500860B99ECB5D792CD646D77D6A`）直接執行 `--test-issue-0050` 與 `--test-issue-0036` 全數通過。
- **證據檔案**：[ISSUE-0050_TEST_REPORT.json](evidence/ISSUE-0050/ISSUE-0050_TEST_REPORT.json)

---

## 2. 專項測試結果明細 (`--test-issue-0050`)

| 測試代號 | 對應驗收條件 | 測試描述 | 結果 | 耗時 | 驗證細節與證據 |
| --- | --- | --- | --- | --- | --- |
| **TC-0050-01** | AC-05 | 驗證 `GoogleAuthService` 儲存 `google_oauth_config.json` 時以 Windows DPAPI 加密 `ClientSecret` 且不含明文 | **PASS** | 158 ms | `DiskCipherPrefix=dpapi:AQAAANCM..., PlaintextAbsentOnDisk=true, RoundtripMatch=true` |
| **TC-0050-02** | AC-06 | 驗證 `AppSettingsService` 儲存 `settings.json` 時以 Windows DPAPI 加密 `VercelAccessToken` 且不含明文 | **PASS** | 106 ms | `DiskCipherPrefix=dpapi:AQAAANCM..., PlaintextAbsentOnDisk=true, RoundtripMatch=true` |
| **TC-0050-03** | AC-05, AC-06 | 驗證舊版明文設定檔讀取後自動平滑升級為 DPAPI 密文，且損毀密文安全回退不崩潰 | **PASS** | 64 ms | `LegacyAutoUpgraded=true, CorruptedSafeFallbackToNull=true` |
| **TC-0050-04** | AC-01, AC-02 | 驗證 `Issue0035`、`DefaultUpdateSettings.json` 與 `CHANGELOG.txt` 已移除個人路徑、姓名與舊版 Drive ID（零敏感常值） | **PASS** | 5 ms | `Issue0035Clean=true, DefaultUpdateSettingsClean=true, ChangelogClean=true, SyntheticDetectorVerified=true` |
| **TC-0050-05** | AC-03, AC-04, AC-08 | 驗證 `.gitignore` 排除 `IssueLog.sqlite3`、正式文件無本機絕對路徑連結且版本同步為 `0.1.0.30` | **PASS** | 13 ms | `GitignoreClean=true, DocRelativeLinksVerified=true, Version=0.1.0.30` |

---

## 3. 回歸測試結果摘要

| 回歸套件命令 | 涵蓋範圍 | 結果 |
| --- | --- | --- |
| `--test-issue-0046` | GitHub Releases API 更新解析、預設網址與舊版 Google Drive 設定遷移 | **PASS (3/3)** |
| `--test-issue-0040` | Google OAuth ClientId/ClientSecret DPAPI 儲存讀取持久化與 PKCE 流程 | **PASS (4/4)** |
| `--test-issue-0036` | `AppSettingsService` 語系持久化、UTF-8 BOM 相容、毀損回退與多執行緒併發安全 | **PASS (7/7)** |
| `--test-issue-0035` | 圖片載入、超高解析度智慧縮放壓縮與去識別化合成名片診斷 | **PASS (5/5)** |
| `--test-issue-0022` | CSV / vCard 匯出與 Google People API Payload（使用 `example.com` 測試網域） | **PASS (8/8)** |
