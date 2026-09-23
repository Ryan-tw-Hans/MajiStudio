# 架構評估書：ISSUE-0001 Android 名片辨識與聯絡人建立系統

| 項目 | 內容 |
| --- | --- |
| 評估版本 | 0.2.0-draft |
| 文件狀態 | 有條件採用；未授權實作 |
| Issue／需求文件 | ISSUE-0001；Requirements/RS-ISSUE-0001-v0.2.md |
| 更新日期 | 2026-09-23，UTC+08:00 |
| 取代關係 | 本文件補充並取代 Architecture/AR-ISSUE-0001.md 的 0.1.0-draft；舊稿保留作歷程。 |
| SA 執行者／審查方式 | Codex；同一 Agent 的架構自查，非獨立審查 |
| 關聯互動設計 | DesignPlans/DP-ISSUE-0001.md |
| 關聯資料庫評估 | DB/DBA-ISSUE-0001.md |
| 架構圖 | Architecture/diagrams/AR-ISSUE-0001-system-architecture.html |

## 1. 架構影響判定

這是新 Android 系統，且新增下列核心邊界：

- CameraX 預覽與對焦狀態驅動的即時影格分析。
- 本機離線 OCR、候選文字版本、欄位鎖定與重新掃描狀態機。
- 完成後存入 App 私有 SQLite，供後續查詢系統使用。
- 可選雲端 OCR、AI 與 Android Contacts 匯出。
- 名片、聯絡資訊、供應商 API 憑證與本機資料庫的隱私／信任邊界。

因此 SA、設計工程師、DBA、系統整合與測試工程師皆適用。沒有任何 Android 程式、Gradle 專案、雲端服務、API 金鑰或發佈輸出已存在。

## 2. 已確認事實與待驗證假設

| 資訊 | 分類 | 架構影響 |
| --- | --- | --- |
| 離線 OCR 預設可用；雲端與 AI 要逐次同意 | 已確認 | 裝置端是主流程；雲端只能是明確選擇的分支。 |
| 對焦可用後開始即時 OCR | 已確認 | Camera Preview、Focus Gate 與單一 in-flight 分析工作須協作。 |
| 點選文字後不再更新，再點一次才重掃 | 已確認 | 每個候選需要獨立狀態、世代號與不可覆寫規則。 |
| 鎖定後可用下拉選單改欄位名稱 | 已確認 | 欄位值與欄位類型必須分離；重分類不應觸發 OCR。 |
| 完成資訊存入本機 SQLite 供後續查詢 | 已確認 | SQLite 是 App 的完成資料來源；須有 transaction、schema version 與查詢索引。 |
| 相機對焦事件在所有 Android 裝置都一致 | 未知 | 不以單一硬體 callback 作為唯一入口；必須提供穩定預覽與手動掃描退路。 |
| 名片影像／完整 OCR 原文要永久保存 | 未知 | 本版預設不保存，以降低敏感資料範圍。 |
| 資料庫加密與 Auto Backup 策略 | 未知 | 是實作前安全決策，不得暗自假定。 |

## 3. 建議提案架構

推薦「離線優先 Android App + App 私有 Room/SQLite + 可選 Provider Gateway」。

### 3.1 裝置端主要資料流

1. CameraX Preview 顯示名片與對焦狀態。
2. Focus Gate 確認相機處於可分析狀態後，將最新影格交給 Live OCR Orchestrator。
3. Orchestrator 只允許一個 OCR 分析工作，採最新影格策略；每個完成結果標示 Scan Generation。
4. Field Mapping 將候選文字對應到欄位；只有與目前 Generation 相符且未 Locked 的候選可更新 UI。
5. Draft & Lock Controller 處理使用者的鎖定、重掃、重分類與衝突選擇。
6. 使用者按完成後，Repository 以單一 Room 交易寫入結構化 Contact Record 和多筆子欄位。
7. 本機 Query Module 只透過 Repository／DAO 查詢已完成資料。
8. Android Contacts 匯出是另一個可取消動作，狀態回寫到本機記錄但不改變其已完成內容。

