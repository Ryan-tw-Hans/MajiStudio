# 架構評估：ISSUE-0001 Android 名片即時辨識、影像保存與本機聯絡人 v0.3

| 項目 | 內容 |
| --- | --- |
| Issue ID | ISSUE-0001 |
| 文件狀態 | SA 草案，待人類確認安全與保存生命週期後才可進入實作 |
| 版本 | 0.3.0-draft |
| 更新日期 | 2026-09-23 |
| 本版變更 | 使用者已確認原始名片影像與結構化聯絡人資料皆需保存。 |
| 前版關係 | 本文件取代 `Architecture/AR-ISSUE-0001-v0.2.md` 作為目前架構草案；v0.1 與 v0.2 保留為歷史。 |

## 1. 架構影響判定

這是新 Android 系統，且本次已確認的影像保存需求新增下列核心邊界：

- CameraX 預覽與對焦狀態驅動的即時影格分析，和高解析原始影像保存必須分開處理。
- 本機離線 OCR、候選文字版本、欄位鎖定與重新掃描狀態機。
- 原始名片影像的 App 私有檔案資產、Room / SQLite metadata 與結構化聯絡人資料。
- 檔案與 SQLite 兩個持久層之間的定稿、啟動復原與查詢可見性規則。
- 可選雲端 OCR、AI 與 Android Contacts 匯出，但這些不能因本機保存需求而自動取得資料。

因此 SA、設計工程師、DBA、系統整合與測試工程師皆適用。沒有任何 Android 程式、Gradle 專案、雲端服務、API 金鑰或發佈輸出已存在。

## 2. 已確認事項、假設與待決事項

| 資訊 | 分類 | 架構影響 |
| --- | --- | --- |
| 離線 OCR 預設可用；雲端與 AI 要逐次同意 | 已確認 | 裝置端是主流程；雲端只能是明確選擇的分支。 |
| 對焦可用後開始即時 OCR | 已確認 | Camera Preview、Focus Gate 與單一 in-flight 分析工作須協作。 |
| 點選文字後不再更新，再點一次才重掃 | 已確認 | 每個候選需要獨立狀態、世代號與不可覆寫規則。 |
| 鎖定後可用下拉選單改欄位名稱 | 已確認 | 欄位值與欄位類型必須分離；重分類不應觸發 OCR。 |
| 結構化聯絡人資訊要保存到本機 SQLite | 已確認 | Room 是正式結構化資料來源；須有 transaction、schema version 與查詢索引。 |
| 原始名片影像也要保存 | 已確認 | 以 App 私有持久檔案保存原始位元資料；Room 保存資產 metadata、關聯及完成狀態。 |
| 完整原始 OCR 文字要保存 | 未確認，預設否 | 保存影像不會連帶保存完整 OCR 全文或所有中間影格。 |
| 影像保存位置 | 架構提案 | 使用 `filesDir/card-images/`，不使用相簿、共享儲存或可移除外部儲存。 |
| 資料庫加密與 Auto Backup 策略 | 未知 | 是實作前安全決策，不得暗自假定。 |
| 影像保留期限與刪除行為 | 未知 | 影響容量、刪除、復原與使用者告知。 |

## 3. 建議提案架構

推薦「離線優先 Android App + App 私有原始影像資產 + App 私有 Room/SQLite + 可選 Provider Gateway」。

### 3.1 裝置端主要資料流

1. CameraX Preview 顯示名片與對焦狀態；即時 OCR 只取得可分析的最新預覽影格。
2. Focus Gate 確認相機處於可分析狀態後，將最新影格交給 Live OCR Orchestrator。
3. Orchestrator 只允許一個 OCR 分析工作，採最新影格策略；每個完成結果標示 Scan Generation。
4. Field Mapping 將候選文字對應到欄位；只有與目前 Generation 相符且未 Locked 的候選可更新 UI。
5. Draft & Lock Controller / Save Coordinator 處理使用者的鎖定、重掃、重分類、衝突選擇與完成保存協調。
6. 使用者按完成後，Coordinator 取得相機完成拍攝輸出或選取影像的私有副本，先寫到 App 私有 staging 區，再建立 Room 的 Pending 記錄。
7. Coordinator 將 staging 原始影像定稿至 `filesDir/card-images/`，並把聯絡人、欄位和影像資產狀態一同收斂為 `Ready`。
8. 本機 Query Module 只透過 Repository／DAO 讀取 Ready 的完成資料，並透過受控 Image Asset Service 載入同筆原始影像。
9. Android Contacts 匯出是另一個可取消動作，狀態回寫到本機記錄但不改變其已保存的影像與欄位資料。

### 3.2 可選外部資料流

使用者選擇雲端 OCR 或 AI 時，App 把本次同意的影像或文字送至 Provider Gateway。Gateway 驗證 App 工作階段、施加供應商 allowlist、保存服務端 API key、執行費用／配額控制，再呼叫已核准的 OCR、OpenAI 或 Gemini 服務。回傳資料仍先進 Draft & Lock Controller，絕不直接覆寫 Locked 項目或直接存入 SQLite。

