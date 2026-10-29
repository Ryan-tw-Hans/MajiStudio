using System.ComponentModel;
using System;
using System.Collections.Generic;

namespace MajiOCR_Windows.Models;

public static class PersistenceStates
{
    public const string PendingFinalize = "PendingFinalize";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
    public const string ImageMissing = "ImageMissing";
}

public static class StorageStates
{
    public const string PendingFinalize = "PendingFinalize";
    public const string Ready = "Ready";
    public const string Failed = "Failed";
    public const string ImageMissing = "ImageMissing";
    public const string PendingDelete = "PendingDelete";
}

public static class CompletionSources
{
    public const string Manual = "Manual";
    public const string OfflineOcr = "OfflineOcr";
    public const string Mixed = "Mixed";
}

public static class CaptureSources
{
    public const string ImagePickerCopy = "ImagePickerCopy";
    public const string ClipboardImageCopy = "ClipboardImageCopy";
}

public class ContactRecord
{
    public string ContactId { get; set; } = string.Empty;
    public string ContractVersion { get; set; } = "0.3";
    public int Revision { get; set; } = 1;
    public string PersistenceState { get; set; } = PersistenceStates.PendingFinalize;
    public string CompletionSource { get; set; } = CompletionSources.Manual;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string SaveOperationId { get; set; } = string.Empty;
    public string ContentFingerprint { get; set; } = string.Empty;
    public bool PendingDelete { get; set; }
    public List<ContactValueRecord> Values { get; set; } = new();
    public CardImageAssetRecord? ImageAsset { get; set; }
}

