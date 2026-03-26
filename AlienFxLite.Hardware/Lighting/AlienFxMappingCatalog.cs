using AlienFxLite.Contracts;

namespace AlienFxLite.Hardware.Lighting;

internal sealed class AlienFxMappingCatalog
{
    private static readonly IReadOnlyDictionary<string, string> PreferredGearAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["g33500"] = "dellg55500",
        };

    private readonly Dictionary<(ushort VendorId, ushort ProductId), IReadOnlyList<CatalogProfile>> _profiles;
    private readonly Dictionary<(ushort VendorId, ushort ProductId), IReadOnlyList<LightingDeviceProfile>> _resolvedCandidates = [];
    private readonly Dictionary<(ushort VendorId, ushort ProductId), LightingDeviceProfile> _resolvedProfiles = [];
    private readonly Lazy<HostIdentity> _hostIdentity = new(HostModelDetector.Detect);

    private AlienFxMappingCatalog(Dictionary<(ushort VendorId, ushort ProductId), IReadOnlyList<CatalogProfile>> profiles)
    {
        _profiles = profiles;
    }

    public static AlienFxMappingCatalog LoadDefault()
    {
        string csvPath = ResolveCsvPath();
        Dictionary<(ushort VendorId, ushort ProductId), List<CatalogProfile>> profiles = [];

        MappingGear? currentGear = null;
        foreach (string rawLine in File.ReadLines(csvPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            IReadOnlyList<string> fields = ParseFields(rawLine);
            if (fields.Count == 0)
            {
                continue;
            }

            switch (fields[0])
            {
                case "3":
                    if (currentGear is not null)
                    {
                        RegisterProfiles(currentGear, profiles);
                    }

                    currentGear = new MappingGear(fields.ElementAtOrDefault(1) ?? "AlienFX Device");
                    break;
                case "2" when currentGear is not null:
                    if (fields.Count >= 5 &&
                        int.TryParse(fields[1], out int gridId) &&
                        int.TryParse(fields[2], out int columns) &&
                        int.TryParse(fields[3], out int rows))
                    {
                        currentGear.Grids[gridId] = new MappingGrid(gridId, columns, rows, fields[4]);
                    }

                    break;
                case "0" when currentGear is not null:
                    if (fields.Count >= 4 &&
                        ushort.TryParse(fields[1], out ushort vendorId) &&
                        ushort.TryParse(fields[2], out ushort productId))
                    {
                        currentGear.Surfaces.Add(new MappingSurface(vendorId, productId, fields[3]));
                    }

                    break;
                case "1" when currentGear is not null && currentGear.Surfaces.Count > 0:
                    if (fields.Count >= 4 &&
                        int.TryParse(fields[1], out int zoneId) &&
                        int.TryParse(fields[2], out int flags))
                    {
                        List<MappingCell> cells = [];
                        for (int index = 4; index + 1 < fields.Count; index += 2)
                        {
                            if (int.TryParse(fields[index], out int cellGridId) &&
                                int.TryParse(fields[index + 1], out int cellIndex))
                            {
                                cells.Add(new MappingCell(cellGridId, cellIndex));
                            }
                        }

                        currentGear.Surfaces[^1].Zones.Add(new MappingZone(zoneId, flags, fields[3], cells));
                    }

                    break;
            }
        }

        if (currentGear is not null)
        {
            RegisterProfiles(currentGear, profiles);
        }

        return new AlienFxMappingCatalog(
            profiles.ToDictionary(
                static pair => pair.Key,
                static pair => (IReadOnlyList<CatalogProfile>)pair.Value));
    }

    public LightingDeviceProfile? FindProfile(ushort vendorId, ushort productId)
    {
        (ushort VendorId, ushort ProductId) key = (vendorId, productId);
        if (_resolvedProfiles.TryGetValue(key, out LightingDeviceProfile? resolved))
        {
            return resolved;
        }

        IReadOnlyList<LightingDeviceProfile> ordered = FindProfiles(vendorId, productId);
        LightingDeviceProfile? selected = ordered.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        _resolvedProfiles[key] = selected;
        return selected;
    }

    public IReadOnlyList<LightingDeviceProfile> FindProfiles(ushort vendorId, ushort productId)
    {
        (ushort VendorId, ushort ProductId) key = (vendorId, productId);
        if (_resolvedCandidates.TryGetValue(key, out IReadOnlyList<LightingDeviceProfile>? cached))
        {
            return cached;
        }

        if (!_profiles.TryGetValue(key, out IReadOnlyList<CatalogProfile>? candidates) || candidates.Count == 0)
        {
            return [];
        }

        HostIdentity host = _hostIdentity.Value;
        string? aliasedGear = ResolvePreferredGear(host);
        IReadOnlyList<LightingDeviceProfile> ordered = candidates
            .OrderByDescending(candidate => ScoreCandidate(candidate, host))
            .ThenByDescending(candidate =>
                !string.IsNullOrWhiteSpace(aliasedGear) &&
                string.Equals(candidate.NormalizedGearName, aliasedGear, StringComparison.Ordinal))
            .ThenByDescending(static candidate => candidate.IsKeyboardSurface)
            .ThenByDescending(static candidate => candidate.Profile.SurfaceName.Contains("main light", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static candidate => candidate.Profile.Zones.Count == 4)
            .ThenBy(static candidate => candidate.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(static candidate => candidate.Profile)
            .ToArray();

        _resolvedCandidates[key] = ordered;
        return ordered;
    }

    private static LightingDeviceProfile SelectBestProfile(IReadOnlyList<CatalogProfile> candidates, HostIdentity host)
    {
        if (candidates.Count == 1)
        {
            return candidates[0].Profile;
        }

        string? aliasedGear = ResolvePreferredGear(host);
        if (!string.IsNullOrWhiteSpace(aliasedGear))
        {
            CatalogProfile? aliasMatch = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.NormalizedGearName, aliasedGear, StringComparison.Ordinal));

            if (aliasMatch is not null)
            {
                return aliasMatch.Profile;
            }
        }

        return candidates
            .OrderByDescending(candidate => ScoreCandidate(candidate, host))
            .ThenByDescending(static candidate => candidate.IsKeyboardSurface)
            .ThenByDescending(static candidate => candidate.Profile.SurfaceName.Contains("main light", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static candidate => candidate.Profile.Zones.Count == 4)
            .ThenBy(static candidate => candidate.Profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .First()
            .Profile;
    }

    private static int ScoreCandidate(CatalogProfile candidate, HostIdentity host)
    {
        int score = 0;

        score += ScoreKeyMatch(candidate.NormalizedGearName, host.NormalizedModelKey, exact: 900, contains: 700);
        score += ScoreKeyMatch(candidate.NormalizedGearName, host.NormalizedProductKey, exact: 900, contains: 700);
        score += ScoreKeyMatch(candidate.NormalizedSearchText, host.NormalizedModelKey, exact: 450, contains: 260);
        score += ScoreKeyMatch(candidate.NormalizedSearchText, host.NormalizedProductKey, exact: 450, contains: 260);
        score += CountSharedTokens(candidate.Tokens, host.Tokens) * 140;

        if (host.IsDell)
        {
            if (candidate.Tokens.Contains("dell"))
            {
                score += 180;
            }

            if (host.IsDellGSeries && candidate.IsDellGSeries)
            {
                score += 240;
            }
        }

        if (host.IsAlienware && candidate.Tokens.Contains("alienware"))
        {
            score += 180;
        }

        if (candidate.IsKeyboardSurface)
        {
            score += 120;
        }

        if (candidate.Profile.SurfaceName.Contains("main light", StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
        }

        if (host.IsDellGSeries && candidate.Profile.Zones.Count == 4)
        {
            score += 90;
        }

        return score;
    }

    private static int ScoreKeyMatch(string candidate, string query, int exact, int contains)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        if (string.Equals(candidate, query, StringComparison.Ordinal))
        {
            return exact;
        }

        if (candidate.Contains(query, StringComparison.Ordinal) || query.Contains(candidate, StringComparison.Ordinal))
        {
            return contains;
        }

        return 0;
    }

    private static int CountSharedTokens(IReadOnlySet<string> left, IReadOnlySet<string> right) =>
        left.Count == 0 || right.Count == 0
            ? 0
            : left.Intersect(right).Count();

    private static string? ResolvePreferredGear(HostIdentity host)
    {
        if (PreferredGearAliases.TryGetValue(host.NormalizedProductKey, out string? preferredGear))
        {
            return preferredGear;
        }

        if (PreferredGearAliases.TryGetValue(host.NormalizedModelKey, out preferredGear))
        {
            return preferredGear;
        }

        if (host.IsDellGSeries && host.Tokens.Contains("g3"))
        {
            return "dellg55500";
        }

        return null;
    }

    private static void RegisterProfiles(
        MappingGear gear,
        Dictionary<(ushort VendorId, ushort ProductId), List<CatalogProfile>> profiles)
    {
        foreach (MappingSurface surface in gear.Surfaces)
        {
            IReadOnlyList<MappingZone> visibleZones = GetVisibleZones(surface.Zones);
            if (visibleZones.Count == 0)
            {
                continue;
            }

            List<LightingZoneDefinition> zones = visibleZones
                .Select(zone =>
                {
                    return new LightingZoneDefinition(
                        zone.ZoneId,
                        zone.Name,
                        zone.Flags != 0,
                        [(byte)Math.Clamp(zone.ZoneId, 0, 255)]);
                })
                .OrderBy(static zone => zone.ZoneId)
                .ToList();

            if (zones.Count == 0)
            {
                continue;
            }

            MappingGrid? previewSource = SelectPreviewGrid(gear.Grids.Values, visibleZones);
            LightingGridDefinition? previewGrid = null;
            if (previewSource is not null)
            {
                int?[] cells = new int?[previewSource.Columns * previewSource.Rows];
                foreach (MappingZone zone in visibleZones)
                {
                    foreach (MappingCell cell in zone.Cells.Where(cell => cell.GridId == previewSource.GridId))
                    {
                        if (cell.CellIndex >= 0 && cell.CellIndex < cells.Length && cells[cell.CellIndex] is null)
                        {
                            cells[cell.CellIndex] = zone.ZoneId;
                        }
                    }
                }

                previewGrid = new LightingGridDefinition(
                    previewSource.GridId,
                    previewSource.Name,
                    previewSource.Columns,
                    previewSource.Rows,
                    cells);
            }

            string displayName = gear.Surfaces.Count > 1
                ? $"{gear.Name} - {surface.Name}"
                : gear.Name;

            LightingDeviceProfile profile = new(
                $"{surface.VendorId:X4}:{surface.ProductId:X4}:{SanitizeKey(gear.Name)}:{SanitizeKey(surface.Name)}",
                displayName,
                surface.VendorId,
                surface.ProductId,
                0,
                surface.Name,
                "AlienFX",
                zones,
                previewGrid);

            (ushort VendorId, ushort ProductId) key = (surface.VendorId, surface.ProductId);
            if (!profiles.TryGetValue(key, out List<CatalogProfile>? candidates))
            {
                candidates = [];
                profiles[key] = candidates;
            }

            candidates.Add(new CatalogProfile(gear.Name, profile));
        }
    }

    private static IReadOnlyList<MappingZone> GetVisibleZones(IReadOnlyList<MappingZone> zones)
    {
        bool hasKeyboardGroups = zones.Any(static zone => zone.Name.Contains("kb", StringComparison.OrdinalIgnoreCase));
        if (!hasKeyboardGroups)
        {
            return zones;
        }

        List<MappingZone> filtered = zones
            .Where(static zone => zone.Name.Contains("kb", StringComparison.OrdinalIgnoreCase) || zone.Flags != 0)
            .ToList();

        return filtered.Count > 0 ? filtered : zones;
    }

    private static MappingGrid? SelectPreviewGrid(IEnumerable<MappingGrid> grids, IReadOnlyList<MappingZone> zones)
    {
        List<MappingGrid> candidates = grids.ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        bool keyboardSurface = zones.Any(static zone =>
            zone.Name.Contains("kb", StringComparison.OrdinalIgnoreCase) ||
            zone.Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase));

        return candidates
            .OrderByDescending(grid =>
                keyboardSurface && grid.Name.Contains("keyboard", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(grid => grid.Columns * grid.Rows)
            .ThenBy(static grid => grid.GridId)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> ParseFields(string line)
    {
        if (!line.StartsWith('\'') || !line.EndsWith('\''))
        {
            return [];
        }

        List<string> fields = [];
        int position = 1;
        while (position < line.Length - 1)
        {
            int separator = line.IndexOf("','", position, StringComparison.Ordinal);
            if (separator < 0)
            {
                fields.Add(line[position..^1]);
                break;
            }

            fields.Add(line[position..separator]);
            position = separator + 3;
        }

        return fields;
    }

    private static string ResolveCsvPath()
    {
        string baseDirectory = AppContext.BaseDirectory;
        string currentDirectory = Directory.GetCurrentDirectory();
        string[] candidates =
        [
            Path.Combine(baseDirectory, "Mappings", "devices.csv"),
            Path.Combine(currentDirectory, "Mappings", "devices.csv"),
            Path.Combine(currentDirectory, "AlienFxLite.Hardware", "Mappings", "devices.csv"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "AlienFxLite.Hardware", "Mappings", "devices.csv")),
        ];

        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("AlienFX device mapping file was not found.", candidates[0]);
    }

    private static string SanitizeKey(string value)
    {
        char[] chars = value
            .Select(static ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_')
            .ToArray();

        return new string(chars).Trim('_');
    }

    private sealed record CatalogProfile(string GearName, LightingDeviceProfile Profile)
    {
        public string NormalizedGearName { get; } = HostModelDetector.NormalizeKey(GearName);

        public string NormalizedSearchText { get; } = HostModelDetector.NormalizeKey($"{GearName} {Profile.SurfaceName} {Profile.DisplayName}");

        public HashSet<string> Tokens { get; } = HostModelDetector.Tokenize($"{GearName} {Profile.SurfaceName}");

        public bool IsKeyboardSurface => Tokens.Contains("keyboard") ||
                                         Tokens.Contains("kb") ||
                                         Profile.Zones.Any(static zone => zone.Name.Contains("kb", StringComparison.OrdinalIgnoreCase));

        public bool IsDellGSeries => Tokens.Contains("dell") &&
                                     (Tokens.Contains("g3") || Tokens.Contains("g5") || Tokens.Contains("g7"));
    }

    private sealed record MappingGear(string Name)
    {
        public List<MappingSurface> Surfaces { get; } = [];

        public Dictionary<int, MappingGrid> Grids { get; } = [];
    }

    private sealed record MappingSurface(ushort VendorId, ushort ProductId, string Name)
    {
        public List<MappingZone> Zones { get; } = [];
    }

    private sealed record MappingZone(int ZoneId, int Flags, string Name, List<MappingCell> Cells);

    private sealed record MappingCell(int GridId, int CellIndex);

    private sealed record MappingGrid(int GridId, int Columns, int Rows, string Name);
}