「影像已保存在本機」不等於可把該影像傳給供應商；每次外送仍須取到相同類型的明確同意。

### 3.3 元件責任

| 元件 | 責任 | 關鍵契約與失敗行為 |
| --- | --- | --- |
| CameraX Preview + Focus Gate | 顯示畫面、品質提示、決定何時可送影格分析與取得完成拍攝影像。 | 無可靠焦點或權限時，保留相簿、手動掃描與手動輸入。 |
| Live OCR Orchestrator | 以離線模型分析最新影格，管理單一 in-flight 工作與 Scan Generation。 | 晚到結果、已關閉影格、過期 Generation 一律丟棄。 |
| Field Mapping | 將 OCR 字串和位置映射到候選欄位。 | 不能確定時保留未分類候選，不偽造欄位。 |
| Draft & Lock Controller / Save Coordinator | 實作 Live、Locked、RescanRequested、Saving、Completed，並協調原始影像與結構化資料定稿。 | 不允許背景流程覆寫 Locked 值；不得在兩種資料都 Ready 前回報成功。 |
| Image Asset Service | 在私有 staging 和最終影像目錄間寫入、驗證、改名、讀取與受控刪除原始影像。 | 只接受程式產生的相對路徑；不得使用外部 URI 或未驗證檔名。 |
| Contact Repository | 封裝 Room DAO、交易、影像 metadata、查詢、migration 與啟動復原。 | 寫入失敗時回報，禁止一般查詢讀到半筆完成資料。 |
| Room / SQLite | 儲存完成的結構化聯絡人、多筆子欄位、影像 metadata 和匯出狀態。 | 位於 App 私有範圍；不保存完整影像 BLOB、完整 OCR 原文或共享 API key。 |
| Local Query Module | 以 Repository／DAO 顯示 Ready 的聯絡人與受控影像詳情。 | 不開放資料庫檔、任意 SQL、任意路徑或完整 dump。 |
| Contacts Export Adapter | 開啟 Android 系統聯絡人編輯畫面並更新匯出結果。 | 取消或失敗不刪除本機完成記錄。 |
| Provider Gateway | 管理外部服務憑證、供應商路由與用量控制。 | 未同意、逾時、配額不足或服務拒絕時，不自動切換供應商。 |

## 4. 即時 OCR 的一致性與效能設計

CameraX 的 ImageAnalysis 應採用只保留最新影格的背壓策略，且每次分析完成後釋放 ImageProxy。這個策略避免 OCR 慢於相機時累積舊影格並拖慢預覽。

即時結果必須帶有下列運行期資訊，但只在記憶體保留：

| 欄位 | 用途 |
| --- | --- |
| scanGeneration | 避免較早完成的 OCR 結果覆寫後來畫面。 |
| candidateId | 對應畫面上單一候選文字與它的欄位狀態。 |
| lockState | Live、Locked 或 RescanRequested。 |
| sourceBounds | 讓使用者看見候選來自名片的哪個區域。 |
| sourceMode | Offline OCR、Cloud OCR、AI 或 Manual。 |

使用者鎖定後，Controller 以 candidateId 和 lockState 拒絕後續更新。使用者重掃時只產生新的候選 Generation；在新值被接受前，舊值仍保留在草稿，避免因模糊影格造成資料消失。

預覽影格與原始保存影像必須分離：前者是頻繁、低延遲、可丟棄的工作資料；後者是使用者按下完成後才建立的持久資產。

## 5. 原始影像與 SQLite 的完成契約

### 5.1 資料模型和儲存位置

- 原始影像位於 App 私有內部 `filesDir/card-images/{opaque-image-id}`。
- `card_image_assets` 以 `contact_id` 關聯 `contact_records`，保存相對路徑、MIME、大小、尺寸、SHA-256、來源與 storage state。
- `contact_values` 保持結構化欄位的一對多關係；完整原始 OCR 全文不持久化。
- 資料庫內不得存放任意絕對檔案路徑、外部 URI 或使用者輸入的檔名。

### 5.2 Pending → Ready 的定稿流程

由於檔案系統與 SQLite 沒有跨資源原子交易，採取以下可恢復流程：

1. 寫入 `.pending/` 原始影像並驗證 metadata 與 SHA-256。
2. 在一個 Room 交易中寫入 `contact_records`、`contact_values` 與 `card_image_assets`，狀態為 `PendingFinalize`。
3. 在同一私有檔案系統內把 staging 檔原子改名至最終位置。
4. 在短交易中把影像及聯絡人標為 `Ready`，此時才讓一般查詢和成功 UI 可見。
5. 每次 App 啟動先執行 Recovery：完成可驗證的 Pending 資產、隔離無法驗證的資產，或標記遺失影像；不得靜默覆寫或擴大刪除範圍。

此流程的目標不是讓兩個儲存機制假裝是同一交易，而是確保使用者與查詢模組只把兩者都完成的資料視為正式完成。

## 6. 信任與資料邊界

