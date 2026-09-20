using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace FROG.Core.Inventory;

public sealed class TeamcraftListImporter
{
    private static readonly Regex ItemLine =
        new(@"^(\d+)x\s+(.+)$", RegexOptions.Compiled);

    private static readonly ClientLanguage[] Languages =
    {
        ClientLanguage.Japanese,
        ClientLanguage.English,
        ClientLanguage.German,
        ClientLanguage.French
    };

    private readonly IDataManager dataManager;

    public TeamcraftListImporter(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public RequirementSet Import(string text, string name = "Teamcraft List")
    {
        var requirements = new RequirementSet(name);

        if (string.IsNullOrWhiteSpace(text))
            return requirements;

        var crystalEntries =
            CollectCrystalEntries(text);

        var currentSection =
            string.Empty;

        var inPrecraftSection =
            false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            if (IsSectionHeader(line))
            {
                currentSection =
                    NormalizeHeader(line);

                inPrecraftSection =
                    IsPrecraftSection(currentSection);

                continue;
            }

            var match = ItemLine.Match(line);

            if (!match.Success)
                continue;

            if (!int.TryParse(
                    match.Groups[1].Value,
                    out var quantity))
            {
                continue;
            }

            if (quantity <= 0)
                continue;

            var itemName =
                CleanName(
                    match.Groups[2].Value);

            if (itemName.Length == 0)
                continue;

            if (IsOtherSection(currentSection) &&
                crystalEntries.Contains(
                    BuildCrystalEntryKey(
                        itemName,
                        quantity)))
            {
                continue;
            }

            if (!TryResolveItemId(
                    itemName,
                    out var itemId))
            {
                continue;
            }

            requirements.Add(
                new Requirement(
                    itemId,
                    quantity,
                    inPrecraftSection
                        ? RequirementQualityPolicy.HqFirst
                        : RequirementQualityPolicy.Any,
                    inPrecraftSection));
        }

        return requirements;
    }

    private static HashSet<string> CollectCrystalEntries(
        string text)
    {
        var entries =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        var currentSection =
            string.Empty;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            if (IsSectionHeader(line))
            {
                currentSection =
                    NormalizeHeader(line);

                continue;
            }

            if (!IsCrystalSection(currentSection))
                continue;

            var match =
                ItemLine.Match(line);

            if (!match.Success)
                continue;

            if (!int.TryParse(
                    match.Groups[1].Value,
                    out var quantity))
            {
                continue;
            }

            if (quantity <= 0)
                continue;

            var itemName =
                CleanName(
                    match.Groups[2].Value);

            if (itemName.Length == 0)
                continue;

            entries.Add(
                BuildCrystalEntryKey(
                    itemName,
                    quantity));
        }

        return entries;
    }

    private bool TryResolveItemId(
        string itemName,
        out uint itemId)
    {
        foreach (var language in LanguageOrder())
        {
            var sheet =
                dataManager.GetExcelSheet<Item>(
                    language);

            if (sheet == null)
                continue;

            foreach (var item in sheet)
            {
                if (string.Equals(
                        item.Name.ToString(),
                        itemName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    itemId = item.RowId;
                    return true;
                }
            }
        }

        itemId = 0;
        return false;
    }

    private IEnumerable<ClientLanguage> LanguageOrder()
    {
        var currentLanguage =
            dataManager.Language;

        yield return currentLanguage;

        foreach (var language in Languages)
        {
            if (language != currentLanguage)
                yield return language;
        }
    }

    private static bool IsCrystalSection(
        string section)
    {
        return string.Equals(
            section,
            "crystals",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOtherSection(
        string section)
    {
        return string.Equals(
            section,
            "other",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrecraftSection(
        string section)
    {
        return string.Equals(
            section,
            "pre crafts",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSectionHeader(
        string line)
    {
        return line.EndsWith(':');
    }

    private static string NormalizeHeader(
        string line)
    {
        var header =
            line.Trim();

        if (header.EndsWith(':'))
            header = header[..^1];

        return header.Trim();
    }

    private static string CleanName(
        string name)
    {
        return name.Trim();
    }

    private static string BuildCrystalEntryKey(
        string itemName,
        int quantity)
    {
        return $"{quantity}|{itemName}";
    }
}
