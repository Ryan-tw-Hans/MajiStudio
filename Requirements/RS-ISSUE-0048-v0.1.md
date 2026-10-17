# 需求規格書：優化 Windows 端「iOS / Web 行動版設定與部署」對話框排版與重疊版面缺陷 (ISSUE-0048)

* **文件編號**：RS-ISSUE-0048-v0.1
* **關聯 Issue**：[`ISSUE-0048`](../IssueLog/IssueLog.sqlite3)
* **提出來源**：人類使用者反饋（提供實體螢幕截圖指示「你的排版也注意一下」）
* **版本**：v0.1
* **建立日期**：2026-10-17

---

## 1. 缺陷背景與問題現象

使用者於 `MajiOCR.Windows v0.1.0-beta.27` 開啟「📱 iOS / Web 行動版設定與部署 (GitHub Pages / Vercel)」對話框時，發現下列嚴重的 UI 版面缺陷：
1. **模式切換按鈕文字遭截斷**：
   對話框頂部模式切換列橫向寬度不足，導致第二個按鈕「⚡ 自訂：部署至個人 Vercel」文字被強制裁切成「⚡ 自訂：部署至個人 V...」。
2. **文字與控制項重疊嚴重（雙層 ScrollViewer / StackPanel Measure 缺陷）**：
   - 提示區塊文字與「步驟 1」之文字、按鈕垂直重疊遮蔽。
   - 步驟 2 中，「Web 端 Google OAuth Client ID」TextBox 遭到下方的「🚀 一鍵導入專案並發布至 Vercel」按鈕向上覆蓋截斷。
3. **寬度過窄與版面侷促**：
   對話框採用預設寬度（約 548px），扣除 Padding 後僅約 500px，而內部又寫死 `Width = 560`，造成右側出現冗餘捲軸，內容緊貼兩側，缺乏現代化卡片式階層感。

---

## 2. 需求驗收條件 (Acceptance Criteria)

* **AC-01（寬度與自適應佈局）**：
  - `ContentDialog` 顯式設定寬度（`MaxWidth = 720`，自適應拉伸），移除寫死的 `rootStack.Width = 560`。
  - 對話框在主流解析度下呈現開闊、大器的視窗比例，文字不會被無故擠壓換行。
* **AC-02（模式切換按鈕等寬與完整呈現）**：
  - 上方切換列改用 Grid 等寬並排（`ColumnDefinitions="*, *"`）或 Segmented 容器，確保兩大按鈕「🌟 推薦：官方公用免設定版 (GitHub Pages)」與「⚡ 自訂：部署至個人 Vercel」文字 100% 完整顯示，零截字、零省略號。
  - 作用中按鈕具備明確的視覺選取狀態（醒目背景與粗體）。
* **AC-03（解決雙層 ScrollViewer 與元素重疊）**：
  - 移除 `ContentDialog.Content` 內多餘嵌套的 `ScrollViewer`，改為外層 `ContentDialog` 統一接管或純自適應單一捲動容器，避免 DirectComposition 座標錯亂造成文字圖層重疊殘影。
  - 步驟 1、步驟 2、步驟 3 採用獨立卡片式 `Border`，內部設定充足 Padding (16px) 與控制項垂直 Spacing (12~14px)。
  - 將過長的說明文字移至標籤副標（Description/Subtext），不擠壓在 `TextBox.Header`，徹底杜絕按鈕疊在輸入框之上的現象。
* **AC-04（GitHub Pages 與 Vercel 雙模式版面美化）**：
  - GitHub Pages 模式：QR Code 居中且置於乾淨白底陰影卡片中，操作按鈕並排對齊，條列式說明文字清晰易讀。
  - Vercel 模式：步驟提示清晰，操作按鈕（前往註冊、建立 Token）尺寸與對齊一致，進度顯示列與完成結果區塊分離清晰。

---

## 3. 角色分工

* **SA 系統架構師**：評估對話框佈局改進對 WinUI 3 渲染管線之架構符合性，撰寫 `Architecture/AR-ISSUE-0048-v0.1.md`。
* **設計工程師**：規劃對話框之版面尺度、卡片視覺、層級間距與按鈕狀態，撰寫 `DesignPlans/DP-ISSUE-0048-v0.1.md`。
* **測試工程師**：制定對話框排版、模式切換、文字完整性與部署互動驗證計畫，撰寫 `TestPlans/TP-ISSUE-0048-v0.1.md`。
* **軟體工程師**：重構 `MajiOCR.Windows/VercelDeployDialog.cs`，推進版本至 `0.1.0-beta.28` 並編譯發布。
