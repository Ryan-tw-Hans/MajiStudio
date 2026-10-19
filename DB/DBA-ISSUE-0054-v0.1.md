# 資料庫影響評估：ISSUE-0054 名片通訊錄支援 Excel 與 CSV 匯入與匯出

- **文件識別碼**：`DBA-ISSUE-0054-v0.1`
- **關聯 Issue**：`ISSUE-0054`
- **DBA**：DBA Agent
- **日期**：`2026-10-19`

---

## 1. 資料庫結構變更判定

- **Schema Migration**：**不適用 (No Schema Changes)**。
- 本次功能擴充僅使用既有之 `contacts`、`contact_values`、`contact_tags` 及 `tags` 資料表結構儲存名片資料。

---

## 2. 批次匯入之資料庫交易安全與效能要求

1. **ACID 交易邊界**：
   - 批次匯入 $N$ 筆名片時，必須使用 `using var transaction = connection.BeginTransaction()` 交易包裹。
   - 避免每筆名片各自獨立 commit 造成 I/O 嚴重延遲或中途異常造成資料殘缺。
2. **覆蓋更新 (Overwrite) 交易規則**：
   - 覆蓋更新既有名片時，更新 `contacts.updated_at`。
   - `contact_values` 採「補齊或更新指定 ValueKind」，若匯入內容非空則更新或新增對應的值欄位；保留現有未受影響之其他自訂欄位。
   - 標籤整併：匯入的標籤與現有名片標籤進行聯集（Union），確保已有標籤不遺失。
3. **備份防護**：
   - 批次匯入執行前，由軟體端自動建立本機資料庫快照於 `backups/` 資料夾，以防使用者誤匯入錯誤資料時可即時還原。
