# 需求規格書：支援 iOS PWA GitHub Pages 公用版發布與 QR Code 快速加入，並保留個人 Vercel 一鍵部署功能 (ISSUE-0047)

- **對應 Issue ID**：`ISSUE-0047`
- **需求版本**：`v0.1`
- **建立／更新時間**：`2026-10-17T12:37:00+08:00`
- **負責窗口**：PM Agent

---

## 1. 問題背景與目標

1. **現況背景**：
   - 先前在 `ISSUE-0044` 建立了 `MajiOCR.Web` (iOS PWA) 以及 Windows 端的「Vercel 一鍵部署精靈」。
   - 該模式要求每位使用者必須自己註冊 Vercel 帳號並申請 Access Token，對於一般 iPhone 使用者而言具有一定的操作門檻。
2. **核心目標**：
   - **GitHub Pages 公用免設定版**：將 `MajiOCR.Web` 部署至專案的 GitHub Pages（例如 `https://ryan-tw-hans.github.io/MajiStudio/`），建立一個所有人共用、免申請帳號的 Web PWA 進入點。
   - **Windows 端雙模式整合**：在 `MajiOCR.Windows` 桌面版中，將原本的部署精靈升級為雙模式支援：
     1. **🌟 推薦：GitHub Pages 官方免設定版**：預載 GitHub Pages 官方網址，直接顯示大尺寸 QR Code，iPhone 使用者只要拿起相機一掃，點擊「加入主畫面」即可立刻使用。
     2. **⚡ 自訂：部署至個人 Vercel**：完整保留原有的個人 Vercel 部署精靈，供需要個人獨立雲端網址的使用者使用。
   - **資料私隱與隔離保證**：無論使用者透過 GitHub Pages 公用版或個人 Vercel 開啟 PWA，所有名片資料庫與解密過程均 100% 發生在使用者各自的手機或個人 Google Drive，伺服器不保存任何使用者資料。

---

## 2. 功能與非功能需求及驗收條件 (AC)

### 2.1 GitHub Pages 部署自動化與相容性 (`MajiOCR.Web`)
* **AC-01（GitHub Actions 自動部署工作流程）**：
  - 於 `.github/workflows/deploy-pages.yml` 建立 GitHub Actions 工作流程，當 `main` 分支有更新且涉及 `MajiOCR.Web/**` 時，自動將 `MajiOCR.Web` 目錄內的靜態網頁資產發布至 GitHub Pages。
* **AC-02（子路徑相容性與相對路徑修正）**：
  - 確保 `index.html`、`manifest.webmanifest`、`sw.js`、`styles.css` 以及 `js/` 內的所有引用皆使用相對路徑（`./`），避免因 GitHub Pages 專案子目錄（如 `/MajiStudio/`）導致 404 或 Service Worker 註冊失敗。

### 2.2 Windows 端介面升級 (`MajiOCR.Windows`)
* **AC-03（雙模式對話框與 QR Code 呈現）**：
  - 將選單與對話框升級為「📱 iOS / Web 行動版 (GitHub Pages / Vercel)」。
  - 對話框提供兩大分頁（Tabs）或明確切換模式：
    - **分頁 1：GitHub Pages 官方公用版（免設定）**：
      - 預設顯示官方 GitHub Pages 網址（例如 `https://ryan-tw-hans.github.io/MajiStudio/`），支援使用者手動修改與儲存。
      - 立即在介面上動態繪製對應的高解析度 QR Code 與「📋 複製網址」按鈕。
      - 提供清晰指引：「拿起 iPhone 相機掃描 QR Code ➔ 於 Safari 點擊『分享』➔ 選擇『加入主畫面』」。
    - **分頁 2：自訂 Vercel 部署**：
      - 完整保留既有的 3 步驟 Vercel 一鍵部署功能、Token 驗證、自動發布 REST API 與個人部署專屬 QR Code。
* **AC-04（設定持久化）**：
  - 於 Windows 端 AppSettings 中新增 `GitHubPagesUrl` 欄位，預設為官方網址，並可記錄使用者自訂輸入。

### 2.3 跨平台資料庫解密與唯讀查詢檢驗
* **AC-05（iPhone 讀取驗證）**：
  - 驗證 iPhone 使用者透過 GitHub Pages 網址開啟 PWA 後，可順利透過「📂 從手機檔案匯入」或「Google Drive」載入 Windows 端匯出的 SQLCipher-AES256 加密資料庫，完成解密、通訊錄瀏覽、複合搜尋與一鍵撥號。

---

## 3. 角色分工與責任

| 角色 | 責任摘要 | 交付物 |
| :--- | :--- | :--- |
| **PM Agent** | 受理需求、建立 IssueLog、定義需求規格與驗收條件、統一對外窗口與進度回報 | `RS-ISSUE-0047-v0.1.md`、IssueLog 紀錄 |
| **SA 系統架構師** | 評估 GitHub Pages 子路徑適配、GitHub Actions CI/CD 與 Windows 端雙模式架構 | `Architecture/AR-ISSUE-0047-v0.1.md` |
| **設計工程師** | 規劃 Windows 端「雙模式 iOS / Web 行動版」UI 介面排版、分頁切換與 QR Code 視覺呈現 | `DesignPlans/DP-ISSUE-0047-v0.1.md` |
| **軟體工程師** | 1. 調整 `MajiOCR.Web` 相對路徑與 Service Worker<br>2. 撰寫 GitHub Actions 部署腳本<br>3. 重構 `VercelDeployDialog` 支援雙模式與 GitHub Pages QR Code | 程式碼變更與本機建置驗證 |
| **測試工程師** | 驗證 Web PWA 在子路徑下的加載與離線運作、驗證 QR Code 解析正確性與 Windows UI 交互 | `TestPlans/TP-ISSUE-0047-v0.1.md`、測試報告 |

---

## 4. 目標版本與獨立程式資料夾

| 程式識別名 | 獨立程式資料夾 | 目標版本 | 備註 |
| :--- | :--- | :--- | :--- |
| `MajiOCR.Web` | `MajiOCR.Web` | `v1.1.0` | 適配 GitHub Pages 與 PWA 離線快取 |
| `MajiOCR.Windows` | `MajiOCR.Windows` | `v1.2.0` | 支援雙模式 iOS / Web 對話框與 QR Code |
