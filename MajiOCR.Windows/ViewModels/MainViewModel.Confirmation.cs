using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Services.Storage;

namespace MajiOCR_Windows.ViewModels;

public partial class MainViewModel
{
    public int ConfirmAllDraftRows(out string? error)
    {
        error = null;
        if (!CanRunActions)
        {
            error = "辨識或儲存正在進行，請稍候再確認。";
            return 0;
        }
        var rows = DraftSnapshotFactory.CollectAllDistinctRows(this)
            .Where(row => !string.IsNullOrWhiteSpace(row.Value)).ToList();
        if (rows.Count == 0)
        {
            error = "目前沒有可確認的欄位，請先辨識或填寫內容。";
            return 0;
        }
        var conflicts = rows.Where(row => CanonicalValueKind.IsSingleValueKind(row.ValueKind))
            .GroupBy(row => row.ValueKind).Where(group => group.Count() > 1)
            .Select(group => CanonicalValueKind.GetDisplayName(group.Key)).ToArray();
        if (conflicts.Length > 0)
        {
            error = $"{string.Join("、", conflicts)}有多筆內容，請先逐欄處理衝突，再確認全部欄位。";
            return 0;
        }
        int count = 0;
        foreach (var row in rows.Where(row => !row.IsConfirmed))
        {
            EnsureRowInDraft(row);
            row.Confirm();
            count++;
        }
        StatusMessage = count == 0 ? "所有非空欄位皆已確認。" : $"已確認 {count} 個欄位；所有非空欄位皆已確認。";
        OnPropertyChanged(nameof(HasUnsavedChanges));
        return count;
    }

    // Includes confirmation state and classifications, which the original text fingerprint does not capture.
    public string CaptureSaveConfirmationState()
    {
        var state = new
        {
            _currentContactId, _currentRevision, _currentSaveOperationId, _isCurrentContactDeleted,
            CurrentTagRevision,
            Image = CurrentSession == null ? null : new
            {
                CurrentSession.Sha256, CurrentSession.Width, CurrentSession.Height,
                CurrentSession.CaptureSource, CurrentSession.MimeType,
                Hash = Convert.ToHexString(SHA256.HashData(CurrentSession.ImageBytes))
            },
            Rows = DraftSnapshotFactory.CollectAllDistinctRows(this).Select(row => new
            {
                row.Id, row.ValueKind, row.CustomFieldDefinitionId, row.CustomFieldNameSnapshot,
                row.Value, row.Status, row.ConfirmedAt, row.EditVersion, row.IsUserModified, row.Source
            }).ToArray(),
            Tags = Classification.ToArray()
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(state))));
    }

    public Task<SaveResult> SaveConfirmedDraftAsync(bool accepted, string confirmationState)
    {
        if (!accepted)
            return Task.FromResult(SaveResult.Failed("SAVE_CANCELLED", "已取消儲存，草稿保持。"));
        if (!string.Equals(confirmationState, CaptureSaveConfirmationState(), StringComparison.Ordinal))
            return Task.FromResult(SaveResult.Failed("SAVE_CONFIRMATION_CHANGED", "草稿內容或分類已變更，請重新確認儲存。"));
        return SaveCurrentDraftAsync();
    }
}
