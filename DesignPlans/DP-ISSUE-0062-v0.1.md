# 設計規劃書：ISSUE-0062 已存名片總覽支援左右區塊（表格與詳細明細）可拖拉調整欄寬（GridSplitter）

- **文件識別碼**：`DP-ISSUE-0062-v0.1`
- **關聯 Issue**：`ISSUE-0062`
- **需求規格**：`Requirements/RS-ISSUE-0062-v0.1.md`
- **建立日期**：`2026-10-31`
- **負責角色**：SA 系統架構師、設計工程師
- **審核角色**：PM Agent、軟體工程師、測試工程師

---

## 1. 架構影響判定 (Architecture Impact Assessment)

| 評估項目 | 影響判定 | 說明 |
| :--- | :---: | :--- |
| **系統架構與邊界** | 無影響 | 僅調整 MajiOCR.Windows 桌面端之 WPF 版面配置，無跨模組介面調整。 |
| **資料庫 (DBA)** | 不適用 | 無 Schema 變更，不寫入 SQLite 資料庫。 |
| **維運工程師** | 不適用 | 無伺服端、背景服務或長期運維影響。 |
| **系統整合工程師** | 不適用 | 無跨系統外部服務串接。 |
| **UI/UX 設計** | 有影響 | 新增左右區塊拖拉分割條（GridSplitter），增強視覺反饋、游標形狀與手把樣式。 |

---

## 2. 佈局重構與 Grid 欄位定義

### 2.1 原版面結構 vs 新版面結構

- **原佈局 (`mainSplitGrid`)**：
  - Column 0: 左側名片表格 (`Width = 1*`)
  - Column 1: 右側詳細抽屜 (`Width = 340`)
  - `ColumnSpacing = 12`（固定間隔，無法滑鼠調整）

- **新佈局 (`mainSplitGrid`)**：
  - 將 `ColumnSpacing` 設為 `0`（由 Splitter 與 Padding 統一控制，避免間隔疊加與拖曳跳動）。
  - **Column 0**：左側名片表格（`Width = new GridLength(1, GridUnitType.Star), MinWidth = 360`）
  - **Column 1**：拖拉分割條（`_splitterColumnDef = new ColumnDefinition { Width = GridLength.Auto }`）
  - **Column 2**：右側詳細抽屜（`_detailColumnDef = new ColumnDefinition { Width = new GridLength(340), MinWidth = 260 }`）

### 2.2 拖拉控制項 (`GridSplitter`) 設計細節

```csharp
var splitter = new GridSplitter
{
    Width = 10,
    HorizontalAlignment = HorizontalAlignment.Center,
    VerticalAlignment = VerticalAlignment.Stretch,
    ShowsPreview = false, // 即時響應拖曳調整
    Cursor = Cursors.SizeWE,
    Background = Brushes.Transparent,
    Focusable = false
};
```

1. **拖曳感應區與外觀**：
   - 點擊/拖曳寬度為 10px，避免因像素過窄難以命中；
   - 內部繪製 1px 垂直線條（與介面邊框顏色一致 `CardStrokeColorDefaultBrush`）；
   - 中央配置 3 個垂直圓點（Grip dots）或短條紋，提供經典且直覺的拖拉把手視覺意象；
   - 支援 `MouseEnter` 與 `MouseLeave` 高亮反饋（Accent 藍色高亮），提示使用者此處為互動元件。

2. **雙擊重置預設值**：
   - 監聽 `MouseDoubleClick` 事件：當使用者雙擊分割條時，自動將右側抽屜重設回標準寬度（340px），並同步更新暫存記憶。

3. **收合與展開記憶機制**：
   - 定義成員變數 `private double _lastDetailDrawerWidth = 340;`。
   - 點擊「收合明細」時：
     - 若抽屜目前為展開狀態，記錄 `_detailColumnDef.ActualWidth`（防呆：大於 200 時存入 `_lastDetailDrawerWidth`）；
     - 將 `_detailColumnDef.Width = new GridLength(0);`；
     - 將 `_detailDrawerBorder.Visibility = Visibility.Collapsed;`；
     - 將 `_gridSplitter.Visibility = Visibility.Collapsed;`。
   - 點擊「展開明細」時：
     - 將 `_detailColumnDef.Width = new GridLength(_lastDetailDrawerWidth);`；
     - 將 `_detailDrawerBorder.Visibility = Visibility.Visible;`；
     - 將 `_gridSplitter.Visibility = Visibility.Visible;`。

---

## 3. 測試與驗證策略

1. 確保各欄位（姓名、電話、圖片、編輯按鈕）在調整大小過程中無遮擋、折行正常且捲軸正常響應。
2. 驗證極限拖曳向左與向右，確認最小寬度限制生效（不發生崩潰或負寬度異常）。
3. 驗證收合與展開按鈕之功能與寬度記憶完整性。
4. 驗證編譯與免安裝單檔發布正常無 Warning/Error。
