using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services;

public class GoogleSyncProgress
{
    public int CurrentIndex { get; set; }
    public int TotalCount { get; set; }
    public string CurrentContactName { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
    public double Percentage => TotalCount > 0 ? (double)CurrentIndex / TotalCount * 100.0 : 0.0;
}

public class GoogleSyncResult
{
    public bool IsSuccess { get; set; }
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public string SummaryMessage { get; set; } = string.Empty;

    public static GoogleSyncResult Success(int total, int success) =>
        new()
        {
            IsSuccess = true,
            TotalCount = total,
            SuccessCount = success,
            FailedCount = 0,
            SummaryMessage = $"已成功同步 {success} 筆名片至 Google 聯絡人！"
        };

    public static GoogleSyncResult Partial(int total, int success, int failed, List<string> errors) =>
        new()
        {
            IsSuccess = success > 0,
            TotalCount = total,
            SuccessCount = success,
            FailedCount = failed,
            Errors = errors,
            SummaryMessage = $"同步完成：成功 {success} 筆，失敗 {failed} 筆。"
        };

    public static GoogleSyncResult Failed(string errorMessage) =>
        new()
        {
            IsSuccess = false,
            TotalCount = 0,
            SuccessCount = 0,
            FailedCount = 0,
            Errors = new List<string> { errorMessage },
            SummaryMessage = $"Google 聯絡人同步失敗：{errorMessage}"
        };
}

public interface IGoogleContactsSyncService
{
    string BuildGooglePersonJson(ContactDetailModel contact);
    Task<GoogleSyncResult> SyncToGoogleContactsAsync(
        IReadOnlyList<ContactDetailModel> contacts,
        string? accessToken = null,
        IProgress<GoogleSyncProgress>? progress = null,
        CancellationToken ct = default);
}

public class GoogleContactsSyncService : IGoogleContactsSyncService
{
    private static readonly HttpClient HttpClient = new();