public class ContactValueRecord
{
    public string ValueId { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string ValueKind { get; set; } = CanonicalValueKind.Unclassified;
    public string? CustomFieldDefinitionId { get; set; }
    public string? CustomFieldNameSnapshot { get; set; }
    public string RawValue { get; set; } = string.Empty;
    public string? NormalizedValue { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Ordinal { get; set; }
    public string SourceMode { get; set; } = CompletionSources.Manual;
    public DateTimeOffset LockedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CardImageAssetRecord
{
    public string AssetId { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string CaptureSource { get; set; } = CaptureSources.ImagePickerCopy;
    public string StorageState { get; set; } = StorageStates.PendingFinalize;
    public string RelativePath { get; set; } = string.Empty;
    public string? StagingRelativePath { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string MimeType { get; set; } = "image/png";
    public long ByteCount { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PendingDeleteJobRecord
{
    public string JobId { get; set; } = string.Empty;
    public string ContactId { get; set; } = string.Empty;
    public string? AssetRelativePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "Pending";
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

public class DraftValueSnapshot
{
    public string ValueKind { get; init; } = CanonicalValueKind.Unclassified;
    public string? CustomFieldDefinitionId { get; init; }
    public string? CustomFieldNameSnapshot { get; init; }
    public string RawValue { get; init; } = string.Empty;
    public string? NormalizedValue { get; init; }
    public string Label { get; init; } = string.Empty;
    public int Ordinal { get; init; }
    public string SourceMode { get; init; } = CompletionSources.Manual;
    public DateTimeOffset LockedAt { get; init; } = DateTimeOffset.UtcNow;
}

public class ContactDraftSnapshot
{
    public IReadOnlyList<TagChoice> Tags { get; init; } = Array.Empty<TagChoice>();
    public int ExpectedTagRevision { get; init; }
    public bool WriteTags { get; init; }

    public string ContactId { get; init; } = string.Empty;
    public string SaveOperationId { get; init; } = string.Empty;
    public int ExpectedRevision { get; init; } = 0;
    public int Revision { get; init; } = 1;
    public List<DraftValueSnapshot> Values { get; init; } = new();
    public byte[] ImageBytes { get; init; } = Array.Empty<byte>();
    public string CaptureSource { get; init; } = CaptureSources.ImagePickerCopy;
    public string MimeType { get; init; } = "image/png";
    public int Width { get; init; }
    public int Height { get; init; }
    public string ImageSha256 { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string CompletionSource { get; init; } = CompletionSources.Manual;
}

public class ContactSummaryItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public int TagRevision { get; set; }
    public IReadOnlyList<TagDefinition> Tags { get; set; } = Array.Empty<TagDefinition>();
    public string DisplayTags => Tags.Count == 0 ? "未分類" : string.Join("、", System.Linq.Enumerable.Select(Tags, t => t.DisplayName));

    public string ContactId { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string PersistenceState { get; set; } = PersistenceStates.Ready;
    public string CompletionSource { get; set; } = CompletionSources.Manual;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool PendingDelete { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool HasImage { get; set; }
    public string? RelativeImagePath { get; set; }

    public string DisplayTitle => !string.IsNullOrWhiteSpace(Name) ? Name : (!string.IsNullOrWhiteSpace(Company) ? Company : "未填姓名");
    public string DisplaySubtitle => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Company) ? Company : string.Empty;
    public string DisplayTime => (CompletedAt ?? CreatedAt).ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string DisplayDateOnly => (CompletedAt ?? CreatedAt).ToLocalTime().ToString("yyyy-MM-dd");
    public string DisplayStatus
    {
        get
        {
            if (PendingDelete) return "待刪除";
            return PersistenceState switch
            {
                PersistenceStates.Ready => "已保存",
                PersistenceStates.ImageMissing => "缺少圖片",
                PersistenceStates.PendingFinalize => "定稿處理中",
                PersistenceStates.Failed => "處理失敗",
                _ => PersistenceState
            };
        }
    }
}

public class ContactDetailModel
{
    public int TagRevision { get; set; }
    public IReadOnlyList<TagDefinition> Tags { get; set; } = Array.Empty<TagDefinition>();
    public string DisplayTags => Tags.Count == 0 ? "未分類" : string.Join("、", System.Linq.Enumerable.Select(Tags, t => t.DisplayName));

    public string ContactId { get; set; } = string.Empty;
    public int Revision { get; set; }
    public string PersistenceState { get; set; } = PersistenceStates.Ready;
    public string CompletionSource { get; set; } = CompletionSources.Manual;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public bool PendingDelete { get; set; }
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Name) ? Name : (!string.IsNullOrWhiteSpace(Company) ? Company : "未填姓名");
    public string DisplaySubtitle => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Company) ? Company : string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;

    public List<ContactValueRecord> Values { get; set; } = new();

    public CardImageAssetRecord? ImageAsset { get; set; }
    public byte[]? VerifiedImageBytes { get; set; }
    public bool IsImageAvailable => VerifiedImageBytes != null && VerifiedImageBytes.Length > 0;
    public string ImageNotice { get; set; } = string.Empty;
}

public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int PageIndex { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
    public bool HasPreviousPage => PageIndex > 0;
    public bool HasNextPage => PageIndex + 1 < TotalPages;
}

public class SaveResult
{
    public TagState? PersistedTags { get; set; }

    public bool Succeeded { get; set; }
    public bool IsDuplicate { get; set; }
    public string? ContactId { get; set; }
    public int PersistedRevision { get; set; }
    public string? PersistedFingerprint { get; set; }
    public bool RequiresSaveAgain { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static SaveResult Success(string contactId, int persistedRevision = 1, string? persistedFingerprint = null, bool isDuplicate = false) =>
        new() { Succeeded = true, ContactId = contactId, PersistedRevision = persistedRevision, PersistedFingerprint = persistedFingerprint, IsDuplicate = isDuplicate };

    public static SaveResult Success(string contactId, bool isDuplicate) =>
        new() { Succeeded = true, ContactId = contactId, PersistedRevision = 1, IsDuplicate = isDuplicate };

    public static SaveResult Failed(string code, string message, int persistedRevision = 0, string? persistedFingerprint = null, bool requiresSaveAgain = false) =>
        new() { Succeeded = false, ErrorCode = code, ErrorMessage = message, PersistedRevision = persistedRevision, PersistedFingerprint = persistedFingerprint, RequiresSaveAgain = requiresSaveAgain };
}

public class DeleteResult
{
    public bool Succeeded { get; set; }
    public string? ContactId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    public static DeleteResult Success(string contactId) =>
        new() { Succeeded = true, ContactId = contactId };

    public static DeleteResult Failed(string code, string message) =>
        new() { Succeeded = false, ErrorCode = code, ErrorMessage = message };
}

public enum DraftValidationCode
{
    Valid,
    NoConfirmedFields,
    UnconfirmedFields,
    MissingImage
}

public class DraftValidationResult
{
    public DraftValidationCode Code { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> UnconfirmedFieldNames { get; set; } = new();
    public bool IsValid => Code == DraftValidationCode.Valid;
}
