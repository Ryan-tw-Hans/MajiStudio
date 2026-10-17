using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.ViewModels;

namespace MajiOCR_Windows.Services.Storage;

public static class DraftSnapshotFactory
{
    public static List<DraftRowItem> CollectAllDistinctRows(MainViewModel vm)
    {
        var list = new List<DraftRowItem>
        {
            vm.NameRow,
            vm.CompanyRow,
            vm.TitleRow,
            vm.WebsiteRow,
            vm.AddressRow,
            vm.NoteRow
        };

        list.AddRange(vm.PhoneRows);
        list.AddRange(vm.EmailRows);
        list.AddRange(vm.CustomRows);
        list.AddRange(vm.UnclassifiedRows);
        list.AddRange(vm.DraftRows);

        return list.GroupBy(r => r.Id).Select(g => g.First()).ToList();
    }

    public static DraftValidationResult ValidateDraft(MainViewModel vm)
    {
        var allRows = CollectAllDistinctRows(vm);
        var nonEmptyRows = allRows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();

        // 1. Block any non-empty unconfirmed fields
        var unconfirmed = nonEmptyRows.Where(r => r.Status == DraftItemStatus.PendingConfirmation).ToList();
        if (unconfirmed.Count > 0)
        {
            var names = unconfirmed.Select(r => r.DisplayField).Distinct().ToList();
            return new DraftValidationResult
            {
                Code = DraftValidationCode.UnconfirmedFields,
                UnconfirmedFieldNames = names,
                Message = $"尚有 {unconfirmed.Count} 個欄位待確認：{string.Join("、", names)}。請先確認或清空這些欄位。"
            };
        }

        // 2. Block if no confirmed fields at all
        var confirmed = nonEmptyRows.Where(r => r.Status == DraftItemStatus.Confirmed).ToList();
        if (confirmed.Count == 0)
        {
            return new DraftValidationResult
            {
                Code = DraftValidationCode.NoConfirmedFields,
                Message = "請至少填寫並確認一個欄位。"
            };
        }

        // 3. Block if no image attached (RS-ISSUE-0011-v0.2 with-image slice)
        if (vm.CurrentSession == null || vm.CurrentSession.ImageBytes == null || vm.CurrentSession.ImageBytes.Length == 0)
        {
            return new DraftValidationResult
            {
                Code = DraftValidationCode.MissingImage,
                Message = "尚未載入名片圖片，無法保存名片。"
            };
        }

        return new DraftValidationResult
        {
            Code = DraftValidationCode.Valid
        };
    }

