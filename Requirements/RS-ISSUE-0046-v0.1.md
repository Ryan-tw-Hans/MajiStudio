# 需求規格書：全面廢止 Google Drive 更新來源，改採 GitHub Releases 作為軟體自動檢查與更新機制

- 文件識別碼：`RS-ISSUE-0046-v0.1`
- 關聯 Issue：`ISSUE-0046`
- 需求種類：需求 / 架構優化
- 優先級：高
- 提出來源：使用者明確指示
- 受理窗口：PM Agent
- 負責角色：PM / SA / 軟體工程師 / 測試工程師

---

## 1. 背景與現況問題

1. **現有 Google Drive 更新機制痛點**：
   - 軟體自動更新管理員原本仰賴手動執行腳本上傳更新包至 Google Drive，並手動/透過腳本維護 Google Drive 上的 `latest.json` 索引檔案。
   - 當使用者在 GitHub 建立 Release 後，因 Google Drive 上的 `latest.json` 未同步發布，導致舊版本軟體無法即時檢查到新版本資訊。
   - Google Drive 下載常受限於 Google 頻寬、Cookie 與檔案大小確認警告頁面。

2. **使用者目標**：
   - 使用者明確指示：全面放棄從 Google Drive 讀取更新，所有自動檢查與下載更新改由 **GitHub Releases** 負責。

---

## 2. 需求目標與驗收條件

### 2.1 驗收條件一：DefaultReleaseManifestUrl 切換為 GitHub Releases API
1. `UpdateService.DefaultReleaseManifestUrl` 預設網址設定為：
   `https://api.github.com/repos/Ryan-tw-Hans/MajiStudio/releases/latest`。
2. 支援設定檔平滑遷移：若使用者的 `UpdateSettings.json` 內存有舊版 Google Drive 網址，系統載入時自動將其重設為預設的 GitHub Releases API 網址。

### 2.2 驗收條件二：原生解析 GitHub Releases API JSON
1. `UpdateManifest.Parse` 支援解析 GitHub Releases API 回傳之 JSON 結構：
   - `tag_name` ➔ `Version`（自動去除前綴 `v`/`V`）
   - `name` ➔ `Description`
   - `body` ➔ `ChangeLog`（直接內嵌展示，免額外網路請求）
   - `published_at` ➔ `ReleaseDate`
   - `assets` ➔ 自動篩選 `MajiOCR.Windows.zip` 或 `.zip` 附件，將其 `browser_download_url` 設為 `PackageUrl`。

### 2.3 驗收條件三：安全下載與套用更新
1. `DownloadAndVerifyAsync` 支援在未顯式提供 SHA-256 雜湊的情況下，自動計算檔案 SHA-256 並驗證 ZIP 檔案特徵（Magic Number `0x50 0x4B`）與目錄結構，確保更新包下載完整性。
2. 若 Release 說明或 manifest 中提供了 64 字元 SHA-256，則維持嚴格比對。

### 2.4 驗收條件四：版本晉級與 CI/CD
1. 軟體版本由 `0.1.0.25` 晉級至 `0.1.0.26`（候選版號 `0.1.0-beta.26`）。
2. 更新 `VERSION.txt`、`CHANGELOG.txt` 與 `MajiOCR.Windows.csproj`。
3. 全數通過自動化驗證測試。
