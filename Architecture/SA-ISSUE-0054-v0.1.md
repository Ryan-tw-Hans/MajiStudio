# 系統架構評估書：ISSUE-0054 名片通訊錄支援 Excel 與 CSV 匯入與匯出

- **文件識別碼**：`SA-ISSUE-0054-v0.1`
- **關聯 Issue**：`ISSUE-0054`
- **架構師**：SA 系統架構師 Agent
- **日期**：`2026-10-19`
- **目標系統**：`MajiOCR.Windows` (.NET 10 + WinUI 3)

---

## 1. 架構影響判定

本變更擴充 Windows 桌面端之資料交換服務（Data Exchange Service），新增 Excel (`.xlsx`) 與通用 CSV (`.csv`) 的讀寫能力，並新增匯入互動對話框與批次資料庫寫入機制。
- **資料庫影響**：不修改底層 SQLite 資料庫 Schema；沿用既有 `contacts`、`contact_values` 及 `contact_tags` 表，但批次匯入必須以資料庫交易（Transaction）封裝。
- **依賴套件評估**：引入 `MiniExcel` (v1.46.0，MIT 授權)。
- **單檔發布評估**：MiniExcel 為 100% 純託管 C# 函式庫，無任何 Unmanaged C++ 或本機平台相依組件，完全相容於 `PublishSingleFile=true` 與 .NET 10 AOT/單檔打包機制。

---

## 2. 模組設計與介面契約

```mermaid
graph TD
    UI[SavedCardsTablePanel / ImportContactsDialog] --> Svc[IContactExcelCsvService]
    Svc --> MiniExcel[MiniExcel Engine (xlsx / csv)]
    Svc --> DupEngine[DuplicateDetectionEngine (跳過/覆蓋/新增)]
    Svc --> Repo[ContactRepository (SQLCipher Transaction)]
```

### 2.1 服務介面契約：`IContactExcelCsvService`
1. **`GenerateExcel(IReadOnlyList<ContactDetailModel> contacts)`**:
   - 產生包含標準名片欄位（姓名、公司、部門、職稱、手機、公司電話、Email、地址、統一編號、網站、分類標籤、備註、建立時間）之 `.xlsx` 二進位資料（byte array 或 MemoryStream）。
2. **`GenerateFullCsv(IReadOnlyList<ContactDetailModel> contacts)`**:
   - 產生包含完整名片屬性之 CSV 字串（UTF-8 with BOM，相容 Excel 直接點擊開啟）。
3. **`GenerateTemplate(bool isExcel)`**:
   - 產生標準空白匯入範本（含 2 筆範例資料）。
4. **`ParseImportFile(Stream stream, string extension)`**:
   - 智慧解析 `.xlsx` 或 `.csv` 內容，自動對齊欄位標頭，回傳標準化 `IReadOnlyList<ImportContactDraft>`。
5. **`ExecuteImportAsync(IReadOnlyList<ImportContactDraft> drafts, ImportDuplicateStrategy strategy)`**:
   - 依據重複裁決策略（Skip / Overwrite / AlwaysAdd），在交易保護下寫入 `ContactRepository`，並回傳統計報告 `ImportExecutionResult`。

---

## 3. 重複判定與整併策略 (Duplicate Resolution)

- **重複判定演算法**：
  - 條件 1：若 `Name` 相同且 `Company` 非空且相同，視為重複。
  - 條件 2：若 `Name` 相同且 `Phone/Mobile` 非空且相同，視為重複。
- **策略模式**：
  1. `SkipDuplicate`（跳過重複）：略過該筆資料，計入 `SkippedCount`。
  2. `OverwriteExisting`（覆蓋更新）：保留現有名片 ID，以匯入欄位更新現有資料（匯入非空欄位優先更新，保留舊有的未重疊欄位），計入 `UpdatedCount`。
  3. `AlwaysAdd`（一律新增）：直接插入新名片記錄，計入 `InsertedCount`。

---

## 4. 安全與交易邊界

1. **記憶體防護**：MiniExcel 採串流（Streaming）解析，防止惡意超大檔案導致 OOM（Out of Memory）。
2. **ACID 交易完整性**：批次寫入過程若有嚴重格式或資料庫錯誤，立即回滾（Rollback），避免資料庫呈現半匯入狀態。
3. **檔案備份**：批次匯入前自動觸發本機資料庫快照備份，防止不可逆的人為操作失誤。
