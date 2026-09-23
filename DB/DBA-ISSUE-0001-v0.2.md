# 資料庫影響評估：ISSUE-0001 本機聯絡人與名片影像保存 v0.2

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | DBA 草案，待人類確認保存與備份決策 |
| 版本 | 0.2.0-draft |
| 更新日期 | 2026-09-23 |
| 本版變更 | 使用者已確認原始名片影像與完成後的結構化聯絡人資料皆須保存。 |
| 前版關係 | 本文件取代 `DB/DBA-ISSUE-0001.md` 作為目前 DBA 草案；原檔保留為歷史。 |

## 1. 結論與資料邊界

使用者已確認：完成後必須保存原始名片影像及結構化聯絡人資料，供手機本機後續查詢使用。

DBA 建議採取「Room / SQLite 保存可查詢的結構化資料與影像 metadata，App 私有內部檔案區保存原始影像位元資料」的模式，而不是將完整影像 BLOB 塞入 SQLite。這可讓聯絡人索引、欄位查詢與 Room migration 保持小而可控，同時仍能在同一筆聯絡人詳情中取回原始影像。

此設計中，影像與 SQLite 是同一筆完成記錄的兩個受控資產，但不是同一個資料庫交易。故不可聲稱純 Room 交易能原子保存影像；必須使用 staging、狀態與啟動復原來確保一般查詢只看到完整的完成資料。

完整原始 OCR 全文、即時影格、裁切／旋轉後衍生影像、雲端供應商長期 API key 仍不保存。

## 2. 資料生命週期

| 資料 | 儲存位置 | 保存時點 | 保存策略 |
| --- | --- | --- | --- |
| Live OCR 候選、影格、位置框 | 記憶體／當次暫存 | 掃描期間 | 結束或放棄後清除。 |
| Locked 草稿 | 記憶體／當次暫存 | 使用者鎖定後 | 按完成前可修改或放棄；不作永久資料。 |
| 原始影像 staging 檔 | App 私有 `filesDir/card-images/.pending/` | 使用者確認完成時 | 僅為定稿過程；不可被一般查詢或分享。 |
| 原始名片影像 | App 私有 `filesDir/card-images/` | 定稿完成後 | 以不含個資語意的隨機資產檔名保存，並以 SHA-256 驗證。 |
| 完成聯絡人與子欄位 | App 私有 Room / SQLite | 完成交易成功後 | 保留供本機查詢。 |
| 影像 asset metadata | App 私有 Room / SQLite | 完成交易成功後 | 保存相對路徑、雜湊、MIME、尺寸、大小、來源與狀態。 |
| Android Contacts 匯出狀態 | App 私有 Room / SQLite | 匯出完成、取消或失敗後 | 保留狀態，不取代本機記錄。 |
| 完整 OCR 原文、裁切衍生影像 | 不保存 | 不適用 | 保存原始影像不等於保存所有 OCR 中間資料。 |
| OpenAI、Gemini、雲端 OCR 長期 API key | 不保存 | 不適用 | 只可在受控後端保存。 |

## 3. 建議邏輯資料模型

### 3.1 contact_records

每一筆完成聯絡人一筆主記錄，保留既有名稱、公司、職稱、備註、完成來源、建立／更新時間、Android Contacts 匯出狀態等欄位。

新增或明確化欄位：

| 欄位 | 規則 |
| --- | --- |
| contact_id | App 產生且不可猜測的主鍵。 |
| persistence_state | `PendingFinalize`、`Ready`、`Failed` 或 `ImageMissing`；一般查詢預設只列出 `Ready`。 |
| completed_at | 影像及結構化資料都 Ready 後才設定的完成時間。 |
| completion_source | Manual、OfflineOcr、CloudOcr、Ai 或混合來源的可讀摘要。 |

### 3.2 contact_values

此表保存電話、Email、網址、地址、未分類候選等可多筆值，並保留：

