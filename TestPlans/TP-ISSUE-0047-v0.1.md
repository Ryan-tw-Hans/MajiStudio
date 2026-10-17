# 測試計畫書：ISSUE-0047 支援 iOS PWA GitHub Pages 公用版發布與 QR Code 快速加入，並保留個人 Vercel 一鍵部署功能

## 1. 測試目標與範圍

- **對應 Issue ID**：`ISSUE-0047`
- **測試版本**：`v0.1`
- **負責角色**：測試工程師 Agent
- **相關文件**：[`Requirements/RS-ISSUE-0047-v0.1.md`](../Requirements/RS-ISSUE-0047-v0.1.md)、[`Architecture/AR-ISSUE-0047-v0.1.md`](../Architecture/AR-ISSUE-0047-v0.1.md)

---

## 2. 測試項目清單

| 測試項目編號 | 測試項目名稱 | 測試步驟與驗證重點 | 預期結果 |
| :--- | :--- | :--- | :--- |
| **TC-01** | `MajiOCR.Web` 相對路徑檢核 | 檢查 `MajiOCR.Web/index.html`、`manifest.webmanifest`、`sw.js` 靜態資源引用路徑 | 全部使用相對路徑（`./`），無根目錄絕對路徑（`/`），相容 GitHub Pages 子目錄 |
| **TC-02** | GitHub Actions 腳本語法檢驗 | 檢查 `.github/workflows/deploy-pages.yml` 之 Trigger、權限（`pages: write`、`id-token: write`）與發布目錄 | 工作流程語法正確，支援推送至 `main` 時自動打包發布 `MajiOCR.Web` |
| **TC-03** | Windows 雙模式對話框切換與 QR Code | 啟動 `MajiOCR.Windows` 點擊「📱 iOS / Web 行動版」，檢驗「官方免設定版」與「自訂 Vercel 部署」切換 | 預設呈現 GitHub Pages 官方版與大尺寸清晰 QR Code，可切換至 Vercel 部署且設定不遺失 |
| **TC-04** | Windows 專案建置與單元測試 | 執行 `dotnet build MajiOCR.Windows.sln`（或專案檔）與現有自動化測試 | 建置成功零錯誤，無語法或編譯警告異常 |
| **TC-05** | QR Code 內容解析驗證 | 以 QR 解碼工具解析產生的點陣圖 | 解碼出的文字與指定的 GitHub Pages 網址一致 |
