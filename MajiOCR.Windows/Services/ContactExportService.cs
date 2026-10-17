using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using MajiOCR_Windows.Models;

namespace MajiOCR_Windows.Services;

public interface IContactExportService
{
    string GenerateOutlookCsv(IReadOnlyList<ContactDetailModel> contacts);
    string GenerateGoogleCsv(IReadOnlyList<ContactDetailModel> contacts);
    string GenerateVCard(IReadOnlyList<ContactDetailModel> contacts);
    Task<string?> ExportToFileAsync(string content, string defaultFileName, string fileTypeDescription, string fileTypeExtension, IntPtr windowHandle);
}

public class ContactExportService : IContactExportService
{
    private static readonly UTF8Encoding Utf8WithBom = new(true);

    public string GenerateOutlookCsv(IReadOnlyList<ContactDetailModel> contacts)
    {
        var sb = new StringBuilder();

        // Outlook Standard CSV Header (English & Chinese standard interoperability)
        // Includes full Outlook contact columns
        var headers = new[]
        {
            "First Name", "Last Name", "Title", "Company", "Department", "Job Title",
            "Business Phone", "Business Phone 2", "Business Fax", "Company Main Phone",
            "Mobile Phone", "Home Phone", "Primary Phone",
            "E-mail Address", "E-mail 2 Address", "E-mail 3 Address",
            "Business Street", "Business City", "Business State", "Business Postal Code", "Business Country/Region",
            "Web Page", "Categories", "Notes"
        };

        sb.AppendLine(string.Join(",", headers.Select(EscapeCsvField)));

        if (contacts != null)
        {
            foreach (var contact in contacts)
            {
                var row = BuildOutlookRow(contact);
                sb.AppendLine(string.Join(",", row.Select(EscapeCsvField)));
            }
        }

        return sb.ToString();
    }