| 欄位 | 規則 |
| --- | --- |
| value_kind | Name、Company、Title、Phone、Email、Website、Address、Note、Unclassified。 |
| value | 使用者最後確認的文字。 |
| normalized_value | 僅在型別適用時用於查詢，例如電話或 Email。 |
| label / ordinal | 支援 Home、Work、Mobile、自訂標籤與同類多筆的呈現順序。 |
| source_mode / locked_at | 記錄值由 Manual、OfflineOcr、CloudOcr 或 Ai 建立，以及使用者鎖定時間。 |

### 3.3 card_image_assets

第一版每個 `contact_records` 最多關聯一筆原始名片影像；雙面名片若納入，改為一對多，不覆寫既有資產。

| 欄位 | 規則 |
| --- | --- |
| image_id | App 產生且不可猜測的主鍵。 |
| contact_id | 非空外鍵，指向 `contact_records`；第一版可加唯一限制。 |
| relative_path | 僅允許相對於 `filesDir/card-images/` 的受控檔名；不得保存外部 URI、絕對路徑或 `..` traversal。 |
| pending_relative_path | 僅在 `PendingFinalize` 使用；定稿後清空。 |
| mime_type / byte_count / width / height | 由實際寫入檔案驗證後記錄。 |
| sha256 | 寫檔後計算；用於完整性核對，非使用者搜尋鍵。 |
| capture_source | CameraCapture 或 ImagePickerCopy；選取來源一律複製至私有區。 |
| storage_state | `PendingFinalize`、`Ready`、`Failed`、`Missing` 或 `PendingDelete`。 |
| created_at / finalized_at / deleted_at | 使用 ISO 8601 含時區。 |

不得保存帶有可識別姓名、公司或電話的檔名。縮圖不是目前需求；若未來加入，必須視為另一份敏感影像資產並建立獨立保存規則。

### 3.4 索引與查詢準備

至少建立：

- `contact_records(persistence_state, completed_at)`，讓一般查詢排除未定稿記錄。
- `contact_values(contact_id, value_kind, normalized_value)`，支援姓名、公司、電話及 Email 類查詢。
- `card_image_assets(contact_id)`、`card_image_assets(storage_state)` 與 `card_image_assets(sha256)`。
- `contacts_export_state` 索引，供查詢尚未匯出的本機聯絡人。

後續若需要跨欄位模糊搜尋，應另立需求評估 Room FTS / FTS5；第一版不假定全文索引必需，也不直接暴露 SQLite 檔案。

## 4. 寫入、定稿與復原契約

### 4.1 完成操作

1. 驗證草稿中的必要聯絡人欄位及原始影像可讀性；相簿來源必須先複製為 App 私有副本。
2. 以隨機 `image_id` 將原始位元資料寫入 `.pending/`，計算 SHA-256、MIME、尺寸與大小；若寫檔失敗，不開始建立可見聯絡人記錄。
3. 以單一 Room 交易寫入 `contact_records(persistence_state=PendingFinalize)`、`contact_values` 與 `card_image_assets(storage_state=PendingFinalize)`。
4. 在同一個 App 私有檔案系統內把 staging 檔案原子改名到最終 `card-images/` 路徑。
5. 以第二個短 Room 交易把影像與聯絡人狀態改為 `Ready`，寫入 `finalized_at` 與 `completed_at`。
6. 只有第五步成功後才在 UI 顯示完成，且一般查詢只取回 `Ready` 資料。

### 4.2 中斷或錯誤後的處理

| 偵測狀態 | 處理方式 | 不得發生 |
| --- | --- | --- |
| staging 檔存在但無資料庫記錄 | 啟動或清理程序確認無對應資產後刪除 staging 檔。 | 將暫存檔當作已完成名片。 |
| Pending 記錄與 staging 檔都存在 | 復原程序驗證雜湊後完成定稿，或標記 Failed 並保留可重試資訊。 | 一般查詢顯示為 Ready。 |
| Pending 記錄存在、最終影像已存在 | 核對雜湊與 metadata 後，將狀態收斂為 Ready。 | 複寫或任意刪除使用者影像。 |
| Ready 記錄但影像遺失或雜湊不符 | 標記 `ImageMissing`，保留結構化聯絡人並顯示可理解錯誤。 | 靜默顯示錯誤影像或假裝完整。 |
| 使用者刪除聯絡人 | 依未來確認的 D-07 建立受控刪除流程；檔案刪除失敗時標記 `PendingDelete` 供後續重試。 | 以未驗證的資料庫路徑遞迴刪除檔案。 |

