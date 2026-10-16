# MajiOCR.Windows 0.1.0-beta.24 (支援 iOS / Web 雲端版 Vercel 一鍵部署精靈與 SQLCipher 4 跨平台解密相容驗證)

目前為本機發布版，更新日期為 2026-10-16（Asia/Taipei）。
直接執行本資料夾的 `MajiOCR.Windows.exe`。維持單一免安裝、自含執行檔（Self-Contained Single-File Executable）及離線 OCR 模型，並內嵌完整 `MajiOCR.Web` (`0.1.0-beta.1`) 靜態 PWA 資產包。

### 本版更新重點
1. **新增「📱 部署 iOS / Web 雲端版 (Vercel)...」一鍵部署精靈 (`VercelDeployDialog` & `VercelDeploymentService`)**：
   - 於主畫面右上角更多功能選單（`...`）與「已存名片」管理面板頂部新增部署入口。
   - 提供三步驟互動引導：一鍵開啟瀏覽器前往 Vercel 免費註冊／登入並建立 Access Token、自動驗證帳號並透過 Vercel REST API (`POST /v13/deployments`) 直推內嵌之 10 項 `MajiOCR.Web` 靜態 PWA 檔案、自動解除 Vercel SSO 存取牆 (`PATCH /v9/projects/{id}`)，並即時產生本機 QR Code 供 iPhone X (iOS 16+) Safari 掃描與「加入主畫面」。
   - 嚴格保障隱私安全：僅上傳 `MajiOCR.Web` 純靜態前端程式碼，本機 `contacts.sqlite3` 與名片圖片絕不隨部署上傳至 Vercel。
2. **跨平台 SQLCipher 4 (`SQLCipher-AES256`) 分頁解密器與驗證套件 (`SqlCipher4Decryptor`)**：
   - 實作與 iOS PWA WebCrypto 完全對等之 SQLCipher 4 頁面解密與 HMAC-SHA512 驗證器（4096-byte page、80-byte reserve、PBKDF2-HMAC-SHA512 256,000 次迭代）。
   - 新增 `--test-issue-0044` 自動化驗證套件（T1~T6 全數通過）。
3. **版本號與更新紀錄（Change Log）顯示強化**：
   - 主視窗標題列新增顯示目前軟體版本號 `MajiOCR.Windows 0.1.0-beta.24 (v0.1.0.24)`。
   - 「關於 MajiOCR.Windows」對話框改為動態讀取最新軟體版本號並內建可捲動的完整更新紀錄（`CHANGELOG.txt`）。
   - 「檢查軟體更新」對話框預設展開「📋 更新內容與變更紀錄 (Change Log)」，並修正 `UpdateService.LoadLocalChangeLogText()` 優先讀取內嵌於單一 `.exe` 的最新 `MajiOCR.CHANGELOG.txt` 資源。

---
- **FileVersion**：0.1.0.24
- **SHA-256**：D7C870496777BC6D7611932855BCB33DCE6725C2EF318FA84856AF4F5BE5FD81
- **檔案大小**：298,808,827 位元組 (~285.0 MB)

本機隔離啟動、備份/還原/加密安全測試（`ISSUE-0041` 9/9 PASS）、自訂欄位與自動更新測試（`ISSUE-0043` 5/5 PASS）及 iOS PWA / Vercel 部署與 SQLCipher 解密測試（`ISSUE-0044` 6/6 PASS）均已全數通過。