    private static string[] BuildOutlookRow(ContactDetailModel contact)
    {
        // Extract fields
        string fullName = contact.Name ?? string.Empty;
        string company = contact.Company ?? string.Empty;
        string jobTitle = string.Empty;
        string mobilePhone = string.Empty;
        string businessPhone = string.Empty;
        string email = string.Empty;
        string address = string.Empty;
        string website = string.Empty;
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
                    if (string.IsNullOrWhiteSpace(mobilePhone))
                    {
                        if (val.RawValue.StartsWith("09") || val.RawValue.StartsWith("+8869"))
                            mobilePhone = val.RawValue;
                        else
                            businessPhone = val.RawValue;
                    }
                    else if (string.IsNullOrWhiteSpace(businessPhone))
                    {
                        businessPhone = val.RawValue;
                    }
                    else
                    {
                        notes.Add($"其他電話: {val.RawValue}");
                    }
                    break;
                case CanonicalValueKind.Email:
                    if (string.IsNullOrWhiteSpace(email)) email = val.RawValue;
                    else notes.Add($"其他Email: {val.RawValue}");
                    break;
                case CanonicalValueKind.Address:
                    if (string.IsNullOrWhiteSpace(address)) address = val.RawValue;
                    else notes.Add($"其他地址: {val.RawValue}");
                    break;
                case CanonicalValueKind.Website:
                    if (string.IsNullOrWhiteSpace(website)) website = val.RawValue;
                    else notes.Add($"其他網站: {val.RawValue}");
                    break;
                case CanonicalValueKind.Note:
                    notes.Add(val.RawValue);
                    break;
                case CanonicalValueKind.Custom:
                    string label = !string.IsNullOrWhiteSpace(val.CustomFieldNameSnapshot) ? val.CustomFieldNameSnapshot : val.Label;
                    notes.Add($"{label}: {val.RawValue}");
                    break;
                case CanonicalValueKind.Unclassified:
                    notes.Add($"未分類內容: {val.RawValue}");
                    break;
            }
        }

        // If phone wasn't classified between mobile/business
        if (string.IsNullOrWhiteSpace(mobilePhone) && string.IsNullOrWhiteSpace(businessPhone))
        {
            var phoneVal = contact.Values.FirstOrDefault(v => v.ValueKind == CanonicalValueKind.Phone)?.RawValue;
            if (!string.IsNullOrWhiteSpace(phoneVal)) mobilePhone = phoneVal;
        }

        // Split name (First Name / Last Name)
        var (firstName, lastName) = SplitChineseName(fullName);

        // Tags to Categories (separated by semicolon)
        string categories = contact.Tags != null && contact.Tags.Count > 0
            ? string.Join("; ", contact.Tags.Select(t => t.DisplayName))
            : string.Empty;

        string combinedNotes = string.Join("\n", notes);

        return new[]
        {
            firstName,           // First Name
            lastName,            // Last Name
            "",                  // Title (Mr/Ms)
            company,             // Company
            "",                  // Department
            jobTitle,            // Job Title
            businessPhone,       // Business Phone
            "",                  // Business Phone 2
            "",                  // Business Fax
            "",                  // Company Main Phone
            mobilePhone,         // Mobile Phone
            "",                  // Home Phone
            "",                  // Primary Phone
            email,               // E-mail Address
            "",                  // E-mail 2 Address
            "",                  // E-mail 3 Address
            address,             // Business Street
            "",                  // Business City
            "",                  // Business State
            "",                  // Business Postal Code
            "",                  // Business Country/Region
            website,             // Web Page
            categories,          // Categories
            combinedNotes        // Notes
        };
    }

    public string GenerateGoogleCsv(IReadOnlyList<ContactDetailModel> contacts)
    {
        var sb = new StringBuilder();

        // Google Contacts Standard Header
        var headers = new[]
        {
            "Name", "Given Name", "Additional Name", "Family Name", "Yomi Name", "Given Name Yomi",
            "Additional Name Yomi", "Family Name Yomi", "Name Prefix", "Name Suffix", "Initials",
            "Nickname", "Short Name", "Maiden Name", "Birthday", "Gender", "Location",
            "Billing Information", "Directory Server", "Mileage", "Occupation", "Hobby",
            "Sensitivity", "Priority", "Subject", "Notes", "Group Membership",
            "E-mail 1 - Type", "E-mail 1 - Value", "E-mail 2 - Type", "E-mail 2 - Value",
            "Phone 1 - Type", "Phone 1 - Value", "Phone 2 - Type", "Phone 2 - Value",
            "Address 1 - Type", "Address 1 - Formatted", "Address 1 - Street", "Address 1 - City",
            "Address 1 - PO Box", "Address 1 - Region", "Address 1 - Postal Code", "Address 1 - Country",
            "Address 1 - Extended Address",
            "Organization 1 - Type", "Organization 1 - Name", "Organization 1 - Title",
            "Organization 1 - Department", "Organization 1 - Symbol", "Organization 1 - Location",
            "Organization 1 - Job Description",
            "Website 1 - Type", "Website 1 - Value"
        };

        sb.AppendLine(string.Join(",", headers.Select(EscapeCsvField)));

        if (contacts != null)
        {
            foreach (var contact in contacts)
            {
                var row = BuildGoogleRow(contact);
                sb.AppendLine(string.Join(",", row.Select(EscapeCsvField)));
            }
        }

        return sb.ToString();
    }

    private static string[] BuildGoogleRow(ContactDetailModel contact)
    {
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

        var (givenName, familyName) = SplitChineseName(fullName);

        // Group Membership: * myContacts ::: Tag1 ::: Tag2
        string groupMembership = "* myContacts";
        if (contact.Tags != null && contact.Tags.Count > 0)
        {
            groupMembership += " ::: " + string.Join(" ::: ", contact.Tags.Select(t => t.DisplayName));
        }

        string email1 = emails.Count > 0 ? emails[0] : "";
        string email2 = emails.Count > 1 ? emails[1] : "";
        string phone1 = phones.Count > 0 ? phones[0] : "";
        string phone2 = phones.Count > 1 ? phones[1] : "";
        string addr1 = addresses.Count > 0 ? addresses[0] : "";
        string web1 = websites.Count > 0 ? websites[0] : "";
        string combinedNotes = string.Join("\n", notes);

        return new[]
        {
            fullName,    // Name
            givenName,   // Given Name
            "",          // Additional Name
            familyName,  // Family Name
            "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", "", // various empty fields
            combinedNotes,    // Notes
            groupMembership,  // Group Membership
            !string.IsNullOrWhiteSpace(email1) ? "Work" : "", email1,
            !string.IsNullOrWhiteSpace(email2) ? "Home" : "", email2,
            !string.IsNullOrWhiteSpace(phone1) ? "Mobile" : "", phone1,
            !string.IsNullOrWhiteSpace(phone2) ? "Work" : "", phone2,
            !string.IsNullOrWhiteSpace(addr1) ? "Work" : "", addr1, addr1, "", "", "", "", "", "",
            !string.IsNullOrWhiteSpace(company) ? "Work" : "", company, jobTitle, "", "", "", "",
            !string.IsNullOrWhiteSpace(web1) ? "Work" : "", web1
        };
    }

    public string GenerateVCard(IReadOnlyList<ContactDetailModel> contacts)
    {
        var sb = new StringBuilder();

        if (contacts != null)
        {
            foreach (var contact in contacts)
            {
                sb.AppendLine("BEGIN:VCARD");
                sb.AppendLine("VERSION:3.0");

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

                var (givenName, familyName) = SplitChineseName(fullName);

                // N:Family;Given;;;
                sb.AppendLine($"N:{EscapeVCard(familyName)};{EscapeVCard(givenName)};;;");
                sb.AppendLine($"FN:{EscapeVCard(fullName)}");

                if (!string.IsNullOrWhiteSpace(company))
                    sb.AppendLine($"ORG:{EscapeVCard(company)}");

                if (!string.IsNullOrWhiteSpace(jobTitle))
                    sb.AppendLine($"TITLE:{EscapeVCard(jobTitle)}");

                foreach (var phone in phones)
                {
                    string phoneType = (phone.StartsWith("09") || phone.StartsWith("+8869")) ? "CELL,VOICE" : "WORK,VOICE";
                    sb.AppendLine($"TEL;TYPE={phoneType}:{EscapeVCard(phone)}");
                }

                foreach (var email in emails)
                {
                    sb.AppendLine($"EMAIL;TYPE=INTERNET,WORK:{EscapeVCard(email)}");
                }

                foreach (var addr in addresses)
                {
                    // ADR:;;Street;City;Region;PostalCode;Country
                    sb.AppendLine($"ADR;TYPE=WORK:;;{EscapeVCard(addr)};;;;");
                }

                foreach (var web in websites)
                {
                    sb.AppendLine($"URL;TYPE=WORK:{EscapeVCard(web)}");
                }

                if (contact.Tags != null && contact.Tags.Count > 0)
                {
                    string categories = string.Join(",", contact.Tags.Select(t => EscapeVCard(t.DisplayName)));
                    sb.AppendLine($"CATEGORIES:{categories}");
                }

                if (notes.Count > 0)
                {
                    string joinedNotes = string.Join("\\n", notes.Select(EscapeVCard));
                    sb.AppendLine($"NOTE:{joinedNotes}");
                }

                sb.AppendLine("END:VCARD");
            }
        }

        return sb.ToString();
    }

    public async Task<string?> ExportToFileAsync(string content, string defaultFileName, string fileTypeDescription, string fileTypeExtension, IntPtr windowHandle)
    {
        try
        {
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = defaultFileName
            };
            savePicker.FileTypeChoices.Add(fileTypeDescription, new List<string> { fileTypeExtension });

            if (windowHandle != IntPtr.Zero)
            {
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, windowHandle);
            }

            StorageFile file = await savePicker.PickSaveFileAsync();
            if (file != null)
            {
                byte[] bytes;
                if (fileTypeExtension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    // CSV with UTF-8 BOM
                    bytes = Utf8WithBom.GetBytes(content);
                }
                else
                {
                    // UTF-8 without BOM or standard UTF-8
                    bytes = Encoding.UTF8.GetBytes(content);
                }

                await File.WriteAllBytesAsync(file.Path, bytes);
                return file.Path;
            }
        }
        catch (Exception)
        {
            // Fallback: save to local Documents folder if WinRT picker throws
            try
            {
                string docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string targetPath = Path.Combine(docsDir, defaultFileName + fileTypeExtension);
                byte[] bytes = fileTypeExtension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
                    ? Utf8WithBom.GetBytes(content)
                    : Encoding.UTF8.GetBytes(content);
                await File.WriteAllBytesAsync(targetPath, bytes);
                return targetPath;
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field)) return "";

        bool mustQuote = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        if (mustQuote)
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }

    public static string EscapeVCard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("\\", "\\\\")
            .Replace(";", "\\;")
            .Replace(",", "\\,")
            .Replace("\r\n", "\\n")
            .Replace("\n", "\\n")
            .Replace("\r", "\\n");
    }

    public static (string FirstName, string LastName) SplitChineseName(string fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return ("", "");

        fullName = fullName.Trim();

        // If English name with space (e.g. "John Doe")
        if (fullName.Contains(' '))
        {
            var parts = fullName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                return (string.Join(" ", parts.Take(parts.Length - 1)), parts.Last());
            }
            return (fullName, "");
        }

        // Chinese naming heuristics:
        // Compound surnames
        var compoundSurnames = new HashSet<string>
        {
            "歐陽", "司徒", "上官", "諸葛", "諸葛", "皇甫", "尉遲", "公孫", "慕容", "司馬", "夏侯"
        };

        if (fullName.Length >= 3)
        {
            string prefix2 = fullName.Substring(0, 2);
            if (compoundSurnames.Contains(prefix2))
            {
                return (fullName.Substring(2), prefix2);
            }
        }

        if (fullName.Length >= 2 && fullName.Length <= 4)
        {
            // First char is surname, rest is given name
            return (fullName.Substring(1), fullName.Substring(0, 1));
        }

        return (fullName, "");
    }
}