| 邊界 | 允許資料 | 不允許事項 |
| --- | --- | --- |
| App 即時記憶體 | 預覽影格、OCR 候選、鎖定狀態、未完成草稿。 | 將未同意資料送出裝置。 |
| App 私有影像檔案 | 使用者完成後的原始名片影像、staging 檔與驗證資訊。 | 相簿共享、外部共享儲存、使用可猜測個資檔名。 |
| App 私有 SQLite | 已完成的結構化聯絡人、影像 metadata、資料來源、時間與 Contacts 匯出狀態。 | 完整原始 OCR 原文、完整影像 BLOB、共享 API key。 |
| App 到 Gateway | 本次被同意的資料及短效 App 工作階段憑證。 | ChatGPT Cookie、登入 token、OpenAI／Gemini 長期 API key。 |
| Gateway 到供應商 | 已核准的影像或文字與最小結構化要求。 | 任意使用者輸入的 URL、未選定供應商、未受控資料轉送。 |
| App 到 Android Contacts | 使用者確認的欄位草稿。 | 背景直接寫入或自動合併既有聯絡人。 |

## 7. SQLite、查詢與備份建議

使用 Android Room 做 SQLite 存取層。Room 仍以 SQLite 為底層，但提供 DAO、編譯期 SQL 檢查與較可追蹤的 migration。完整資料模型、索引、交易與復原要求由 `DB/DBA-ISSUE-0001-v0.2.md` 定義。

本機 SQLite 是完成資料的索引與結構化來源，原始影像位元資料則由私有 Image Asset Service 管理。未來若要建立查詢 UI、分享資料、同步或公開 API，必須另立需求；不得以直接讀取 SQLite 檔案或開放影像目錄取代 Repository／DAO／Asset Service 的權限與查詢邊界。

Android Auto Backup 預設會涵蓋 App 內部檔案與資料庫的範圍，且原始影像可能快速消耗備份額度。因此 D-06 未確認前，本版只承諾本機保存，不承諾跨裝置、雲端或卸載後復原。實作時必須明確設定備份 include／exclude 規則，不能依賴平台預設值。

## 8. 風險、回復與驗證

| 情境 | 緩解方式 | 驗證 |
| --- | --- | --- |
| OCR 比相機慢 | 僅保留最新影格、單一工作、釋放 ImageProxy。 | 低階目標裝置長時間預覽測試。 |
| 晚到結果覆寫欄位 | Generation + Lock State 守衛。 | 模擬延遲結果與快速點選／重掃。 |
| 誤判欄位 | 鎖定後下拉重分類、手動編輯、衝突確認。 | 多語言名片與重複欄位測試。 |
| 檔案與 SQLite 定稿中斷 | staging、PendingFinalize、Ready 與啟動 Recovery。 | 在每一步注入中斷，確認正常查詢不見半筆資料。 |
| 原始影像損壞或消失 | SHA-256、Missing 狀態、保留結構化聯絡人和明確錯誤。 | 竄改、刪除影像後重新啟動與查詢。 |
| 裝置空間不足 | 保存前檢查可用空間，顯示可理解錯誤並保留草稿。 | 低儲存空間模擬。 |
| App 卸載或 Auto Backup | 在 UI／說明中界定本機保存，不假稱跨卸載保留；D-06 決定 backup include／exclude。 | 卸載、雲端備份、D2D 測試在政策確認後執行。 |
| Contacts 匯出取消 | 將 Contacts 匯出視為可選 side effect，本機影像和聯絡人仍保留。 | 系統聯絡人畫面取消與完成兩種測試。 |
| 雲端故障或超支 | 使用者明確選擇、Gateway 用量控制、保留本機草稿。 | 逾時、拒絕、配額與離線測試。 |

## 9. 架構圖與結論

本版的自包含架構圖為 `Architecture/diagrams/AR-ISSUE-0001-system-architecture-v0.3.html`。圖示維持九個關鍵節點，將原始影像資產放入 App 私有區，並把詳細的 Pending／Ready 狀態、表欄位和 Recovery 規則留在本文件與 DBA 評估，以維持總覽可讀性。

SA 結論為「有條件採用」：推薦離線優先、CameraX 最新影格即時 OCR、候選鎖定狀態機、App 私有原始影像資產、Room/SQLite metadata 與結構化聯絡人，以及可選 Gateway 的組合。D-02 已由使用者確認為「保存原始影像與結構化資料」；實作前仍必須確認語言範圍、雲端供應商策略、最低 Android 版本、資料庫加密／備份，以及影像保存與刪除生命週期。

## 10. 參考資料

- [ML Kit Android Text Recognition](https://developers.google.com/ml-kit/vision/text-recognition/v2/android)
- [CameraX Image analysis](https://developer.android.com/media/camera/camerax/analyze)
- [Android：存取 App 專屬檔案](https://developer.android.com/training/data-storage/app-specific)
- [Android：資料與檔案儲存概覽](https://developer.android.com/training/data-storage)
- [Android：以 Room 建立本機資料庫](https://developer.android.com/training/data-storage/room)
- [Android：Auto Backup 使用者資料](https://developer.android.com/identity/data/autobackup)
- [Android Contacts Provider：以 Intent 修改聯絡人](https://developer.android.com/identity/providers/contacts-provider/modify-data)
