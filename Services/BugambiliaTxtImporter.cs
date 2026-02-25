using System.Globalization;
using System.Text.RegularExpressions;
using MapaInteractivoBugambilia.Models;

namespace MapaInteractivoBugambilia.Services;

public static class BugambiliaTxtImporter
{
    private static readonly Regex BlockHeaderRegex =
        new(@"BLOQUE\s+""{2}(?<b>[A-Z])""{2}", RegexOptions.IgnoreCase);

    private static readonly Regex MultiSpaceRegex =
        new(@"\s{2,}", RegexOptions.Compiled);

    public static List<Lot> ParseLots(string projectKey, string txt)
    {
        txt = Normalize(txt);

        var lines = txt.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        var lots = new List<Lot>();
        var activeBlocks = new List<string>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // header like: "BLOQUE ""A"""   "BLOQUE ""B"""   ...
            var matches = BlockHeaderRegex.Matches(line);
            if (matches.Count > 0)
            {
                activeBlocks = matches
                    .Select(m => m.Groups["b"].Value.ToUpperInvariant())
                    .ToList();
                continue;
            }

            // skip table headers
            if (line.StartsWith("No.", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("No. Lote", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("m²", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("v²", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (activeBlocks.Count == 0)
                continue;

            var tokens = Tokenize(line);
            if (tokens.Count == 0)
                continue;

            // Process row across blocks: for each block read [lotNo] + either "Area verde" or [m2] [v2]
            var idx = 0;

            for (int bi = 0; bi < activeBlocks.Count; bi++)
            {
                if (idx >= tokens.Count)
                    break;

                if (!int.TryParse(tokens[idx], out var lotNumber))
                    break;

                idx++;

                if (idx >= tokens.Count)
                    break;

                var block = activeBlocks[bi];

                // area verde detection
                var t1 = tokens[idx];
                var t2 = (idx + 1 < tokens.Count) ? tokens[idx + 1] : "";
                var two = $"{t1} {t2}".Trim();

                if (IsAreaVerde(t1) || IsAreaVerde(two))
                {
                    idx += IsAreaVerde(two) ? 2 : 1;

                    lots.Add(new Lot
                    {
                        ProjectKey = projectKey,
                        Block = block,
                        LotNumber = lotNumber,
                        DisplayCode = $"{block}-{lotNumber}",
                        LotType = LotType.AreaVerde,
                        Status = LotStatus.Available
                    });

                    continue;
                }

                // normal numeric parse
                if (TryParseDecimal(tokens[idx], out var m2))
                {
                    idx++;

                    decimal? v2 = null;
                    if (idx < tokens.Count && TryParseDecimal(tokens[idx], out var parsedV2))
                    {
                        v2 = parsedV2;
                        idx++;
                    }

                    lots.Add(new Lot
                    {
                        ProjectKey = projectKey,
                        Block = block,
                        LotNumber = lotNumber,
                        DisplayCode = $"{block}-{lotNumber}",
                        LotType = LotType.Lot,
                        Status = LotStatus.Available,
                        AreaM2 = m2,
                        AreaV2 = v2
                    });

                    continue;
                }

                // Could not parse this block; continue to next block without consuming more.
            }
        }

        return lots
            .GroupBy(x => (x.ProjectKey, x.DisplayCode))
            .Select(g => g.First())
            .OrderBy(x => x.Block).ThenBy(x => x.LotNumber)
            .ToList();
    }

    private static string Normalize(string txt)
        => txt
            .Replace("�REA", "ÁREA", StringComparison.OrdinalIgnoreCase)
            .Replace("�rea", "Área", StringComparison.OrdinalIgnoreCase)
            .Replace("m�", "m²", StringComparison.OrdinalIgnoreCase)
            .Replace("v�", "v²", StringComparison.OrdinalIgnoreCase);

    private static List<string> Tokenize(string line)
    {
        var tab = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tab.Length >= 2)
            return tab.ToList();

        return MultiSpaceRegex.Split(line)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static bool IsAreaVerde(string s)
        => s.Contains("área verde", StringComparison.OrdinalIgnoreCase)
           || s.Equals("área", StringComparison.OrdinalIgnoreCase)
           || s.Equals("area", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseDecimal(string s, out decimal value)
        => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
}