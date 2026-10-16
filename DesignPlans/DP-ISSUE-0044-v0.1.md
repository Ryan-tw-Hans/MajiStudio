# 設計規劃書：Windows 端 Vercel 雲端一鍵部署精靈與 iPhone (iOS PWA) 行動版通訊錄介面 (ISSUE-0044)

- **對應 Issue ID**：`ISSUE-0044`
- **設計版本**：`v0.1`
- **設計工程師角色**：設計工程師 Agent（交由 PM Agent 統一與人類使用者確認）

---

## 1. 設計目標、目標使用者與使用情境

1. **設計目標**：
   - **Windows 端 (`MajiOCR.Windows`)**：讓從未接觸過 iOS 開發與雲端部署的使用者，能在 3 個直覺步驟內完成「註冊／登入 Vercel ➔ 貼上 Token ➔ 一鍵自動部署 iOS PWA 並掃描 QR Code 安裝至 iPhone」。
   - **iPhone 端 (`MajiOCR.Web` iOS PWA)**：針對 **iPhone X (375×812 pt, iOS 16+) 以上**之瀏海／動態島與底部 Home Indicator 安全區域 (`safe-area-inset`)，打造符合 iOS Human Interface Guidelines (HIG) 直覺體驗的行動名片通訊錄，支援單手快速搜尋、標籤篩選、檢視正反面名片原圖、一鍵撥號／寄信與雙管道資料載入。
2. **使用情境**：
   - **情境 A（電腦端首次部署）**：使用者在 Windows 電腦點擊「📱 部署 iOS / Web 雲端版 (Vercel)...」，跟隨畫面按鈕註冊 Vercel 並貼入 Token，點擊一鍵部署，5～10 秒後直接拿起 iPhone X 掃描電腦螢幕上的 QR Code 將 App 加入主畫面。
   - **情境 B（iPhone 載入與解密資料庫）**：使用者在 iPhone 主畫面打開 MajiOCR，選擇「從 Google Drive 下載備份」或「從手機檔案匯入 `.zip`」，若該備份受 `SQLCipher-AES256` 保護，彈出密碼框輸入主密碼完成解密並持久化於手機本機。
   - **情境 C（外出離線查詢與聯絡）**：使用者外出無網路時打開 iPhone MajiOCR，輸入空白分隔複合關鍵字或點選分類標籤，快速找到客戶名片、放大檢視名片正反面圖片，並直接點擊電話號碼撥號或匯出 vCard (`.vcf`)。

---

## 2. 視覺方向、色彩、字體、間距與元件規則

### 2.1 色彩系統（延續 MajiOCR 品牌專業商務藍與 iOS 原生質感）
* **主色調 (Primary Brand)**：`#0F6CBD`（MajiOCR 品牌藍，與 Windows WinUI 3 Accent 及 Android 主色呼應）
* **深色／淺色背景 (Surface & Background)**：
  - 預設採用清晰高對比之 **iOS 現代商務淺色主題**（支援跟隨系統深色模式 `prefers-color-scheme: dark`）：
  - 背景色：`#F2F4F8`（淺灰藍底色，凸顯純白名片卡片 `#FFFFFF`）
  - 頂部導覽與固定搜尋列：毛玻璃半透明效果 (`backdrop-filter: blur(16px)`, `rgba(255, 255, 255, 0.88)`)
* **語意與徽章色彩**：
  - 🔒 `已加密 (SQLCipher)` 徽章：背景 `#D97706`（琥珀橘 Amber）、文字 `#FFFFFF`
  - 🔓 `未加密` 徽章：背景 `#64748B`（石板灰 Slate）、文字 `#FFFFFF`
  - `WINDOWS` 平台徽章：`#2563EB` / `ANDROID` 平台徽章：`#0D9488`
  - 關鍵字搜尋高亮 (`<mark>`)：背景 `#FEF08A`、文字 `#854D0E`（高對比醒目黃底深棕字）
* **字體與排版**：
  - iOS 端優先採用 `-apple-system, BlinkMacSystemFont, "SF Pro Text", "PingFang TC", "Microsoft JhengHei", sans-serif`。
  - 最小可點擊區域（Touch Target）嚴格遵守 iOS HIG **至少 `44×44 px`**，搜尋輸入框字級設為 **`16px`**（防止 iOS Safari 點擊輸入框時自動強制放大畫面）。