## 5. 安全、保存、備份與刪除邊界

- 原始影像必須存於 App 私有內部持久檔案區，而非系統相簿、共享儲存或可移除外部儲存。這讓資料只對本 App 有意義且避免其他 App 直接讀取。
- `relative_path` 由程式產生並經 allowlist 解析；任何由 UI、OCR、檔名或外部 URI 提供的路徑都不可直接使用。
- Android 的 App 私有內部儲存與資料庫在 App 卸載時會刪除；若使用者期望跨卸載保留，這是另一個同步／匯出需求，不可由本版暗自補上。
- Android Auto Backup 預設可能涵蓋內部 `filesDir` 與資料庫；原始影像會增加敏感資料與 25 MB 備份額度風險。D-06 確認前，不得宣稱影像可跨裝置或雲端復原；實作時須用資料擷取規則明確 include／exclude。
- 平台儲存加密不等於本產品已完成資料庫額外加密。D-06 決定後才可選擇額外加密、金鑰生命周期及備份策略。
- Android Contacts 匯出、雲端 OCR 與 AI 都是獨立外部動作；保存本機影像不授權將影像傳送出去。

## 6. 驗收與測試要求

| 情境 | 通過條件 |
| --- | --- |
| 相機拍攝完成 | App 重啟後，可依聯絡人找到結構化欄位與相同 SHA-256 的原始影像。 |
| 相簿選取完成 | 即使原始外部 URI 日後失效，本 App 私有副本仍可供查詢。 |
| 多筆電話／Email | 所有子欄位保留順序與類型，不互相覆寫。 |
| 寫檔故障 | 不建立 Ready 聯絡人；草稿仍可重試或取消。 |
| DB 交易故障 | 查不到 Ready 的半筆資料、孤兒 `contact_values` 或 Ready 影像 metadata。 |
| 改名或重啟中斷 | 啟動復原會安全地完成、隔離或清理 Pending 狀態，不會顯示假成功。 |
| Ready 影像遺失 | 結構化聯絡人仍可讀，且影像狀態明確為 Missing。 |
| 隱私邊界 | 系統圖庫、共享儲存、一般日誌與未同意網路請求中均找不到原始影像。 |
| Backup 規則 | D-06 確認後，驗證雲端備份與裝置轉移的 include／exclude 行為。 |

## 7. 開發前阻擋與待決事項

| 待決事項 | 是否阻擋 | 原因 |
| --- | --- | --- |
| D-06：資料庫額外加密與 Android Auto Backup／裝置轉移 | 是 | 原始影像使資料敏感度、金鑰與備份邊界顯著提高。 |
| D-07：影像保存期限及刪除聯絡人時是否同步刪除 | 是 | 決定資料生命週期、容量與受控刪除。 |
| D-08：原始影像最大解析度、大小與壓縮策略 | 否，可在技術設計階段定義 | 影響容量與效能，但不改變資料模型。 |
| 雙面名片 | 是，若納入第一版 | 會把一對一影像關係改為一對多。 |
| 查詢 UI 的詳細功能 | 否，可後續新增 | 現有結構與索引先支援基礎欄位與影像詳情查詢。 |
| 多裝置同步、分享與匯出 | 是，若新增 | 會改變資料安全、衝突與備份設計。 |

## 8. 參考資料

- [Android：存取 App 專屬檔案](https://developer.android.com/training/data-storage/app-specific)
- [Android：資料與檔案儲存概覽](https://developer.android.com/training/data-storage)
- [Android：以 Room 建立本機資料庫](https://developer.android.com/training/data-storage/room)
- [Android：Auto Backup 使用者資料](https://developer.android.com/identity/data/autobackup)
- [Android：以 SQLite 儲存資料](https://developer.android.com/training/data-storage/sqlite)
