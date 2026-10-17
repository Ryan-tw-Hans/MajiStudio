# 測試報告：ISSUE-0047 支援 iOS PWA GitHub Pages 公用版發布與 QR Code 快速加入，並保留個人 Vercel 一鍵部署功能

- **對應 Issue ID**：`ISSUE-0047`
- **測試版本**：`v0.1`
- **測試日期**：`2026-10-17T12:48:00+08:00`
- **測試工程師 Agent**
- **測試結果**：全部通過 (ALL PASSED)

---

## 1. 測試環境

- **作業系統**：Windows 11 (X64)
- **開發平台**：.NET 10.0.26100.0 / WinUI 3 (Windows App SDK)
- **前端環境**：iOS PWA (Safari / WebKit 離線相容，支援 WebCrypto、IndexedDB、Service Worker)
- **CI/CD**：GitHub Actions (`.github/workflows/deploy-pages.yml`)

---

## 2. 測試項目執行結果

| 測試編號 | 測試項目 | 驗證項目與實測細節 | 結果 |
| :--- | :--- | :--- | :---: |
| **TC-01** | `MajiOCR.Web` 相對路徑檢核 | 檢核 `index.html`、`manifest.webmanifest`、`sw.js`、`styles.css` 及 `js/` 目錄內之資源路徑。確認全部採用相對路徑（`./`），無任何根目錄絕對路徑（`/`），完全相容 GitHub Pages Repo 子路徑（例如 `https://ryan-tw-hans.github.io/MajiStudio/`）。 | **通過** |
| **TC-02** | GitHub Actions 部署腳本檢核 | 檢核 `.github/workflows/deploy-pages.yml`：觸發條件為 `main` 分支更新且涵蓋 `MajiOCR.Web/**`，具備 `pages: write` 與 `id-token: write` 權限，採用官方 `upload-pages-artifact@v3` 與 `deploy-pages@v4`。 | **通過** |
| **TC-03** | Windows 雙模式對話框與 QR Code | 檢驗 `VercelDeployDialog.cs`：預設呈現「🌟 推薦：GitHub Pages 官方公用免設定版」，即時渲染大尺寸高解析度 QR Code，提供一鍵複製網址、開啟瀏覽器預覽與 iPhone 安裝指南；點擊可切換至「⚡ 自訂：部署至個人 Vercel」完整保留原有 3 步驟部署流程。 | **通過** |
| **TC-04** | Windows 專案編譯與設定持久化 | 執行 `dotnet build MajiOCR.Windows/MajiOCR.Windows.csproj -c Debug`：編譯成功零錯誤、零警告。`AppSettings.cs` 與 `AppSettingsService.cs` 成功新增 `GitHubPagesUrl` 儲存與讀取邏輯。 | **通過** |
| **TC-05** | 回歸測試與解密相容性 | 執行現有自動化整合測試套件（SQLCipher-AES256 解密、密碼錯誤攔截、內嵌靜態包檢核、Vercel API 模擬、QR Code 解碼、ZIP 備份還原）：共 6 項測試，全數通過 (6/6 Pass, 0 Fail)。 | **通過** |

---

## 3. 測試結論與交付物狀態

- **驗收結論**：`ISSUE-0047` 定義之所有驗收條件（AC-01 至 AC-05）均已全數達成並驗證通過。
- **程式交付物**：
  - [`.github/workflows/deploy-pages.yml`](../.github/workflows/deploy-pages.yml)
  - [`MajiOCR.Windows/VercelDeployDialog.cs`](../MajiOCR.Windows/VercelDeployDialog.cs)
  - [`MajiOCR.Windows/Models/AppSettings.cs`](../MajiOCR.Windows/Models/AppSettings.cs)
  - [`MajiOCR.Windows/Services/AppSettingsService.cs`](../MajiOCR.Windows/Services/AppSettingsService.cs)
  - [`MajiOCR.Windows/MainPage.xaml`](../MajiOCR.Windows/MainPage.xaml)
  - [`MajiOCR.Windows/SavedCardsTablePanel.cs`](../MajiOCR.Windows/SavedCardsTablePanel.cs)