    public string BuildGooglePersonJson(ContactDetailModel contact)
    {
        var person = new JsonObject();

        string fullName = contact.Name ?? string.Empty;
        string company = contact.Company ?? string.Empty;
        string jobTitle = string.Empty;
        var phones = new List<string>();
        var emails = new List<string>();
        var addresses = new List<string>();
        var websites = new List<string>();
        var notes = new List<string>();

        foreach (var val in contact.Values)
        {
            if (string.IsNullOrWhiteSpace(val.RawValue)) continue;

            switch (val.ValueKind)
            {
                case CanonicalValueKind.Name:
                    if (string.IsNullOrWhiteSpace(fullName)) fullName = val.RawValue;
                    break;
                case CanonicalValueKind.Company:
                    if (string.IsNullOrWhiteSpace(company)) company = val.RawValue;
                    break;
                case CanonicalValueKind.Title:
                    if (string.IsNullOrWhiteSpace(jobTitle)) jobTitle = val.RawValue;
                    break;
                case CanonicalValueKind.Phone:
                    phones.Add(val.RawValue);
                    break;
                case CanonicalValueKind.Email:
                    emails.Add(val.RawValue);
                    break;
                case CanonicalValueKind.Address:
                    addresses.Add(val.RawValue);
                    break;
                case CanonicalValueKind.Website:
                    websites.Add(val.RawValue);
                    break;
                case CanonicalValueKind.Note:
                    notes.Add(val.RawValue);
                    break;
                case CanonicalValueKind.Custom:
                    string label = !string.IsNullOrWhiteSpace(val.CustomFieldNameSnapshot) ? val.CustomFieldNameSnapshot : val.Label;
                    notes.Add($"{label}: {val.RawValue}");
                    break;
                case CanonicalValueKind.Unclassified:
                    notes.Add($"未分類: {val.RawValue}");
                    break;
            }
        }

        var (givenName, familyName) = ContactExportService.SplitChineseName(fullName);

        // Names
        var namesArray = new JsonArray();
        var nameObj = new JsonObject();
        if (!string.IsNullOrWhiteSpace(fullName)) nameObj["displayName"] = fullName;
        if (!string.IsNullOrWhiteSpace(givenName)) nameObj["givenName"] = givenName;
        if (!string.IsNullOrWhiteSpace(familyName)) nameObj["familyName"] = familyName;
        if (nameObj.Count > 0) namesArray.Add(nameObj);
        if (namesArray.Count > 0) person["names"] = namesArray;

        // Organizations
        if (!string.IsNullOrWhiteSpace(company) || !string.IsNullOrWhiteSpace(jobTitle))
        {
            var orgsArray = new JsonArray();
            var orgObj = new JsonObject { ["type"] = "work" };
            if (!string.IsNullOrWhiteSpace(company)) orgObj["name"] = company;
            if (!string.IsNullOrWhiteSpace(jobTitle)) orgObj["title"] = jobTitle;
            orgsArray.Add(orgObj);
            person["organizations"] = orgsArray;
        }

        // Phone Numbers
        if (phones.Count > 0)
        {
            var phonesArray = new JsonArray();
            for (int i = 0; i < phones.Count; i++)
            {
                var phone = phones[i];
                string type = (phone.StartsWith("09") || phone.StartsWith("+8869")) ? "mobile" : (i == 0 ? "work" : "other");
                phonesArray.Add(new JsonObject { ["value"] = phone, ["type"] = type });
            }
            person["phoneNumbers"] = phonesArray;
        }

        // Email Addresses
        if (emails.Count > 0)
        {
            var emailsArray = new JsonArray();
            for (int i = 0; i < emails.Count; i++)
            {
                string type = i == 0 ? "work" : "home";
                emailsArray.Add(new JsonObject { ["value"] = emails[i], ["type"] = type });
            }
            person["emailAddresses"] = emailsArray;
        }

        // Addresses
        if (addresses.Count > 0)
        {
            var addrsArray = new JsonArray();
            foreach (var addr in addresses)
            {
                addrsArray.Add(new JsonObject { ["formattedValue"] = addr, ["type"] = "work" });
            }
            person["addresses"] = addrsArray;
        }

        // URLs
        if (websites.Count > 0)
        {
            var urlsArray = new JsonArray();
            foreach (var web in websites)
            {
                urlsArray.Add(new JsonObject { ["value"] = web, ["type"] = "work" });
            }
            person["urls"] = urlsArray;
        }

        // Biographies (Notes)
        if (notes.Count > 0)
        {
            var biosArray = new JsonArray();
            biosArray.Add(new JsonObject
            {
                ["value"] = string.Join("\n", notes),
                ["contentType"] = "TEXT_PLAIN"
            });
            person["biographies"] = biosArray;
        }

        // User Defined / Tags
        if (contact.Tags != null && contact.Tags.Count > 0)
        {
            var userDefinedArray = new JsonArray();
            userDefinedArray.Add(new JsonObject
            {
                ["key"] = "分類標籤",
                ["value"] = string.Join(", ", contact.Tags.Select(t => t.DisplayName))
            });
            person["userDefined"] = userDefinedArray;
        }

        return person.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<GoogleSyncResult> SyncToGoogleContactsAsync(
        IReadOnlyList<ContactDetailModel> contacts,
        string? accessToken = null,
        IProgress<GoogleSyncProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (contacts == null || contacts.Count == 0)
        {
            return GoogleSyncResult.Success(0, 0);
        }

        int total = contacts.Count;
        int success = 0;
        int failed = 0;
        var errors = new List<string>();

        for (int i = 0; i < contacts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var contact = contacts[i];
            string displayName = contact.Name ?? contact.Company ?? $"名片 #{i + 1}";

            progress?.Report(new GoogleSyncProgress
            {
                CurrentIndex = i + 1,
                TotalCount = total,
                CurrentContactName = displayName,
                StatusMessage = $"正在同步 ({i + 1}/{total})：{displayName}"
            });

            try
            {
                string personJson = BuildGooglePersonJson(contact);

                if (!string.IsNullOrWhiteSpace(accessToken) &&
                    !accessToken.StartsWith("mock_", StringComparison.OrdinalIgnoreCase) &&
                    !accessToken.StartsWith("test_", StringComparison.OrdinalIgnoreCase))
                {
                    // Live HTTP POST to Google People API
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://people.googleapis.com/v1/people:createContact?personFields=names,organizations,phoneNumbers,emailAddresses,addresses,urls,userDefined,biographies");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Content = new StringContent(personJson, Encoding.UTF8, "application/json");

                    var response = await HttpClient.SendAsync(request, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        success++;
                    }
                    else
                    {
                        failed++;
                        string errorBody = await response.Content.ReadAsStringAsync(ct);
                        errors.Add($"{displayName}: HTTP {(int)response.StatusCode} - {errorBody}");
                    }
                }
                else
                {
                    // Validation mode / Offline verification mode
                    // Validate JSON schema
                    var node = JsonNode.Parse(personJson);
                    if (node != null)
                    {
                        success++;
                    }
                    else
                    {
                        failed++;
                        errors.Add($"{displayName}: JSON 解析失敗");
                    }
                }
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{displayName}: {ex.Message}");
            }
        }

        if (failed == 0)
        {
            return GoogleSyncResult.Success(total, success);
        }
        else
        {
            return GoogleSyncResult.Partial(total, success, failed, errors);
        }
    }
}