### 3.2 可選外部資料流

使用者選擇雲端 OCR 或 AI 時，App 把本次同意的影像或文字送至 Provider Gateway。Gateway 驗證 App 工作階段、施加供應商 allowlist、保存服務端 API key、執行費用／配額控制，再呼叫已核准的 OCR、OpenAI 或 Gemini 服務。回傳資料仍先進 Draft & Lock Controller，絕不直接覆寫 Locked 項目或直接存入 SQLite。

### 3.3 元件責任

| 元件 | 責任 | 關鍵契約與失敗行為 |
| --- | --- | --- |
| CameraX Preview + Focus Gate | 顯示畫面、品質提示、決定何時可送影格分析。 | 無可靠焦點或權限時，保留相簿、手動掃描與手動輸入。 |
| Live OCR Orchestrator | 以離線模型分析最新影格，管理單一 in-flight 工作與 Scan Generation。 | 晚到結果、已關閉影格、過期 Generation 一律丟棄。 |
| Field Mapping | 將 OCR 字串和位置映射到候選欄位。 | 不能確定時保留未分類候選，不偽造欄位。 |
| Draft & Lock Controller | 實作 Live、Locked、RescanRequested、Completed，並處理重分類衝突。 | 不允許任何背景流程覆寫 Locked 值。 |
| Contact Repository | 封裝 Room DAO、交易、寫入／查詢與 schema migration。 | 寫入失敗時回報，禁止半筆完成資料。 |
| Room / SQLite | 儲存完成的結構化聯絡人與多筆子欄位。 | 位於 App 私有儲存；原始影像與完整 OCR 原文不預設保存。 |
| Contacts Export Adapter | 開啟 Android 系統聯絡人編輯畫面並更新匯出結果。 | 取消或失敗不刪除本機完成記錄。 |
| Provider Gateway | 管理外部服務憑證、供應商路由與用量控制。 | 未同意、逾時、配額不足或服務拒絕時，不自動切換供應商。 |

## 4. 即時 OCR 的一致性與效能設計

CameraX 的 ImageAnalysis 應採用只保留最新影格的背壓策略，且每次分析完成後釋放 ImageProxy。這個策略避免 OCR 慢於相機時累積舊影格並拖慢預覽。

即時結果必須帶有以下運行期資訊，但只在記憶體保留：

| 欄位 | 用途 |
| --- | --- |
| scanGeneration | 避免較早完成的 OCR 結果覆寫後來畫面。 |
| candidateId | 對應畫面上單一候選文字與它的欄位狀態。 |
| lockState | Live、Locked 或 RescanRequested。 |
| sourceBounds | 讓使用者看見候選來自名片的哪個區域。 |
| sourceMode | Offline OCR、Cloud OCR、AI 或 Manual。 |

使用者鎖定後，Controller 以 candidateId 和 lockState 拒絕後續更新。使用者重掃時只產生新的候選 Generation；在新值被接受前，舊值仍保留在草稿，避免因模糊影格造成資料消失。

## 5. 資料與信任邊界

| 邊界 | 允許資料 | 不允許事項 |
| --- | --- | --- |
| App 即時記憶體 | 預覽影格、OCR 候選、鎖定狀態、未完成草稿。 | 將未同意資料送出裝置。 |
| App 私有 SQLite | 已完成的結構化聯絡人、資料來源、時間與 Contacts 匯出狀態。 | 名片影像、完整原始 OCR 原文、共享 API key。 |
| App 到 Gateway | 本次被同意的資料及短效 App 工作階段憑證。 | ChatGPT Cookie、登入 token、OpenAI/Gemini 長期 API key。 |
| Gateway 到供應商 | 已核准的影像或文字與最小結構化要求。 | 任意使用者輸入的 URL、未選定供應商、未受控資料轉送。 |
| App 到 Android Contacts | 使用者確認的欄位草稿。 | 背景直接寫入或自動合併既有聯絡人。 |