---

## 3. 頁面流程、互動行為與狀態設計

### 3.1 Windows 端：`VercelDeployDialog`（Vercel 雲端一鍵部署精靈）
* **視窗結構**（寬度 `620px`，可捲動）：
  - **區塊 1：步驟一 — 註冊／登入 Vercel 並取得 Access Token**
    - 提供兩顆並排引導按鈕：
      - `[ 🌐 1. 前往 Vercel 註冊/登入 ]`（開啟 `https://vercel.com/signup`）
      - `[ 🔑 2. 前往建立 Access Token ]`（開啟 `https://vercel.com/account/tokens`）
    - 簡明提示：「於 Vercel 點擊 Create Token（Scope 選 Full Account），複製產生的 Token 貼入下方欄位。」
    - `PasswordBox` 輸入框：「Vercel Access Token *」（支援顯示／隱藏密碼）。
  - **區塊 2：步驟二 — 專案設定與 Google Drive 連線設定**
    - `TextBox`：「Vercel 專案名稱 *」（預設 `majiocr-ios-pwa`）。
    - `TextBox`：「Web 端 Google OAuth Client ID（選填，供 iPhone 直接登入 Google Drive；若留空仍可使用手機檔案匯入或於手機端設定）」。
    - 主行動按鈕：**`[ 🚀 一鍵導入並發布至 Vercel ]`**（Accent 樣式）。
  - **區塊 3：步驟三 — 部署進度與 iPhone 掃描安裝 QR Code（部署成功後展開）**
    - 狀態列：`ProgressRing` + 即時進度文字（「正在驗證 Vercel 帳號...」➔「正在打包並上傳內嵌 iOS PWA (共 N 個檔案)...」➔「✅ 部署成功！」）。
    - 顯示正式網址連結（例如 `https://majiocr-ios-pwa-xxxx.vercel.app`）＋ `[ 📋 複製網址 ]` 按鈕 ＋ `[ 🌐 在瀏覽器開啟 ]` 按鈕。
    - **本機動態產生之高解析度 QR Code 圖片 (`220×220 px`)**，搭配操作指引：「📱 請使用 iPhone X 相機掃描上方 QR Code ➔ 於 Safari 開啟 ➔ 點擊底部『分享』圖示 ➔ 選擇『加入主畫面』」。

### 3.2 iPhone 端：`MajiOCR.Web` (iOS PWA) 頁面與狀態
1. **頂部固定列（Header + 搜尋與篩選列）**：
   - 標題列：左側 MajiOCR Logo 與名稱 + 目前載入名片總數徽章；右側 **`[ ☁️ 載入/同步資料庫 ]`** 主按鈕。
   - 複合搜尋框：`🔍 搜尋姓名、公司、職稱、電話、標籤...`（支援多關鍵字空白分隔 AND 即時篩選 + 一鍵清除 `✕`）。
   - 分類標籤橫向捲動列（Tag Chips Bar）：`[ 全部 (N) ]`、`[ 未分類 ]` 及所有自訂分類標籤膠囊，點擊即可切換篩選；右側提供排序下拉選單（最新建立／姓名／公司）。
2. **通訊錄清單區（Contact Cards List）**：
   - 每張名片卡片顯示：
     - 左側：名片正面縮圖（若無圖片則顯示姓名首字圓形頭像）
     - 中間：姓名（粗體 `17px`）、職稱、公司名稱、主要電話與 Email、所屬分類標籤膠囊（支援關鍵字高亮）
     - 右側：快捷撥號按鈕 `📞` 與進入詳情箭頭 `›`
3. **名片詳情抽屜／彈窗（Contact Detail Sheet）**：
   - 頂部顯示名片正／反面原圖檢視器（支援切換正面／反面、點擊全螢幕放大檢視）。
   - 快捷行動列：`[ 📞 撥打電話 ]`、`[ ✉️ 發送郵件 ]`、`[ 📍 地圖導航 ]`、`[ 📇 儲存至 iPhone 聯絡人 (.vcf) ]`。
   - 完整欄位清單（所有電話、Email、地址、統編、部門、網站、備註、自訂欄位），每列右側提供 `[ 複製 ]` 按鈕。
