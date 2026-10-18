using System;
using System.Collections.Generic;
using System.Linq;

namespace MajiOCR_Windows.Models;

/// <summary>
/// 衝突裁決策略
/// </summary>
public enum MergeResolutionStrategy
{
    /// <summary>
    /// 以較新時間為準（推薦預設）
    /// </summary>
    KeepNewer,

    /// <summary>
    /// 保留本機資料
    /// </summary>
    KeepLocal,

    /// <summary>
    /// 採納外部/雲端資料
    /// </summary>
    KeepRemote,

    /// <summary>
    /// 兩側同時保留（本機維持，外部派生為新名片與新圖檔）
    /// </summary>
    KeepBoth
}

/// <summary>
/// 名片差異狀態分類
/// </summary>
public enum ContactDiffStatus
{
    /// <summary>
    /// 僅存在於本機（本機新增）
    /// </summary>
    OnlyInLocal,

    /// <summary>
    /// 僅存在於外部（外部/雲端新增）
    /// </summary>
    OnlyInRemote,

    /// <summary>
    /// 兩端完全一致
    /// </summary>
    Identical,

    /// <summary>
    /// 內容衝突（兩端皆存在且欄位/備註/圖片/標籤有差異）
    /// </summary>
    Conflicted
}

/// <summary>
/// 單個欄位差異描述
/// </summary>
public class FieldDiffDetail
{
    public string FieldName { get; set; } = string.Empty;
    public string LocalValue { get; set; } = string.Empty;
    public string RemoteValue { get; set; } = string.Empty;

    public override string ToString() => $"{FieldName}：本機「{LocalValue}」 vs 外部「{RemoteValue}」";
}

/// <summary>
/// 名片資料比對項目
/// </summary>
public class ContactDiffItem
{
    public string ContactId { get; set; } = string.Empty;
    public ContactDiffStatus Status { get; set; }

    public string DisplayName { get; set; } = "未命名";
    public string Company { get; set; } = string.Empty;
    public string JobTitle { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;

    public DateTimeOffset? LocalUpdatedAt { get; set; }
    public DateTimeOffset? RemoteUpdatedAt { get; set; }

    public bool HasLocal => LocalUpdatedAt.HasValue;
    public bool HasRemote => RemoteUpdatedAt.HasValue;

    public bool IsRemoteNewer => (RemoteUpdatedAt ?? DateTimeOffset.MinValue) > (LocalUpdatedAt ?? DateTimeOffset.MinValue);

    public string LocalTimeFormatted => LocalUpdatedAt.HasValue
        ? LocalUpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "（無本機資料）";

    public string RemoteTimeFormatted => RemoteUpdatedAt.HasValue
        ? RemoteUpdatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
        : "（無外部資料）";

    public string TimeDifferenceSummary
    {
        get
        {
            if (!LocalUpdatedAt.HasValue || !RemoteUpdatedAt.HasValue) return string.Empty;
            var diff = RemoteUpdatedAt.Value - LocalUpdatedAt.Value;
            if (Math.Abs(diff.TotalMinutes) < 1) return "（更新時間相近）";
            if (diff > TimeSpan.Zero)
            {
                return diff.TotalHours >= 1
                    ? $"🟢 外部較新 (領先 {(int)diff.TotalHours} 小時 {diff.Minutes} 分)"
                    : $"🟢 外部較新 (領先 {(int)diff.TotalMinutes} 分鐘)";
            }
            else
            {
                var absDiff = -diff;
                return absDiff.TotalHours >= 1
                    ? $"💻 本機較新 (領先 {(int)absDiff.TotalHours} 小時 {absDiff.Minutes} 分)"
                    : $"💻 本機較新 (領先 {(int)absDiff.TotalMinutes} 分鐘)";
            }
        }
    }

    public List<FieldDiffDetail> Differences { get; set; } = new();

    /// <summary>
    /// 使用者當前為本項目所選定的裁決策略
    /// </summary>
    public MergeResolutionStrategy Resolution { get; set; } = MergeResolutionStrategy.KeepNewer;
}

/// <summary>
/// 資料庫整併差異分析報告
/// </summary>
public class DatabaseMergeDiffReport
{
    public List<ContactDiffItem> Items { get; set; } = new();

    public int TotalCompared => Items.Count;
    public int OnlyInLocalCount => Items.Count(i => i.Status == ContactDiffStatus.OnlyInLocal);
    public int OnlyInRemoteCount => Items.Count(i => i.Status == ContactDiffStatus.OnlyInRemote);
    public int IdenticalCount => Items.Count(i => i.Status == ContactDiffStatus.Identical);
    public int ConflictedCount => Items.Count(i => i.Status == ContactDiffStatus.Conflicted);

    public IEnumerable<ContactDiffItem> ConflictedItems => Items.Where(i => i.Status == ContactDiffStatus.Conflicted);
    public IEnumerable<ContactDiffItem> OnlyInRemoteItems => Items.Where(i => i.Status == ContactDiffStatus.OnlyInRemote);
    public IEnumerable<ContactDiffItem> OnlyInLocalItems => Items.Where(i => i.Status == ContactDiffStatus.OnlyInLocal);
}