## 6. SQLite 與查詢系統建議

使用 Android Room 做 SQLite 存取層。Room 仍以 SQLite 為底層，但提供 DAO、編譯期 SQL 檢查與較可追蹤的 migration。完整資料模型、索引、交易與復原要求由 DB/DBA-ISSUE-0001.md 定義。

本機 SQLite 只作 App 內的正式完成資料來源。未來若要建立查詢 UI、分享資料、同步或公開 API，必須另立需求；不得以直接讀取 SQLite 檔案取代 App 的 Repository／DAO 權限與查詢邊界。

## 7. 方案比較

| 面向 | A：離線 OCR + SQLite | B：離線優先 + SQLite + Gateway（推薦） | C：APK 直接內嵌供應商 API key |
| --- | --- | --- | --- |
| 符合已確認需求 | 滿足即時 OCR、鎖定與本機查詢；不含雲端／AI。 | 同時滿足本機主流程與使用者主動選擇的雲端／AI。 | 功能表面可行，但不安全。 |
| 隱私與費用 | 最佳，無離機資料。 | 使用者可控，服務端可做金鑰、額度與稽核管理。 | API key 可被擷取，會造成未授權費用與資料風險。 |
| 複雜度 | 最低。 | 中等，需要 Gateway、登入與維運規劃。 | 初期低，但屬不可接受的生產風險。 |
| 建議 | 作為可驗證 MVP 的第一個里程碑。 | 符合完整產品目標的推薦架構。 | 不採用。 |

## 8. 風險、回復與驗證

| 情境 | 緩解方式 | 驗證 |
| --- | --- | --- |
| OCR 比相機慢 | 僅保留最新影格、單一工作、釋放 ImageProxy。 | 低階目標裝置長時間預覽測試。 |
| 晚到結果覆寫欄位 | Generation + Lock State 守衛。 | 模擬延遲結果與快速點選／重掃。 |
| 誤判欄位 | 鎖定後下拉重分類、手動編輯、衝突確認。 | 多語言名片與重複欄位測試。 |
| SQLite 寫入中斷 | 單一 Room 交易與錯誤提示。 | 強制例外後確認沒有半筆記錄。 |
| Contacts 匯出取消 | 將 Contacts 匯出視為可選 side effect，本機記錄保留。 | 系統聯絡人畫面取消與完成兩種測試。 |
| 雲端故障或超支 | 使用者明確選擇、Gateway 用量控制、保留本機草稿。 | 逾時、拒絕、配額與離線測試。 |

## 9. 圖示與結論

已依使用者同意採用預設淺色系產出自包含架構圖：Architecture/diagrams/AR-ISSUE-0001-system-architecture.html。圖示保留八個關鍵節點；細緻的候選狀態機、資料表欄位與供應商內部細節保留在本文件、互動設計與 DBA 評估，避免架構圖超出可讀性預算。

SA 結論為「有條件採用」：推薦離線優先、CameraX 最新影格即時 OCR、候選鎖定狀態機、Room/SQLite 完成資料庫，以及可選 Gateway 的組合。實作前仍必須確認語言範圍、影像／原文保存、雲端供應商策略、最低 Android 版本與資料庫加密／備份策略。

## 10. 參考資料

- [ML Kit Android Text Recognition](https://developers.google.com/ml-kit/vision/text-recognition/v2/android)
- [CameraX Image analysis](https://developer.android.com/media/camera/camerax/analyze)
- [Android：以 SQLite 儲存資料](https://developer.android.com/training/data-storage/sqlite)
- [Android：以 Room 建立本機資料庫](https://developer.android.com/training/data-storage/room)
- [Android Contacts Provider：以 Intent 修改聯絡人](https://developer.android.com/identity/providers/contacts-provider/modify-data)