4. **資料載入抽屜（Data Import & Cloud Sync Modal）**：
   - **分頁／卡片 ①：📂 從 iPhone「檔案」匯入備份（免設 OAuth 最速管道）**
     - 點擊按鈕喚起 iOS 檔案選擇器，支援選取 `MajiOCR_Backup_*.zip` 或 `contacts.sqlite3`。
   - **分頁／卡片 ②：☁️ 從 Google Drive 雲端下載備份**
     - 支援 Google OAuth 登入（或輸入 Client ID / Access Token），自動列出 `MajiOCR_Backups` 雲端備份清單（清楚標示 `🔒 已加密 (SQLCipher)` 或 `🔓 未加密`、平台、名片數、圖片數與日期），點擊「下載並還原」即可載入。
5. **SQLCipher 主密碼解鎖對話框（Password Unlock Modal）**：
   - 當偵測到載入之資料庫為 `SQLCipher-AES256` 加密時自動彈出。
   - 內含密碼輸入框（支援顯示／隱藏密碼、Enter 提交）、解密進度提示，以及密碼錯誤時的紅色搖晃警示訊息。
6. **空值／載入／錯誤／離線狀態**：
   - **首次開啟空狀態**：顯示清楚的歡迎引導卡片，提供「📂 從手機檔案匯入備份包」與「☁️ 從 Google Drive 下載備份」兩顆大按鈕，以及「載入內建範例名片」預覽按鈕。
   - **離線狀態**：頂部顯示輕量「📡 離線模式（已載入本機資料庫）」提示，所有本機查詢與名片圖片檢視 100% 正常運作。

---

## 4. 響應式規則、效能與無障礙要求

* **響應式版面 (RWD)**：
  - **手機直向 (`< 640px`，含 iPhone X `375px` 寬度)**：單欄卡片清單 + 底部滑出式詳情頁 (Bottom Sheet)，完美貼合大拇指單手操作，底部預留 `env(safe-area-inset-bottom)`。
  - **平板／橫向或桌面瀏覽器 (`>= 768px`)**：自動切換為左欄通訊錄清單 + 右欄名片詳情雙欄並排檢視。
* **效能與安全性考量**：
  - SQLCipher PBKDF2 (256,000 次迭代) 與 AES-256-CBC 解密全程使用瀏覽器原生硬體加速之 `window.crypto.subtle` 非同步執行，不阻塞 UI 執行緒。
  - 名片圖片採用 `loading="lazy"` 與 ObjectURL / Base64 快取管理，避免大量高解析度圖片造成 iOS Safari 記憶體溢位。

---

## 5. 設計方案取捨與待人類確認事項

| 設計議題 | 推薦方案 (方案 A) | 替代方案 (方案 B) | 取捨原因 |
| :--- | :--- | :--- | :--- |
| **iPhone PWA 詳情頁呈現方式** | **底部滑出式全高抽屜 (iOS Sheet Modal)** | 切換網址跳頁 | 保持搜尋關鍵字與捲動位置不遺失，關閉詳情即可瞬間回到原篩選清單 |
| **Windows 部署成功後的安裝引導** | **本機自動產生 QR Code + 網址複製按鈕** | 僅顯示文字網址 | iPhone X 相機直接掃描電腦螢幕 QR Code 即可開啟 Safari，免去在手機上手動打字輸入長網址 |

---

## 6. UI 驗收條件（供測試工程師驗證）

* **UI-AC-01**：`MajiOCR.Windows` 主選單與已存名片清單選單均可開啟「📱 部署 iOS / Web 雲端版 (Vercel)...」對話框，且具備註冊／Token 連結、輸入欄位、一鍵部署按鈕與 QR Code 顯示區。
* **UI-AC-02**：`MajiOCR.Web` 在 `375×812` (iPhone X) 視窗尺寸下無水平捲軸破版，搜尋框字級 `>= 16px`，具備 PWA Manifest 與 iOS Safe Area 適配。
* **UI-AC-03**：`MajiOCR.Web` 具備「從 Google Drive 下載」與「從手機檔案匯入 (.zip/.sqlite3)」雙入口，且載入加密備份時會彈出主密碼輸入框並正確顯示加密／未加密徽章。
* **UI-AC-04**：`MajiOCR.Web` 通訊錄清單支援複合關鍵字搜尋高亮、分類標籤篩選、名片正反面圖片放大檢視，以及 `tel:` / `mailto:` / `.vcf` 匯出操作。
