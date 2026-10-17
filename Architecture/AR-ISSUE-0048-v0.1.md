# 架構評估書：Windows 端行動版設定對話框排版與渲染管線重構 (ISSUE-0048)

* **文件編號**：AR-ISSUE-0048-v0.1
* **關聯 Issue**：[`ISSUE-0048`](../IssueLog/IssueLog.sqlite3)
* **架構影響判定**：**無實質架構影響（UI 層佈局重構）**
* **版本**：v0.1
* **建立日期**：2026-10-17

---

## 1. 現況與問題根因分析 (Technical Root Cause)

### 1.1 雙層 ScrollViewer 嵌套引發之 Composition 錯位
在 WinUI 3 (Windows App SDK) 的 `ContentDialog` 控制項模板中，其內部結構預設包含 `ContentScrollViewer`：
```xml
<Grid x:Name="DialogSpace">
    ...
    <ScrollViewer x:Name="ContentScrollViewer">
        <ContentPresenter Content="{TemplateBinding Content}" />
    </ScrollViewer>
    ...
</Grid>
```
在原本的 `VercelDeployDialog.cs` 中，`dialog.Content` 被賦值為自建的 `ScrollViewer`：
```csharp
var scrollViewer = new ScrollViewer { MaxHeight = 620, Content = rootStack };
dialog.Content = scrollViewer;
```
這導致了兩層 ScrollViewer 的垂直 Measure 與 DirectComposition 排版重疊。當內部含有動態切換 Visibility 的容器（如 `ghpPanel` 與 `vercelPanel`）時，外層與內層的 Arrange 週期無法正確同步，引發文字圖層與控制項的原地疊加與殘影問題。

### 1.2 寬度硬編碼與 ContentDialog 寬度約束衝突
- WinUI 3 原生 `ContentDialog` 具有預設的最大寬度限制（`ContentDialogMaxWidth` 約為 548px）。
- 原程式碼設定 `rootStack.Width = 560`，導致元素溢出、內部兩欄按鈕水平排列空間不足，第二個按鈕被迫裁切省略號。

---

## 2. 建議技術方案與架構約定

1. **單一 ScrollViewer 責任歸屬**：
   - 移除內部自建的 `ScrollViewer`，或若需保留自訂捲動行為，將 `ContentDialog` 的直接內容置於單一責任容器中，顯式設定 `dialog.MaxWidth = 720`，內容寬度使用 `HorizontalAlignment.Stretch`。
2. **模式切換列改為 Grid 均分**：
   - 使用 1 列 2 欄的 `Grid`，寬度為 `1*` 與 `1*`，按鈕設為 `HorizontalAlignment.Stretch`，確保兩大模式按鈕平分寬度、整齊劃一。
3. **表單欄位解耦**：
   - 將標題、副標說明與輸入框以 `StackPanel` 垂直分離，避免依賴 `TextBox.Header` 承載過長的多行說明文字，消除 Measure 延遲導致按鈕疊置於輸入框上的 Bug。
4. **狀態隔離**：
   - 繼續維持 `VercelDeploymentService` 與 `AppSettingsService` 的解耦架構，UI 排版變更完全不破壞底層邏輯與後台部署流程。
