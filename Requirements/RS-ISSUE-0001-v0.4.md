# 需求規格增補：ISSUE-0001 Android Auto Backup 與裝置轉移 v0.4

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | 草案；本增補已記錄使用者確認的備份政策，仍待加密與保存生命週期決策後才可實作。 |
| 版本 | 0.4.0-draft |
| 更新日期 | 2026-09-23 |
| 基準文件 | `Requirements/RS-ISSUE-0001-v0.3.md`；除本文件明確變更者外，其餘 v0.3 需求維持有效。 |
| 本版決策 | 使用者允許原始名片影像與完成後結構化聯絡人資料納入 Android Auto Backup 與裝置轉移。 |

## 1. 已確認的備份政策

使用者確認允許下列已完成的資料，依 Android 系統備份與換機機制保存／轉移：

1. App 私有 Room / SQLite 中 `Ready` 聯絡人、欄位值、影像 metadata 與必要查詢索引資料。
2. App 私有 `filesDir/card-images/` 中已定稿的原始名片影像。

此決策只授權 Android 平台的 Auto Backup 與 device-to-device transfer，不代表：

- 自動傳送影像到雲端 OCR、OpenAI、Gemini 或任何 AI 服務。
- 將名片影像公開到系統相簿、共享儲存或其他 App。
- 保存完整原始 OCR 全文、即時影格、裁切衍生影像、日誌、暫存檔或 API key。
- 已決定 App 層資料庫／檔案額外加密、影像保留期限、刪除策略或影像品質上限。

## 2. 需求變更

| ID | 新增或修訂需求 |
| --- | --- |
| FR-21 | App 必須以 Android 官方備份設定建立明確 allowlist，讓已完成的 Room / SQLite 資料與 `card-images/` 原始影像可納入 cloud backup 及 device-to-device transfer；不得只依賴平台預設範圍。 |
| FR-22 | 備份 allowlist 必須排除 `.pending/`、cache、OCR 中間影格、完整 OCR 原文、日誌、診斷輸出、外部 URI、供應商 API key 與任何未 Ready 的資產。若 include 規則會涵蓋子目錄，排除規則必須優先保護 `.pending/`。 |
| FR-23 | 還原或換機後，App 必須先執行既有 Pending／Ready Recovery 與影像雜湊核對，再讓一般查詢顯示記錄。若影像不存在或不符，保留結構化資料並標示 `ImageMissing`，不得假裝完成。 |
| FR-24 | App 必須在首次啟用或設定畫面清楚說明：「原始名片影像與聯絡人資料可依 Android 裝置／帳戶政策備份及轉移」。文案不得宣稱立即上傳、保證還原，或把 Android Backup 與 AI／雲端 OCR 混為同一服務。 |
| FR-25 | 偵測到可預期的備份容量風險、儲存不足或還原不完整時，App 必須提供可理解狀態與資料管理入口；不得為了符合備份額度而靜默刪除原始影像或結構化資料。 |

## 3. 備份與還原行為契約

| 階段 | App／平台行為 | 使用者可見結果 |
| --- | --- | --- |
| 完成保存 | App 將影像與結構化資料定稿為 `Ready`；平台依自己的排程執行備份。 | 顯示本機保存成功；不可宣稱已立即雲端備份。 |
| Cloud backup | 系統只依 allowlist 擷取指定資料；具體時間與帳戶行為由 Android 系統控制。 | 設定畫面顯示已允許備份，但不顯示無法驗證的備份成功。 |
| Device transfer | 系統只依 allowlist 傳輸指定資料。 | 還原後先執行完整性檢查，再顯示完成資料。 |
| 還原成功 | 資料庫、Ready 影像及 SHA-256 相符。 | 可正常查詢欄位與開啟對應原始影像。 |
| 還原不完整 | 有聯絡人但影像缺失／不符，或 Pending 記錄無法收斂。 | 保留能安全使用的欄位，標示影像缺失或需要重掃，不能靜默刪除。 |

## 4. 非功能與驗收補充

| ID | 要求 | 通過標準 |
| --- | --- | --- |
| NFR-09 | 備份最小化與可預測性 | 靜態檢查及裝置測試證明 cloud backup 和 device transfer 僅包含 allowlist 中的資料；`.pending/`、cache、日誌、OCR 原文及機密均不在內。 |
| NFR-10 | 還原完整性 | 在可支援的 Android 版本與目標裝置上，備份／還原後的 Ready 記錄可讀，影像 SHA-256 與 metadata 相符；不完整資料會顯示真實狀態。 |
| NFR-11 | 使用者可理解性 | 首次使用或設定頁清楚區分 Android Backup、Android Contacts 匯出、雲端 OCR 與 AI；使用者不會被誤導為資料自動送給 AI。 |
| NFR-12 | 容量與資料保護 | 面對 Android Backup 的每使用者容量限制或本機空間不足時，App 不靜默刪除完成資料，並能提供可理解的管理訊息。 |

## 5. 決策狀態

| ID | 狀態 | 結論 |
| --- | --- | --- |
| D-06-A | 已確認 | 原始名片影像與結構化聯絡人資料允許納入 Android Auto Backup 及裝置轉移。 |
| D-06-B | 待確認 | 是否採用 App 層資料庫／影像額外加密，以及金鑰生命週期。Android 平台保護不等於本產品已完成額外加密設計。 |
| D-07 | 待確認 | 影像保留期限及刪除聯絡人時是否同步刪除影像。 |
| D-08 | 待確認 | 原始影像最大解析度、檔案大小與壓縮策略；此項直接影響每位使用者的備份容量。 |

## 6. 參考資料

- [Android：Auto Backup 使用者資料](https://developer.android.com/identity/data/autobackup)
- [Android：資料備份概覽](https://developer.android.com/identity/data/backup)
- [Android：存取 App 專屬檔案](https://developer.android.com/training/data-storage/app-specific)
- [Android：以 Room 建立本機資料庫](https://developer.android.com/training/data-storage/room)
