# 測試計畫：全面廢止 Google Drive 更新來源改採 GitHub Releases

- 文件識別碼：`TP-ISSUE-0046-v0.1`
- 關聯 Issue：`ISSUE-0046`
- 關聯規格：`RS-ISSUE-0046-v0.1`
- 負責角色：測試工程師 Agent

---

## 1. 測試範圍與案例

1. **TC-01：GitHub Releases API 格式 JSON 解析測試**
   - 驗證 `UpdateManifest.Parse` 能正確解析 GitHub Releases API JSON（包含 `tag_name`、`name`、`body`、`published_at`、`assets`）。
   - 驗證版本號去除 `v` 前綴，`PackageUrl` 自動對應至 `MajiOCR.Windows.zip` 之 `browser_download_url`。

2. **TC-02：DefaultReleaseManifestUrl 與舊網址遷移測試**
   - 驗證 `UpdateService.DefaultReleaseManifestUrl` 為 `https://api.github.com/repos/Ryan-tw-Hans/MajiStudio/releases/latest`。
   - 驗證當設定檔含有 `drive.google.com` 舊網址時，`LoadSettings()` 自動平滑遷移為預設 GitHub Releases API 網址。

3. **TC-03：版本號升級與一致性測試**
   - 驗證 `VERSION.txt`、`CHANGELOG.txt` 與 `MajiOCR.Windows.csproj` 同步更新至 `0.1.0.26` / `0.1.0-beta.26`。
