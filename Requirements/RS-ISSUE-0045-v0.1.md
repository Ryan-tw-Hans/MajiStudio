# 需求規格書：修復資料欄位使用 Ctrl+V 貼上文字時誤觸發貼上圖片提示與 GitHub README Mermaid 渲染錯誤

- 文件識別碼：`RS-ISSUE-0045-v0.1`
- 關聯 Issue：`ISSUE-0045`
- 需求種類：缺陷修復 (Bugfix)
- 優先級：高
- 提出來源：使用者回報
- 受理窗口：PM Agent
- 負責角色：PM / SA / 軟體工程師 / 測試工程師

---

## 1. 背景與現況問題

### 1.1 問題一：文字欄位貼上文字時誤觸發貼上名片圖片錯誤提示
在 MajiOCR.Windows 桌面端主畫面中，使用者在名片草稿區的文字輸入欄位（如姓名、公司、職稱、電話、Email、地址、統編、自訂欄位等 TextBox 控制項）使用 `Ctrl+V` 貼上複製的文字時：
1. 文字欄位雖能貼上文字，但因全域 CommandBar 上 `PasteImageButton` 定義了全域鍵盤快捷鍵 `<KeyboardAccelerator Key="V" Modifiers="Control" />`，導致 WinUI 3 同步觸發了 `PasteImageButton.Click`。
2. 貼上圖片邏輯向系統剪貼簿查詢圖片資料，因剪貼簿目前為文字而非圖片，回傳「剪貼簿中未偵測到有效圖片資料 (僅支援圖片或單一張圖片檔案)。」。
3. 系統彈出對話框中斷使用者操作流程，嚴重影響使用者編輯體驗。

### 1.2 問題二：GitHub README Mermaid 流程圖渲染失敗
在 GitHub 專案首頁 (github.com/Ryan-tw-Hans/MajiStudio) 檢視 `README.md` 時，架構章節之 Mermaid 流程圖區塊顯示：
`Unable to render rich display`
`Cannot read properties of undefined (reading 'render')`
其原因為 Mermaid 原始碼中使用了含有雙引號/巢狀圓括號/資料庫圓柱體複合括號 `[("...")]` 及 Subgraph 特殊字元，導致 GitHub Mermaid 渲染引擎在解析 AST 時拋出 TypeError。

---

## 2. 需求目標與驗收條件

### 2.1 驗收條件一：文字編輯狀態下屏蔽貼上圖片
1. 當使用者焦點位於任何文字輸入控制項（`TextBox`、`PasswordBox`、`RichEditBox`、`AutoSuggestBox` 或其內部 Visual Tree 元件）時，按下 `Ctrl+V` 僅執行文字貼上，**嚴禁**觸發貼上名片圖片流程，亦**嚴禁**彈出「未偵測到有效圖片資料」提示視窗。
2. 當使用者焦點**不在**文字輸入控制項（例如焦點在圖片畫布、工作區空白處、按鈕、清單或無特定輸入焦點）時，按下 `Ctrl+V` 維持快捷鍵功能：若剪貼簿有圖片則貼上圖片，若無圖片則提示「未偵測到有效圖片資料」。
3. 使用者以滑鼠主動點擊工具列「貼上圖片」按鈕時，無論目前焦點為何，均正常執行貼上圖片流程。

### 2.2 驗收條件二：GitHub README Mermaid 圖表修正
1. 將 `README.md` 中之 Mermaid 流程圖語法優化為標準 `graph TD` 相容語法，移除可能引發解析器異常的複合括號 `[("...")]`、雙引號巢狀括號與不相容連線語法。
2. 確保在 GitHub Markdown 渲染環境下能穩定正確呈現清晰的架構圖，無任何渲染失敗錯誤。

### 2.3 驗收條件三：版本更新與發布
1. 版本由 `0.1.0.24` / `0.1.0-beta.24` 升級為 `0.1.0.25` / `0.1.0-beta.25`。
2. 更新 `VERSION.txt`、`CHANGELOG.txt` 與 `MajiOCR.Windows.csproj`。
3. 完成自包含單檔發布 `publish/MajiOCR.Windows/MajiOCR.Windows.exe`。
4. 全數通過自動化驗證測試。