    public static ContactDraftSnapshot CreateSnapshot(
        MainViewModel vm,
        string contactId,
        int expectedRevision,
        int revision,
        string saveOperationId)
    {
        var allRows = CollectAllDistinctRows(vm);
        var confirmedRows = allRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Value) && r.Status == DraftItemStatus.Confirmed)
            .ToList();

        // Stable ordering: standard kinds first in canonical order, then Custom, then Unclassified, preserving user UI order
        var orderedRows = confirmedRows
            .OrderBy(r => GetKindSortPriority(r.ValueKind))
            .ToList();

        int ordinal = 0;
        var valueSnapshots = new List<DraftValueSnapshot>();
        foreach (var row in orderedRows)
        {
            string sourceMode = (row.IsUserModified || string.IsNullOrEmpty(row.Source) || !row.Source.Contains("辨識"))
                ? CompletionSources.Manual
                : CompletionSources.OfflineOcr;

            valueSnapshots.Add(new DraftValueSnapshot
            {
                ValueKind = row.ValueKind,
                CustomFieldDefinitionId = row.CustomFieldDefinitionId,
                CustomFieldNameSnapshot = row.CustomFieldNameSnapshot,
                RawValue = row.Value, // Exact raw value, no trim
                NormalizedValue = row.Value.Trim(),
                Label = row.DisplayField,
                Ordinal = ordinal++,
                SourceMode = sourceMode,
                LockedAt = row.ConfirmedAt ?? DateTimeOffset.UtcNow
            });
        }

        bool hasManual = valueSnapshots.Any(v => v.SourceMode == CompletionSources.Manual);
        bool hasOcr = valueSnapshots.Any(v => v.SourceMode == CompletionSources.OfflineOcr);
        string completionSource = (hasManual && hasOcr)
            ? CompletionSources.Mixed
            : (hasManual ? CompletionSources.Manual : CompletionSources.OfflineOcr);

        var session = vm.CurrentSession;
        string captureSource = session?.CaptureSource ?? CaptureSources.ImagePickerCopy;
        if (session != null && string.IsNullOrEmpty(session.CaptureSource))
        {
            captureSource = session.SourceName.Contains("剪貼簿")
                ? CaptureSources.ClipboardImageCopy
                : CaptureSources.ImagePickerCopy;
        }

        string imgSha = session?.Sha256 ?? string.Empty;
        string fingerprint = ComputeFingerprint(contactId, valueSnapshots, imgSha);

        return new ContactDraftSnapshot
        {
            Tags = vm.Classification.ToArray(),
            ExpectedTagRevision = vm.CurrentTagRevision,
            WriteTags = vm.HasUnsavedTags,
            ContactId = contactId,
            SaveOperationId = saveOperationId,
            ExpectedRevision = expectedRevision,
            Revision = revision,
            Values = valueSnapshots,
            ImageBytes = session?.ImageBytes != null ? (byte[])session.ImageBytes.Clone() : Array.Empty<byte>(),
            CaptureSource = captureSource,
            MimeType = session?.MimeType ?? string.Empty,
            Width = session?.Width ?? 0,
            Height = session?.Height ?? 0,
            ImageSha256 = imgSha,
            Fingerprint = fingerprint,
            CompletionSource = completionSource
        };
    }

    public static string? ComputeCurrentDraftFingerprint(MainViewModel vm) => ComputeCurrentDraftFingerprint(vm, vm.CurrentContactId);

    public static string? ComputeCurrentDraftFingerprint(MainViewModel vm, string contactId)
    {
        var allRows = CollectAllDistinctRows(vm);
        var nonEmpty = allRows.Where(r => !string.IsNullOrWhiteSpace(r.Value)).ToList();

        if (vm.CurrentSession == null && nonEmpty.Count == 0)
        {
            return null; // Empty draft
        }

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("contact_id", contactId);
            writer.WriteString("image_sha256", vm.CurrentSession?.Sha256 ?? string.Empty);
            writer.WriteStartArray("rows");

            var ordered = nonEmpty
                .OrderBy(r => GetKindSortPriority(r.ValueKind))
                .ToList();

            foreach (var r in ordered)
            {
                writer.WriteStartObject();
                writer.WriteString("id", r.Id);
                writer.WriteString("kind", r.ValueKind);
                writer.WriteString("label", r.DisplayField);
                writer.WriteString("raw_value", r.Value);
                writer.WriteString("custom_def_id", r.CustomFieldDefinitionId ?? string.Empty);
                writer.WriteString("custom_name", r.CustomFieldNameSnapshot ?? string.Empty);
                writer.WriteString("status", r.Status.ToString());
                writer.WriteBoolean("is_user_modified", r.IsUserModified);
                writer.WriteString("source", r.Source ?? string.Empty);
                writer.WriteString("confirmed_at", r.ConfirmedAt?.ToString("o") ?? string.Empty);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash);
    }

    public static string ComputeFingerprint(
        string contactId,
        IEnumerable<DraftValueSnapshot> values,
        string imageSha256)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("contact_id", contactId);
            writer.WriteString("image_sha256", imageSha256);
            writer.WriteStartArray("values");

            foreach (var v in values.OrderBy(v => v.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", v.Ordinal);
                writer.WriteString("kind", v.ValueKind);
                writer.WriteString("label", v.Label);
                writer.WriteString("raw_value", v.RawValue);
                writer.WriteString("custom_def_id", v.CustomFieldDefinitionId ?? string.Empty);
                writer.WriteString("custom_name", v.CustomFieldNameSnapshot ?? string.Empty);
                writer.WriteString("source_mode", v.SourceMode);
                writer.WriteString("locked_at", v.LockedAt.ToString("o"));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        byte[] hash = SHA256.HashData(ms.ToArray());
        return Convert.ToHexString(hash);
    }

    private static int GetKindSortPriority(string kind)
    {
        return kind switch
        {
            CanonicalValueKind.Name => 1,
            CanonicalValueKind.Company => 2,
            CanonicalValueKind.Title => 3,
            CanonicalValueKind.Phone => 4,
            CanonicalValueKind.Email => 5,
            CanonicalValueKind.Website => 6,
            CanonicalValueKind.Address => 7,
            CanonicalValueKind.Note => 8,
            CanonicalValueKind.Custom => 9,
            _ => 10
        };
    }
}
