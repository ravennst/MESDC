using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Utils;
using VRageMath;

namespace MESDC
{
    // ─── Update order changed to AfterSimulation ──────────────────────────────
    // Required to poll MESApiReady after LoadData registers the message handler.
    // Once the apply pass is complete the update hook unregisters itself.
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class MesdcSession : MySessionComponentBase
    {
        // ─── Constants ────────────────────────────────────────────────────────

        private const string MOD_TAG       = "[MESDC]";
        private const string BASELINE_FILE = "MESDC-Baseline.xml";
        private const string CONFIG_FILE   = "MESDC-Config.xml";
        private const string CHAT_PREFIX   = "/MESDC";

        private const string FACTION_VANILLA      = "(vanilla)";
        private const string FACTION_UNTAGGED_MOD = "(untagged-mod)";

        // MES Workshop ID — used to register the API message handler.
        private const long MES_MOD_ID = 1521905890L;

        // ─── State ────────────────────────────────────────────────────────────

        private bool _isServer             = false;
        private bool _initDone             = false;
        private bool _awaitingInitConfirm  = false;
        private bool _applyPassComplete    = false;

        // MES API
        private MesApi _mes                = null;

        // ─── Lifecycle ────────────────────────────────────────────────────────

        public override void LoadData()
        {
            _isServer = MyAPIGateway.Multiplayer.IsServer ||
                        MyAPIGateway.Utilities.IsDedicated;

            if (!_isServer)
                return;

            Log("LoadData: server detected, beginning startup sequence.");

            try
            {
                // Initialise MES API — registers message handler.
                // MesApi.Ready will become true once MES broadcasts its dict.
                _mes = new MesApi();

                RunStartupSequence();
            }
            catch (Exception ex)
            {
                Log("FATAL in LoadData: " + ex.ToString());
            }
        }

        protected override void UnloadData()
        {
            if (!_isServer)
                return;

            if (MyAPIGateway.Utilities != null)
                MyAPIGateway.Utilities.MessageEntered -= OnChatMessageEntered;

            _mes?.UnregisterListener();

            Log("UnloadData: MESDC unloaded.");
        }

        // ─── Per-tick update — polls for MES API ready ────────────────────────

        public override void UpdateAfterSimulation()
        {
            if (!_isServer || _applyPassComplete)
                return;

            if (_mes == null || !_mes.Ready)
                return;

            // MES API is ready. Run the apply pass once then stop polling.
            try
            {
                ApplyConfig();
            }
            catch (Exception ex)
            {
                Log("FATAL in ApplyConfig: " + ex.ToString());
            }

            _applyPassComplete = true;
            _initDone          = true;
            Log("Startup sequence complete.");
        }

        // ─── Startup Sequence ─────────────────────────────────────────────────

        private void RunStartupSequence()
        {
            WriteBaseline();

            if (!ConfigFileExists())
            {
                Log("No config file found. Generating from baseline.");
                GenerateConfigFromBaseline();
            }

            // ApplyConfig() is deferred to UpdateAfterSimulation until
            // MES API is ready. Register chat handler now so /MESDC commands
            // work even before the apply pass completes.
            MyAPIGateway.Utilities.MessageEntered += OnChatMessageEntered;

            Log("Baseline and config ready. Waiting for MES API...");
        }

        // ─── Storage Helpers ─────────────────────────────────────────────────

        private bool ConfigFileExists()
        {
            return MyAPIGateway.Utilities.FileExistsInWorldStorage(
                CONFIG_FILE, typeof(MesdcSession));
        }

        private bool BaselineFileExists()
        {
            return MyAPIGateway.Utilities.FileExistsInWorldStorage(
                BASELINE_FILE, typeof(MesdcSession));
        }

        private string ReadWorldFile(string filename)
        {
            try
            {
                using (TextReader reader = MyAPIGateway.Utilities
                    .ReadFileInWorldStorage(filename, typeof(MesdcSession)))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Log("ReadWorldFile failed for " + filename + ": " + ex.Message);
                return null;
            }
        }

        private bool WriteWorldFile(string filename, string content)
        {
            try
            {
                using (TextWriter writer = MyAPIGateway.Utilities
                    .WriteFileInWorldStorage(filename, typeof(MesdcSession)))
                {
                    writer.Write(content);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("WriteWorldFile failed for " + filename + ": " + ex.Message);
                return false;
            }
        }

        // ─── Layer 2: Baseline Writer (unchanged) ─────────────────────────────

        private void WriteBaseline()
        {
            Log("WriteBaseline: reading SpawnGroup definitions.");

            try
            {
                var factionMap = new Dictionary<string,
                    List<SpawnGroupEntry>>(StringComparer.OrdinalIgnoreCase);

                var spawnGroups = MyDefinitionManager.Static
                    .GetSpawnGroupDefinitions();

                int total = 0;

                foreach (var sg in spawnGroups)
                {
                    if (sg == null || sg.Id.SubtypeId.String == null)
                        continue;

                    string subtypeId   = sg.Id.SubtypeId.String;
                    string description = sg.DescriptionString ?? string.Empty;
                    bool   isBaseGame  = sg.Context != null
                                        && sg.Context.IsBaseGame;
                    string modSource   = isBaseGame
                        ? "BaseGame"
                        : (sg.Context != null
                            ? sg.Context.ModName + " (" + sg.Context.ModId + ")"
                            : "Unknown");

                    string rawFaction = ParseDescriptionTag(
                        description, "FactionOwner", null);

                    string faction;
                    if (rawFaction != null)
                        faction = rawFaction;
                    else if (isBaseGame)
                        faction = FACTION_VANILLA;
                    else
                        faction = FACTION_UNTAGGED_MOD;

                    bool weaponRandomizer = string.Equals(
                        ParseDescriptionTag(description,
                            "UseWeaponRandomizerOnSpawn", "false"),
                        "true", StringComparison.OrdinalIgnoreCase);

                    var entry = new SpawnGroupEntry
                    {
                        SubtypeId        = subtypeId,
                        Frequency        = sg.Frequency,
                        WeaponRandomizer = weaponRandomizer,
                        ModSource        = modSource,
                        IsMesSpawnGroup  = description.IndexOf(
                            "[Modular Encounters SpawnGroup]",
                            StringComparison.OrdinalIgnoreCase) >= 0
                    };

                    if (!factionMap.ContainsKey(faction))
                        factionMap[faction] = new List<SpawnGroupEntry>();

                    factionMap[faction].Add(entry);
                    total++;
                }

                // Mod-source promotion pass.
                if (factionMap.ContainsKey(FACTION_UNTAGGED_MOD))
                {
                    var untagged = factionMap[FACTION_UNTAGGED_MOD];
                    var byMod = new Dictionary<string, List<SpawnGroupEntry>>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in untagged)
                    {
                        string key = string.IsNullOrEmpty(entry.ModSource)
                            ? FACTION_UNTAGGED_MOD
                            : entry.ModSource;

                        if (!byMod.ContainsKey(key))
                            byMod[key] = new List<SpawnGroupEntry>();

                        byMod[key].Add(entry);
                    }

                    factionMap.Remove(FACTION_UNTAGGED_MOD);

                    foreach (var kvp in byMod)
                    {
                        string factionKey = string.Equals(
                            kvp.Key, FACTION_UNTAGGED_MOD,
                            StringComparison.OrdinalIgnoreCase)
                            ? FACTION_UNTAGGED_MOD
                            : kvp.Key;

                        if (!factionMap.ContainsKey(factionKey))
                            factionMap[factionKey] = new List<SpawnGroupEntry>();

                        factionMap[factionKey].AddRange(kvp.Value);
                    }
                }

                Log(string.Format(
                    "WriteBaseline: found {0} SpawnGroups across {1} factions.",
                    total, factionMap.Count));

                string xml = BuildBaselineXml(factionMap);

                if (WriteWorldFile(BASELINE_FILE, xml))
                    Log("WriteBaseline: wrote " + BASELINE_FILE);
                else
                    Log("WriteBaseline: FAILED to write " + BASELINE_FILE);
            }
            catch (Exception ex)
            {
                Log("WriteBaseline: exception: " + ex.ToString());
            }
        }

        private static string ParseDescriptionTag(
            string description, string tagName, string defaultValue)
        {
            if (string.IsNullOrEmpty(description))
                return defaultValue;

            string search = "[" + tagName + ":";
            int start = description.IndexOf(search,
                StringComparison.OrdinalIgnoreCase);

            if (start < 0)
                return defaultValue;

            int valueStart = start + search.Length;
            int valueEnd   = description.IndexOf(']', valueStart);

            if (valueEnd < 0)
                return defaultValue;

            return description.Substring(
                valueStart, valueEnd - valueStart).Trim();
        }

        private string BuildBaselineXml(
            Dictionary<string, List<SpawnGroupEntry>> factionMap)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<!-- MESDC-Baseline.xml");
            sb.AppendLine("     Auto-generated on server startup from live");
            sb.AppendLine("     MES SpawnGroup definitions BEFORE any MESDC");
            sb.AppendLine("     overrides are applied. DO NOT hand-edit.");
            sb.AppendLine("     Regenerated every server startup.");
            sb.AppendLine("     Run /MESDC INIT to regenerate");
            sb.AppendLine("     MESDC-Config.xml from this file. -->");
            sb.AppendLine("<MESDC>");
            sb.AppendLine();

            sb.AppendLine("  <!-- ═══════════════════════════════════════════"
                        + "═══════════════════");
            sb.AppendLine("       GLOBAL OVERRIDES");
            sb.AppendLine("       Populated with documented MES defaults.");
            sb.AppendLine("       MESDC cannot read live MES config values");
            sb.AppendLine("       from memory. Verify these against your MES");
            sb.AppendLine("       config files before editing MESDC-Config.xml.");
            sb.AppendLine("       MES config files are at:");
            sb.AppendLine("         [WorldSave]\\Storage\\"
                        + "1521905890.sbm_ModularEncountersSpawner\\");
            sb.AppendLine("       ════════════════════════════════════════════"
                        + "══════════════════ -->");
            sb.AppendLine("  <GlobalOverrides>");
            WriteGlobalOverridesDefaults(sb);
            sb.AppendLine("  </GlobalOverrides>");
            sb.AppendLine();

            sb.AppendLine("  <!-- ═══════════════════════════════════════════"
                        + "═══════════════════");
            sb.AppendLine("       FACTIONS");
            sb.AppendLine(string.Format(
                "       {0} faction(s) detected from live SpawnGroup definitions.",
                factionMap.Count));
            sb.AppendLine("       ════════════════════════════════════════════"
                        + "══════════════════ -->");
            sb.AppendLine("  <Factions>");

            var factionKeys = new List<string>(factionMap.Keys);
            bool hasVanilla     = factionKeys.Remove(FACTION_VANILLA);
            bool hasUntaggedMod = factionKeys.Remove(FACTION_UNTAGGED_MOD);
            bool hasSprt        = factionKeys.Remove("SPRT");

            factionKeys.Sort(StringComparer.OrdinalIgnoreCase);

            if (hasSprt)
                factionKeys.Insert(0, "SPRT");

            if (hasVanilla)
                factionKeys.Add(FACTION_VANILLA);
            if (hasUntaggedMod)
                factionKeys.Add(FACTION_UNTAGGED_MOD);

            foreach (string faction in factionKeys)
            {
                bool isGoby = string.Equals(
                    faction, "SPRT", StringComparison.OrdinalIgnoreCase);

                WriteFactionBlock(sb, faction, factionMap[faction], isGoby);
            }

            sb.AppendLine("  </Factions>");
            sb.AppendLine();
            sb.AppendLine("</MESDC>");

            return sb.ToString();
        }

        private void WriteGlobalOverridesDefaults(StringBuilder sb)
        {
            sb.AppendLine("    <General>");
            sb.AppendLine("      <!-- MES Config-General.xml defaults -->");
            sb.AppendLine("      <EnableLegacySpaceCargoShipDetection>true"
                        + "</EnableLegacySpaceCargoShipDetection>");
            sb.AppendLine("      <UseModIdSelectionForSpawning>true"
                        + "</UseModIdSelectionForSpawning>");
            sb.AppendLine("      <UseWeightedModIdSelection>true"
                        + "</UseWeightedModIdSelection>");
            sb.AppendLine("      <UseMaxNpcGrids>false</UseMaxNpcGrids>");
            sb.AppendLine("      <MaxGlobalNpcGrids>30</MaxGlobalNpcGrids>");
            sb.AppendLine("      <PlayerWatcherTimerTrigger>5"
                        + "</PlayerWatcherTimerTrigger>");
            sb.AppendLine("      <NpcCleanupCheckTimerTrigger>60"
                        + "</NpcCleanupCheckTimerTrigger>");
            sb.AppendLine("      <ThreatReductionHandicap>0"
                        + "</ThreatReductionHandicap>");
            sb.AppendLine("    </General>");

            sb.AppendLine("    <Grids>");
            sb.AppendLine("      <!-- MES Config-Grids.xml defaults -->");
            sb.AppendLine("      <EnableGlobalNPCWeaponRandomizer>false"
                        + "</EnableGlobalNPCWeaponRandomizer>");
            sb.AppendLine("      <RandomWeaponChance>100"
                        + "</RandomWeaponChance>");
            sb.AppendLine("      <RandomWeaponSizeVariance>-1"
                        + "</RandomWeaponSizeVariance>");
            sb.AppendLine("      <EnableGlobalNPCShieldProvider>false"
                        + "</EnableGlobalNPCShieldProvider>");
            sb.AppendLine("      <ShieldProviderChance>100"
                        + "</ShieldProviderChance>");
            sb.AppendLine("      <UseNonPhysicalAmmoForNPCs>false"
                        + "</UseNonPhysicalAmmoForNPCs>");
            sb.AppendLine("      <RemoveContainerInventoryFromNPCs>false"
                        + "</RemoveContainerInventoryFromNPCs>");
            sb.AppendLine("    </Grids>");

            sb.AppendLine("    <SpaceCargoShips />");
            sb.AppendLine("    <RandomEncounters />");
            sb.AppendLine("    <PlanetaryCargoShips />");
            sb.AppendLine("    <PlanetaryInstallations />");
            sb.AppendLine("    <BossEncounters />");
            sb.AppendLine("    <Combat />");
            sb.AppendLine("    <Cleanup />");
        }

        private void WriteFactionBlock(StringBuilder sb, string factionTag,
            List<SpawnGroupEntry> entries, bool isGoby)
        {
            sb.AppendLine();

            bool isVanilla     = string.Equals(factionTag,
                FACTION_VANILLA, StringComparison.OrdinalIgnoreCase);
            bool isUntaggedMod = string.Equals(factionTag,
                FACTION_UNTAGGED_MOD, StringComparison.OrdinalIgnoreCase);
            bool isPromotedMod = !isGoby && !isVanilla && !isUntaggedMod
                && factionTag.Contains("(");

            if (isGoby)
            {
                sb.AppendLine("    <!-- ╔════════════════════════════════════"
                            + "═══════════════════════╗");
                sb.AppendLine("         ║  " + factionTag
                            + " — GOBY faction. Full annotation in"
                            + " MESDC-Config.xml.  ║");
                sb.AppendLine("         ╚════════════════════════════════════"
                            + "═══════════════════════╝ -->");
            }
            else if (isPromotedMod)
            {
                sb.AppendLine("    <!-- Auto-promoted: mod-sourced SpawnGroups"
                            + " with no [FactionOwner] tag,");
                sb.AppendLine("         grouped by mod of origin."
                            + " Tag = ModSource value for this mod.");
                sb.AppendLine("         FactionEnabled and per-SpawnGroup"
                            + " overrides are supported. -->");
            }
            else if (isVanilla)
            {
                sb.AppendLine("    <!-- (vanilla): vanilla SE SpawnGroups with"
                            + " no [FactionOwner] tag.");
                sb.AppendLine("         These are not a real MES faction."
                            + " MESDC groups them here for reference.");
                sb.AppendLine("         FactionEnabled and per-SpawnGroup"
                            + " overrides are supported. -->");
            }
            else if (isUntaggedMod)
            {
                sb.AppendLine("    <!-- (untagged-mod): mod-added SpawnGroups"
                            + " with no [FactionOwner] tag.");
                sb.AppendLine("         These are not a real MES faction."
                            + " Check ModSource attribute to identify origin.");
                sb.AppendLine("         FactionEnabled and per-SpawnGroup"
                            + " overrides are supported. -->");
            }

            sb.AppendLine("    <Faction Tag=\"" + factionTag + "\">");
            sb.AppendLine("      <FactionEnabled>true</FactionEnabled>");
            sb.AppendLine("      <SpawnGroups>");

            foreach (var entry in entries)
            {
                sb.AppendLine("        <SpawnGroup SubtypeId=\""
                            + entry.SubtypeId + "\""
                            + " ModSource=\"" + entry.ModSource + "\">");
                sb.AppendLine("          <Enabled>true</Enabled>");
                sb.AppendLine(string.Format(
                    "          <SpawnFrequency>{0:F1}</SpawnFrequency>",
                    entry.Frequency));
                sb.AppendLine("          <WeaponRandomizerEnabled>"
                            + entry.WeaponRandomizer.ToString().ToLower()
                            + "</WeaponRandomizerEnabled>");
                sb.AppendLine("        </SpawnGroup>");
            }

            sb.AppendLine("      </SpawnGroups>");
            sb.AppendLine("      <!-- RegionGating absent in Baseline."
                        + " MESDC-only field. -->");
            sb.AppendLine("    </Faction>");
        }

        private struct SpawnGroupEntry
        {
            public string SubtypeId;
            public float  Frequency;
            public bool   WeaponRandomizer;
            public bool   IsMesSpawnGroup;
            public string ModSource;
        }

        // ─── Layer 3: Config Generator (unchanged) ────────────────────────────

        private void GenerateConfigFromBaseline()
        {
            Log("GenerateConfigFromBaseline: reading baseline.");

            string baselineXml = ReadWorldFile(BASELINE_FILE);
            if (baselineXml == null)
            {
                Log("GenerateConfigFromBaseline: baseline not found, aborting.");
                return;
            }

            try
            {
                var factions = ParseBaselineFactions(baselineXml);

                Log(string.Format(
                    "GenerateConfigFromBaseline: parsed {0} faction(s).",
                    factions.Count));

                string configXml = BuildConfigXml(factions);

                if (WriteWorldFile(CONFIG_FILE, configXml))
                    Log("GenerateConfigFromBaseline: wrote " + CONFIG_FILE);
                else
                    Log("GenerateConfigFromBaseline: FAILED to write "
                        + CONFIG_FILE);
            }
            catch (Exception ex)
            {
                Log("GenerateConfigFromBaseline: exception: " + ex.ToString());
            }
        }

        private List<BaselineFaction> ParseBaselineFactions(string xml)
        {
            var factions = new List<BaselineFaction>();

            if (string.IsNullOrEmpty(xml))
                return factions;

            BaselineFaction    currentFaction = null;
            BaselineSpawnGroup currentSg      = new BaselineSpawnGroup();
            bool               inSpawnGroup   = false;

            string[] lines = xml.Split('\n');

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                if (line.Length == 0
                    || line.StartsWith("<!--")
                    || line.StartsWith("<?"))
                    continue;

                if (line.StartsWith("<Faction "))
                {
                    string tag = ParseXmlAttribute(line, "Tag");
                    currentFaction = new BaselineFaction
                    {
                        Tag         = tag,
                        SpawnGroups = new List<BaselineSpawnGroup>()
                    };
                    factions.Add(currentFaction);
                    inSpawnGroup = false;
                    continue;
                }

                if (line.StartsWith("</Faction>"))
                {
                    currentFaction = null;
                    inSpawnGroup   = false;
                    continue;
                }

                if (currentFaction == null)
                    continue;

                if (line.StartsWith("<SpawnGroup "))
                {
                    currentSg = new BaselineSpawnGroup
                    {
                        SubtypeId        = ParseXmlAttribute(line, "SubtypeId"),
                        ModSource        = ParseXmlAttribute(line, "ModSource"),
                        Frequency        = 1.0f,
                        WeaponRandomizer = false
                    };
                    inSpawnGroup = true;
                    continue;
                }

                if (line.StartsWith("</SpawnGroup>"))
                {
                    if (inSpawnGroup)
                        currentFaction.SpawnGroups.Add(currentSg);

                    inSpawnGroup = false;
                    continue;
                }

                if (!inSpawnGroup)
                    continue;

                string elemVal;

                if (TryParseXmlElement(line, "SpawnFrequency", out elemVal))
                {
                    float f;
                    if (float.TryParse(elemVal,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out f))
                    {
                        currentSg.Frequency = f;
                    }
                    continue;
                }

                if (TryParseXmlElement(line, "WeaponRandomizerEnabled",
                    out elemVal))
                {
                    bool b;
                    bool.TryParse(elemVal, out b);
                    currentSg.WeaponRandomizer = b;
                    continue;
                }
            }

            return factions;
        }

        private static string ParseXmlAttribute(string line, string attrName)
        {
            string search = attrName + "=\"";
            int start = line.IndexOf(search, StringComparison.Ordinal);
            if (start < 0)
                return string.Empty;

            int valueStart = start + search.Length;
            int valueEnd   = line.IndexOf('"', valueStart);
            if (valueEnd < 0)
                return string.Empty;

            return line.Substring(valueStart, valueEnd - valueStart);
        }

        private static bool TryParseXmlElement(string line, string elemName,
            out string value)
        {
            value = string.Empty;
            string open  = "<"  + elemName + ">";
            string close = "</" + elemName + ">";

            int start = line.IndexOf(open, StringComparison.Ordinal);
            if (start < 0)
                return false;

            int valueStart = start + open.Length;
            int valueEnd   = line.IndexOf(close, valueStart,
                StringComparison.Ordinal);

            if (valueEnd < 0)
                return false;

            value = line.Substring(valueStart, valueEnd - valueStart).Trim();
            return true;
        }

        private string BuildConfigXml(List<BaselineFaction> factions)
        {
            var sb = new StringBuilder();

            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<!-- MESDC-Config.xml");
            sb.AppendLine("     Admin override file. Edit this file to");
            sb.AppendLine("     customise MES spawn behaviour.");
            sb.AppendLine("     Omit or comment out any element to leave");
            sb.AppendLine("     the corresponding MES value untouched.");
            sb.AppendLine("     ───────────────────────────────────────────");
            sb.AppendLine("     WARNING: /MESDC INIT will overwrite this");
            sb.AppendLine("     file without confirmation. Back up your");
            sb.AppendLine("     settings before running that command.");
            sb.AppendLine("     A server restart is required for changes");
            sb.AppendLine("     to take effect. -->");
            sb.AppendLine("<MESDC>");
            sb.AppendLine();

            WriteConfigGlobalOverrides(sb);

            sb.AppendLine("  <!-- ═══════════════════════════════════════════"
                        + "═══════════════════");
            sb.AppendLine("       FACTIONS");
            sb.AppendLine("       ════════════════════════════════════════════"
                        + "══════════════════ -->");
            sb.AppendLine("  <Factions>");

            foreach (var faction in factions)
            {
                bool isGoby = string.Equals(
                    faction.Tag, "SPRT", StringComparison.OrdinalIgnoreCase);

                bool isReserved =
                    string.Equals(faction.Tag, FACTION_VANILLA,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(faction.Tag, FACTION_UNTAGGED_MOD,
                        StringComparison.OrdinalIgnoreCase);

                if (isGoby)
                    WriteConfigGobyFaction(sb, faction);
                else if (isReserved)
                    WriteConfigReservedFaction(sb, faction);
                else
                    WriteConfigMinimalFaction(sb, faction);
            }

            sb.AppendLine("  </Factions>");
            sb.AppendLine();
            sb.AppendLine("</MESDC>");

            return sb.ToString();
        }

        private void WriteConfigGlobalOverrides(StringBuilder sb)
        {
            sb.AppendLine("  <!-- ═══════════════════════════════════════════"
                        + "═══════════════════");
            sb.AppendLine("       GLOBAL OVERRIDES");
            sb.AppendLine("       These values are written to the corresponding");
            sb.AppendLine("       MES Config-*.xml files on server startup.");
            sb.AppendLine("       Omit any element to leave MES defaults.");
            sb.AppendLine("       Verify current MES values at:");
            sb.AppendLine("         [WorldSave]\\Storage\\"
                        + "1521905890.sbm_ModularEncountersSpawner\\");
            sb.AppendLine("       ════════════════════════════════════════════"
                        + "══════════════════ -->");
            sb.AppendLine("  <GlobalOverrides>");

            sb.AppendLine("    <General>");
            sb.AppendLine("      <!-- <EnableLegacySpaceCargoShipDetection>true"
                        + "</EnableLegacySpaceCargoShipDetection> -->");
            sb.AppendLine("      <!-- <UseModIdSelectionForSpawning>true"
                        + "</UseModIdSelectionForSpawning> -->");
            sb.AppendLine("      <!-- <UseWeightedModIdSelection>true"
                        + "</UseWeightedModIdSelection> -->");
            sb.AppendLine("      <!-- <LowWeightModIdSpawnGroups>5"
                        + "</LowWeightModIdSpawnGroups> -->");
            sb.AppendLine("      <!-- <LowWeightModIdModifier>1"
                        + "</LowWeightModIdModifier> -->");
            sb.AppendLine("      <!-- <MediumWeightModIdSpawnGroups>15"
                        + "</MediumWeightModIdSpawnGroups> -->");
            sb.AppendLine("      <!-- <MediumWeightModIdModifier>2"
                        + "</MediumWeightModIdModifier> -->");
            sb.AppendLine("      <!-- <HighWeightModIdSpawnGroups>16"
                        + "</HighWeightModIdSpawnGroups> -->");
            sb.AppendLine("      <!-- <HighWeightModIdModifier>3"
                        + "</HighWeightModIdModifier> -->");
            sb.AppendLine("      <!-- <UseMaxNpcGrids>false</UseMaxNpcGrids> -->");
            sb.AppendLine("      <!-- <MaxGlobalNpcGrids>30"
                        + "</MaxGlobalNpcGrids> -->");
            sb.AppendLine("      <!-- <PlayerWatcherTimerTrigger>5"
                        + "</PlayerWatcherTimerTrigger> -->");
            sb.AppendLine("      <!-- <NpcDistanceCheckTimerTrigger>5"
                        + "</NpcDistanceCheckTimerTrigger> -->");
            sb.AppendLine("      <!-- <NpcOwnershipCheckTimerTrigger>60"
                        + "</NpcOwnershipCheckTimerTrigger> -->");
            sb.AppendLine("      <!-- <NpcCleanupCheckTimerTrigger>60"
                        + "</NpcCleanupCheckTimerTrigger> -->");
            sb.AppendLine("      <!-- <ThreatReductionHandicap>0"
                        + "</ThreatReductionHandicap> -->");
            sb.AppendLine("      <!-- <PlanetSpawnsDisableList>");
            sb.AppendLine("             <string>EarthLike</string>");
            sb.AppendLine("           </PlanetSpawnsDisableList> -->");
            sb.AppendLine("      <!-- <NpcSpawnGroupBlacklist>");
            sb.AppendLine("             <string>SpawnGroupSubtypeIdHere</string>");
            sb.AppendLine("           </NpcSpawnGroupBlacklist> -->");
            sb.AppendLine("    </General>");

            sb.AppendLine("    <Grids>");
            sb.AppendLine("      <!-- <EnableGlobalNPCWeaponRandomizer>false"
                        + "</EnableGlobalNPCWeaponRandomizer> -->");
            sb.AppendLine("      <!-- <RandomWeaponChance>100"
                        + "</RandomWeaponChance> -->");
            sb.AppendLine("      <!-- <RandomWeaponSizeVariance>-1"
                        + "</RandomWeaponSizeVariance> -->");
            sb.AppendLine("      <!-- <WeaponReplacerBlacklist>");
            sb.AppendLine("             <string>WeaponSubtypeOrModIdHere</string>");
            sb.AppendLine("           </WeaponReplacerBlacklist> -->");
            sb.AppendLine("      <!-- <WeaponReplacerWhitelist>");
            sb.AppendLine("             <string>WeaponSubtypeOrModIdHere</string>");
            sb.AppendLine("           </WeaponReplacerWhitelist> -->");
            sb.AppendLine("      <!-- <WeaponReplacerTargetBlacklist>");
            sb.AppendLine("             <string>WeaponSubtypeOrModIdHere</string>");
            sb.AppendLine("           </WeaponReplacerTargetBlacklist> -->");
            sb.AppendLine("      <!-- <WeaponReplacerTargetWhitelist>");
            sb.AppendLine("             <string>WeaponSubtypeOrModIdHere</string>");
            sb.AppendLine("           </WeaponReplacerTargetWhitelist> -->");
            sb.AppendLine("      <!-- <WeaponReplacerUseTotalGridMassMultiplier>"
                        + "false</WeaponReplacerUseTotalGridMassMultiplier> -->");
            sb.AppendLine("      <!-- <WeaponReplacerTotalGridMassMultiplier>1.5"
                        + "</WeaponReplacerTotalGridMassMultiplier> -->");
            sb.AppendLine("      <!-- <RandomizedWeaponsUseFullRange>false"
                        + "</RandomizedWeaponsUseFullRange> -->");
            sb.AppendLine("      <!-- <EnableGlobalNPCShieldProvider>false"
                        + "</EnableGlobalNPCShieldProvider> -->");
            sb.AppendLine("      <!-- <ShieldProviderChance>100"
                        + "</ShieldProviderChance> -->");
            sb.AppendLine("      <!-- <UseGlobalBlockReplacer>false"
                        + "</UseGlobalBlockReplacer> -->");
            sb.AppendLine("      <!-- <GlobalBlockReplacerReference>");
            sb.AppendLine("             <string>OldBlockId|NewBlockId</string>");
            sb.AppendLine("           </GlobalBlockReplacerReference> -->");
            sb.AppendLine("      <!-- <UseNonPhysicalAmmoForNPCs>false"
                        + "</UseNonPhysicalAmmoForNPCs> -->");
            sb.AppendLine("      <!-- <RemoveContainerInventoryFromNPCs>false"
                        + "</RemoveContainerInventoryFromNPCs> -->");
            sb.AppendLine("      <!-- <UseMaxAmmoInventoryWeight>false"
                        + "</UseMaxAmmoInventoryWeight> -->");
            sb.AppendLine("      <!-- <MaxAmmoInventoryWeight>0"
                        + "</MaxAmmoInventoryWeight> -->");
            sb.AppendLine("    </Grids>");

            sb.AppendLine("    <SpaceCargoShips />");
            sb.AppendLine("    <RandomEncounters />");
            sb.AppendLine("    <PlanetaryCargoShips />");
            sb.AppendLine("    <PlanetaryInstallations />");
            sb.AppendLine("    <BossEncounters />");
            sb.AppendLine("    <Combat />");
            sb.AppendLine("    <Cleanup />");
            sb.AppendLine("  </GlobalOverrides>");
            sb.AppendLine();
        }

        private void WriteConfigGobyFaction(StringBuilder sb,
            BaselineFaction faction)
        {
            sb.AppendLine();
            sb.AppendLine("    <!-- ╔══════════════════════════════════════════"
                        + "═══════════════════╗");
            sb.AppendLine("         ║  SPRT — Space Pirates (vanilla Keen"
                        + " faction)                    ║");
            sb.AppendLine("         ║  GOBY: Every available override is shown"
                        + " and documented.    ║");
            sb.AppendLine("         ║  Copy elements from this block into other"
                        + " faction blocks.   ║");
            sb.AppendLine("         ╚══════════════════════════════════════════"
                        + "═══════════════════╝ -->");
            sb.AppendLine("    <Faction Tag=\"SPRT\">");
            sb.AppendLine();
            sb.AppendLine("      <!-- Disable this entire faction.");
            sb.AppendLine("           All SpawnGroups are blacklisted at load.");
            sb.AppendLine("           Default: true -->");
            sb.AppendLine("      <FactionEnabled>true</FactionEnabled>");
            sb.AppendLine();
            sb.AppendLine("      <!-- ~~~ Region Gating (MESDC feature) ~~~~~~");
            sb.AppendLine("           Mode: Lock   = ONLY spawns inside region");
            sb.AppendLine("                 Exclude = NEVER spawns inside region");
            sb.AppendLine("           Region types: GPS, Planet, Gravity");
            sb.AppendLine("           Remove this block entirely for no gating.");
            sb.AppendLine("           Multiple Region elements = OR logic. -->");
            sb.AppendLine("      <!-- <RegionGating Mode=\"Lock\">");
            sb.AppendLine("             <Region Type=\"GPS\">");
            sb.AppendLine("               <Label>ExampleRegion</Label>");
            sb.AppendLine("               <X>0</X><Y>0</Y><Z>0</Z>");
            sb.AppendLine("               <RadiusM>100000</RadiusM>");
            sb.AppendLine("             </Region>");
            sb.AppendLine("             <Region Type=\"Planet\">");
            sb.AppendLine("               <PlanetName>EarthLike</PlanetName>");
            sb.AppendLine("             </Region>");
            sb.AppendLine("             <Region Type=\"Gravity\">");
            sb.AppendLine("               <MinG>0.0</MinG>");
            sb.AppendLine("               <MaxG>0.5</MaxG>");
            sb.AppendLine("             </Region>");
            sb.AppendLine("           </RegionGating> -->");
            sb.AppendLine();
            sb.AppendLine("      <SpawnGroups>");

            foreach (var sg in faction.SpawnGroups)
            {
                sb.AppendLine("        <!-- ModSource: " + sg.ModSource + " -->");
                sb.AppendLine("        <SpawnGroup SubtypeId=\""
                            + sg.SubtypeId + "\">");
                sb.AppendLine();
                sb.AppendLine("          <!-- Blacklist this SpawnGroup."
                            + " Default: true -->");
                sb.AppendLine("          <Enabled>true</Enabled>");
                sb.AppendLine();
                sb.AppendLine("          <!-- Absolute spawn frequency.");
                sb.AppendLine("               Scale: 0.0 (never) to 10.0 (max).");
                sb.AppendLine("               Omit to keep authored value.");
                sb.AppendLine("               Authored value: "
                            + sg.Frequency.ToString("F1") + " -->");
                sb.AppendLine("          <!-- <SpawnFrequency>"
                            + sg.Frequency.ToString("F1")
                            + "</SpawnFrequency> -->");
                sb.AppendLine();
                sb.AppendLine("          <!-- Enable weapon randomization.");
                sb.AppendLine("               Requires EnableGlobalNPCWeaponRandomizer.");
                sb.AppendLine("               Default: false -->");
                sb.AppendLine("          <WeaponRandomizerEnabled>"
                            + sg.WeaponRandomizer.ToString().ToLower()
                            + "</WeaponRandomizerEnabled>");
                sb.AppendLine();
                sb.AppendLine("        </SpawnGroup>");
                sb.AppendLine();
            }

            sb.AppendLine("      </SpawnGroups>");
            sb.AppendLine("    </Faction>");
        }

        private void WriteConfigMinimalFaction(StringBuilder sb,
            BaselineFaction faction)
        {
            sb.AppendLine();
            sb.AppendLine("    <!-- ── " + faction.Tag
                        + " ─────────────────────────────────────────────");
            sb.AppendLine("         Copy override elements from the SPRT GOBY");
            sb.AppendLine("         block above as needed. -->");
            sb.AppendLine("    <Faction Tag=\"" + faction.Tag + "\">");
            sb.AppendLine("      <FactionEnabled>true</FactionEnabled>");
            sb.AppendLine("      <!-- <RegionGating Mode=\"Lock\"> ... "
                        + "</RegionGating> -->");
            sb.AppendLine("      <SpawnGroups>");

            foreach (var sg in faction.SpawnGroups)
            {
                sb.AppendLine("        {SpawnGroup SubtypeId=\""
                            + sg.SubtypeId + "\"");
                sb.AppendLine("          Enabled: true");
                sb.AppendLine("          SpawnFrequency authored: "
                            + sg.Frequency.ToString("F1")
                            + "  (add SpawnFrequency element to override)");
                sb.AppendLine("          WeaponRandomizerEnabled: false}");
            }

            sb.AppendLine("      </SpawnGroups>");
            sb.AppendLine("    </Faction>");
        }

        private void WriteConfigReservedFaction(StringBuilder sb,
            BaselineFaction faction)
        {
            bool isVanilla = string.Equals(faction.Tag, FACTION_VANILLA,
                StringComparison.OrdinalIgnoreCase);

            sb.AppendLine();
            sb.AppendLine("    <!-- ── " + faction.Tag
                        + " ─────────────────────────────────────");
            if (isVanilla)
                sb.AppendLine("         Vanilla SE SpawnGroups. -->");
            else
                sb.AppendLine("         Mod-added untagged SpawnGroups. -->");

            sb.AppendLine("    <Faction Tag=\"" + faction.Tag + "\">");
            sb.AppendLine("      <FactionEnabled>true</FactionEnabled>");
            sb.AppendLine("      <SpawnGroups>");

            foreach (var sg in faction.SpawnGroups)
            {
                sb.AppendLine("        {SpawnGroup SubtypeId=\""
                            + sg.SubtypeId + "\"");
                sb.AppendLine("          Enabled: true");
                sb.AppendLine("          SpawnFrequency authored: "
                            + sg.Frequency.ToString("F1")
                            + "  (add SpawnFrequency element to override)");
                sb.AppendLine("          WeaponRandomizerEnabled: false}");
            }

            sb.AppendLine("      </SpawnGroups>");
            sb.AppendLine("    </Faction>");
        }

        private class BaselineFaction
        {
            public string Tag;
            public List<BaselineSpawnGroup> SpawnGroups;
        }

        private struct BaselineSpawnGroup
        {
            public string SubtypeId;
            public string ModSource;
            public float  Frequency;
            public bool   WeaponRandomizer;
        }

        // ─── Layer 4: Apply Pass ──────────────────────────────────────────────

        /// <summary>
        /// Reads MESDC-Config.xml and applies all overrides. Called once from
        /// UpdateAfterSimulation after MES API becomes ready.
        /// 
        /// Apply order:
        ///   1. GlobalOverrides → write to MES Config-*.xml files on disk
        ///   2. Faction.FactionEnabled=false → ToggleSpawnGroupEnabled(id, false)
        ///      for every SpawnGroup in that faction
        ///   3. SpawnGroup.Enabled=false → ToggleSpawnGroupEnabled(id, false)
        ///   4. SpawnGroup.SpawnFrequency → patch Frequency on live definition
        ///   5. SpawnGroup.WeaponRandomizerEnabled=true → patch DescriptionString
        /// </summary>
        private void ApplyConfig()
        {
            Log("ApplyConfig: starting.");

            string configXml = ReadWorldFile(CONFIG_FILE);
            if (configXml == null)
            {
                Log("ApplyConfig: no config file found, nothing to apply.");
                return;
            }

            var config = ParseConfig(configXml);

            // ── 1. GlobalOverrides ────────────────────────────────────────────
            ApplyGlobalOverrides(config.GlobalOverrides);

            // ── 2–5. Faction and SpawnGroup overrides ─────────────────────────
            // Build a lookup of live SpawnGroup definitions for fast access.
            var liveDefinitions = BuildSpawnGroupLookup();

            int toggled   = 0;
            int patched   = 0;
            int wrPatched = 0;

            foreach (var faction in config.Factions)
            {
                bool factionEnabled = faction.FactionEnabled;

                foreach (var sg in faction.SpawnGroups)
                {
                    // FactionEnabled=false overrides individual SpawnGroup
                    // enabled state — if the faction is off, everything is off.
                    bool shouldBeEnabled = factionEnabled && sg.Enabled;

                    if (!shouldBeEnabled)
                    {
                        _mes.ToggleSpawnGroupEnabled(sg.SubtypeId, false);
                        toggled++;
                        Log(string.Format(
                            "ApplyConfig: disabled SpawnGroup '{0}'" +
                            " (faction enabled={1}, sg enabled={2})",
                            sg.SubtypeId, factionEnabled, sg.Enabled));
                    }

                    // SpawnFrequency — patch live definition directly.
                    if (sg.FrequencyOverride.HasValue)
                    {
                        MySpawnGroupDefinition def;
                        if (liveDefinitions.TryGetValue(
                            sg.SubtypeId, out def))
                        {
                            def.Frequency = sg.FrequencyOverride.Value;
                            patched++;
                            Log(string.Format(
                                "ApplyConfig: set Frequency={0:F1} on '{1}'",
                                sg.FrequencyOverride.Value, sg.SubtypeId));
                        }
                        else
                        {
                            Log(string.Format(
                                "ApplyConfig: WARNING — SpawnGroup '{0}'" +
                                " not found in live definitions for" +
                                " frequency patch.", sg.SubtypeId));
                        }
                    }

                    // WeaponRandomizerEnabled — patch DescriptionString.
                    if (sg.WeaponRandomizerOverride.HasValue)
                    {
                        MySpawnGroupDefinition def;
                        if (liveDefinitions.TryGetValue(
                            sg.SubtypeId, out def))
                        {
                            PatchDescriptionTag(def,
                                "UseWeaponRandomizerOnSpawn",
                                sg.WeaponRandomizerOverride.Value
                                    ? "true" : "false");
                            wrPatched++;
                            Log(string.Format(
                                "ApplyConfig: set UseWeaponRandomizerOnSpawn" +
                                "={0} on '{1}'",
                                sg.WeaponRandomizerOverride.Value,
                                sg.SubtypeId));
                        }
                        else
                        {
                            Log(string.Format(
                                "ApplyConfig: WARNING — SpawnGroup '{0}'" +
                                " not found for weapon randomizer patch.",
                                sg.SubtypeId));
                        }
                    }
                }
            }

            Log(string.Format(
                "ApplyConfig: complete. " +
                "{0} SpawnGroups disabled, " +
                "{1} frequencies patched, " +
                "{2} weapon randomizer flags patched.",
                toggled, patched, wrPatched));
        }

        /// <summary>
        /// Builds a case-insensitive dictionary of SubtypeId → live definition
        /// from MyDefinitionManager for fast lookup during the apply pass.
        /// </summary>
        private Dictionary<string, MySpawnGroupDefinition> BuildSpawnGroupLookup()
        {
            var lookup = new Dictionary<string, MySpawnGroupDefinition>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var sg in MyDefinitionManager.Static
                .GetSpawnGroupDefinitions())
            {
                if (sg == null || sg.Id.SubtypeId.String == null)
                    continue;

                string key = sg.Id.SubtypeId.String;
                if (!lookup.ContainsKey(key))
                    lookup[key] = sg;
            }

            return lookup;
        }

        /// <summary>
        /// Patches or inserts a MES description tag in a live SpawnGroup
        /// definition's DescriptionString. If the tag already exists it is
        /// replaced in-place; otherwise it is appended.
        /// </summary>
        private static void PatchDescriptionTag(
            MySpawnGroupDefinition def, string tagName, string value)
        {
            string current = def.DescriptionString ?? string.Empty;
            string search  = "[" + tagName + ":";
            int    start   = current.IndexOf(search,
                StringComparison.OrdinalIgnoreCase);

            if (start >= 0)
            {
                // Replace existing tag value.
                int valueStart = start + search.Length;
                int valueEnd   = current.IndexOf(']', valueStart);
                if (valueEnd >= 0)
                {
                    def.DescriptionString =
                        current.Substring(0, valueStart)
                        + value
                        + current.Substring(valueEnd);
                    return;
                }
            }

            // Tag not present — append it.
            def.DescriptionString = current.TrimEnd()
                + "\n[" + tagName + ":" + value + "]";
        }

        // ─── GlobalOverrides apply ────────────────────────────────────────────

        /// <summary>
        /// Writes GlobalOverrides values to MES Config-*.xml files on disk.
        /// Only elements present (non-null) in the parsed config are written.
        /// Each MES config file is read, the relevant fields updated in-place
        /// using the same hand-rolled string approach, then written back.
        /// </summary>
        private void ApplyGlobalOverrides(ConfigGlobalOverrides overrides)
        {
            if (overrides == null)
            {
                Log("ApplyGlobalOverrides: no GlobalOverrides in config.");
                return;
            }

            Log("ApplyGlobalOverrides: applying to MES config files.");

            // MES config files live in the MES storage folder.
            // MESDC writes to them directly using the MES storage key.
            // Key format: the Type passed to WriteFileInWorldStorage must
            // match what MES used — we use a string-keyed approach via
            // direct file path instead, since MES owns that storage slot.
            // We write only the fields that are present in our config.

            ApplyMesConfigFile("Config-General.xml",
                overrides.GeneralFields);

            ApplyMesConfigFile("Config-Grids.xml",
                overrides.GridsFields);

            ApplyMesConfigFile("Config-SpaceCargoShips.xml",
                overrides.SpaceCargoShipFields);

            ApplyMesConfigFile("Config-RandomEncounters.xml",
                overrides.RandomEncounterFields);

            ApplyMesConfigFile("Config-PlanetaryCargoShips.xml",
                overrides.PlanetaryCargoShipFields);

            ApplyMesConfigFile("Config-PlanetaryInstallations.xml",
                overrides.PlanetaryInstallationFields);

            ApplyMesConfigFile("Config-BossEncounters.xml",
                overrides.BossEncounterFields);

            ApplyMesConfigFile("Config-Combat.xml",
                overrides.CombatFields);

            // Cleanup fields go into all encounter type files.
            if (overrides.CleanupFields != null
                && overrides.CleanupFields.Count > 0)
            {
                string[] cleanupTargets = {
                    "Config-SpaceCargoShips.xml",
                    "Config-RandomEncounters.xml",
                    "Config-PlanetaryCargoShips.xml",
                    "Config-PlanetaryInstallations.xml",
                    "Config-BossEncounters.xml"
                };

                foreach (string target in cleanupTargets)
                    ApplyMesConfigFile(target, overrides.CleanupFields);
            }

            Log("ApplyGlobalOverrides: complete.");
        }

        /// <summary>
        /// Reads a MES config file from its storage folder, patches the
        /// provided field values using element-level string replacement,
        /// and writes the result back. Fields not present in the file are
        /// appended before the closing root tag.
        /// </summary>
        private void ApplyMesConfigFile(string filename,
            Dictionary<string, string> fields)
        {
            if (fields == null || fields.Count == 0)
                return;

            // MES stores its config files under its own storage type key.
            // We read/write them using the MES session component type name
            // via MyAPIGateway.Utilities — but MES uses a different Type.
            // The correct path is direct Storage folder access.
            // SE allows reading/writing any file under the world Storage
            // folder if you know the subfolder name.
            // We construct the path manually and use TextReader/TextWriter
            // via the GamePaths API.

            string mesFolderName = "1521905890.sbm_ModularEncountersSpawner";
            string worldStorage  = MyAPIGateway.Utilities.GamePaths
                .UserDataPath;

            // On dedicated server, UserDataPath points to the server's
            // instance folder. Storage lives at:
            // [InstancePath]\Saves\[WorldName]\Storage\[FolderName]\
            // We need to locate the active world save path.
            // MyAPIGateway.Session.CurrentPath gives us the world folder.
            string mesFolder = System.IO.Path.Combine(
                MyAPIGateway.Session.CurrentPath,
                "Storage",
                mesFolderName);

            string filePath = System.IO.Path.Combine(mesFolder, filename);

            // If the MES config file doesn't exist yet, MES hasn't been
            // run with this world. Skip — MES will create defaults on next
            // load and MESDC will patch on the subsequent startup.
            if (!System.IO.File.Exists(filePath))
            {
                Log(string.Format(
                    "ApplyMesConfigFile: {0} not found — skipping " +
                    "(MES will create on first run).", filename));
                return;
            }

            string content;
            try
            {
                content = System.IO.File.ReadAllText(filePath);
            }
            catch (Exception ex)
            {
                Log("ApplyMesConfigFile: failed to read " + filename
                    + ": " + ex.Message);
                return;
            }

            bool modified = false;

            foreach (var kvp in fields)
            {
                string elemName = kvp.Key;
                string newValue = kvp.Value;
                string open     = "<"  + elemName + ">";
                string close    = "</" + elemName + ">";

                int start = content.IndexOf(open, StringComparison.Ordinal);
                if (start >= 0)
                {
                    int valueStart = start + open.Length;
                    int valueEnd   = content.IndexOf(close, valueStart,
                        StringComparison.Ordinal);

                    if (valueEnd >= 0)
                    {
                        string oldValue = content.Substring(
                            valueStart, valueEnd - valueStart);

                        if (oldValue != newValue)
                        {
                            content = content.Substring(0, valueStart)
                                    + newValue
                                    + content.Substring(valueEnd);
                            modified = true;

                            Log(string.Format(
                                "ApplyMesConfigFile: {0} — set {1}={2}",
                                filename, elemName, newValue));
                        }
                        continue;
                    }
                }

                // Element not found — append before closing root tag.
                // Find the last closing tag and insert before it.
                int lastClose = content.LastIndexOf("</",
                    StringComparison.Ordinal);

                if (lastClose >= 0)
                {
                    string insertion = "\n  " + open + newValue + close;
                    content = content.Substring(0, lastClose)
                            + insertion
                            + content.Substring(lastClose);
                    modified = true;

                    Log(string.Format(
                        "ApplyMesConfigFile: {0} — appended {1}={2}",
                        filename, elemName, newValue));
                }
            }

            if (!modified)
                return;

            try
            {
                System.IO.File.WriteAllText(filePath, content);
                Log("ApplyMesConfigFile: wrote " + filename);
            }
            catch (Exception ex)
            {
                Log("ApplyMesConfigFile: failed to write " + filename
                    + ": " + ex.Message);
            }
        }

        // ─── Config Parser ────────────────────────────────────────────────────

        /// <summary>
        /// Parses MESDC-Config.xml into a ConfigDocument using the same
        /// hand-rolled line parser as ParseBaselineFactions. Only elements
        /// that are actually present (uncommented) in the file are parsed —
        /// absent elements are left as null/HasValue=false so the apply pass
        /// knows to skip them.
        /// </summary>
        private ConfigDocument ParseConfig(string xml)
        {
            var doc = new ConfigDocument
            {
                GlobalOverrides = new ConfigGlobalOverrides
                {
                    GeneralFields              = new Dictionary<string, string>(),
                    GridsFields                = new Dictionary<string, string>(),
                    SpaceCargoShipFields       = new Dictionary<string, string>(),
                    RandomEncounterFields      = new Dictionary<string, string>(),
                    PlanetaryCargoShipFields   = new Dictionary<string, string>(),
                    PlanetaryInstallationFields= new Dictionary<string, string>(),
                    BossEncounterFields        = new Dictionary<string, string>(),
                    CombatFields               = new Dictionary<string, string>(),
                    CleanupFields              = new Dictionary<string, string>()
                },
                Factions = new List<ConfigFaction>()
            };

            // Section tracking.
            string         currentSection  = null;
            ConfigFaction  currentFaction  = null;
            ConfigSgEntry  currentSg       = new ConfigSgEntry();
            bool           inSpawnGroup    = false;

            string[] lines = xml.Split('\n');

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();

                if (line.Length == 0
                    || line.StartsWith("<!--")
                    || line.StartsWith("<?"))
                    continue;

                // ── Section tracking ──────────────────────────────────────────
                if (line.StartsWith("<General>"))
                { currentSection = "General"; continue; }
                if (line.StartsWith("</General>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<Grids>"))
                { currentSection = "Grids"; continue; }
                if (line.StartsWith("</Grids>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<SpaceCargoShips>"))
                { currentSection = "SpaceCargoShips"; continue; }
                if (line.StartsWith("</SpaceCargoShips>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<RandomEncounters>"))
                { currentSection = "RandomEncounters"; continue; }
                if (line.StartsWith("</RandomEncounters>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<PlanetaryCargoShips>"))
                { currentSection = "PlanetaryCargoShips"; continue; }
                if (line.StartsWith("</PlanetaryCargoShips>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<PlanetaryInstallations>"))
                { currentSection = "PlanetaryInstallations"; continue; }
                if (line.StartsWith("</PlanetaryInstallations>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<BossEncounters>"))
                { currentSection = "BossEncounters"; continue; }
                if (line.StartsWith("</BossEncounters>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<Combat>"))
                { currentSection = "Combat"; continue; }
                if (line.StartsWith("</Combat>"))
                { currentSection = null; continue; }

                if (line.StartsWith("<Cleanup>"))
                { currentSection = "Cleanup"; continue; }
                if (line.StartsWith("</Cleanup>"))
                { currentSection = null; continue; }

                // ── GlobalOverrides field capture ─────────────────────────────
                if (currentSection != null && currentFaction == null)
                {
                    // Any <ElementName>value</ElementName> on this line goes
                    // into the appropriate section dictionary.
                    string elemName;
                    string elemVal;
                    if (TryParseAnyXmlElement(line, out elemName, out elemVal))
                    {
                        var dict = GetSectionDict(
                            doc.GlobalOverrides, currentSection);
                        if (dict != null && !dict.ContainsKey(elemName))
                            dict[elemName] = elemVal;
                    }
                    continue;
                }

                // ── Faction blocks ────────────────────────────────────────────
                if (line.StartsWith("<Faction "))
                {
                    currentFaction = new ConfigFaction
                    {
                        Tag          = ParseXmlAttribute(line, "Tag"),
                        FactionEnabled = true,
                        SpawnGroups  = new List<ConfigSgEntry>()
                    };
                    doc.Factions.Add(currentFaction);
                    inSpawnGroup   = false;
                    currentSection = null;
                    continue;
                }

                if (line.StartsWith("</Faction>"))
                {
                    currentFaction = null;
                    inSpawnGroup   = false;
                    continue;
                }

                if (currentFaction == null)
                    continue;

                // FactionEnabled
                string feval;
                if (TryParseXmlElement(line, "FactionEnabled", out feval))
                {
                    bool b;
                    bool.TryParse(feval, out b);
                    currentFaction.FactionEnabled = b;
                    continue;
                }

                // SpawnGroup open tag
                if (line.StartsWith("<SpawnGroup "))
                {
                    currentSg = new ConfigSgEntry
                    {
                        SubtypeId              = ParseXmlAttribute(line, "SubtypeId"),
                        Enabled                = true,
                        FrequencyOverride      = null,
                        WeaponRandomizerOverride = null
                    };
                    inSpawnGroup = true;
                    continue;
                }

                // SpawnGroup close tag
                if (line.StartsWith("</SpawnGroup>"))
                {
                    if (inSpawnGroup)
                        currentFaction.SpawnGroups.Add(currentSg);
                    inSpawnGroup = false;
                    continue;
                }

                if (!inSpawnGroup)
                    continue;

                // SpawnGroup child elements
                string val;

                if (TryParseXmlElement(line, "Enabled", out val))
                {
                    bool b;
                    bool.TryParse(val, out b);
                    currentSg.Enabled = b;
                    continue;
                }

                if (TryParseXmlElement(line, "SpawnFrequency", out val))
                {
                    float f;
                    if (float.TryParse(val,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out f))
                    {
                        currentSg.FrequencyOverride = f;
                    }
                    continue;
                }

                if (TryParseXmlElement(line, "WeaponRandomizerEnabled", out val))
                {
                    bool b;
                    bool.TryParse(val, out b);
                    currentSg.WeaponRandomizerOverride = b;
                    continue;
                }
            }

            return doc;
        }

        /// <summary>
        /// Attempts to parse any single-line XML element, returning its name
        /// and inner text. Used for GlobalOverrides field capture where element
        /// names are not known in advance.
        /// </summary>
        private static bool TryParseAnyXmlElement(string line,
            out string elemName, out string elemValue)
        {
            elemName  = string.Empty;
            elemValue = string.Empty;

            int openStart = line.IndexOf('<');
            if (openStart < 0 || line.StartsWith("</"))
                return false;

            int nameEnd = line.IndexOf('>', openStart + 1);
            if (nameEnd < 0)
                return false;

            string name = line.Substring(openStart + 1, nameEnd - openStart - 1);

            // Skip tags with attributes (they're container elements, not values)
            if (name.Contains(' ') || name.Contains('/'))
                return false;

            string closeTag = "</" + name + ">";
            int closeStart  = line.IndexOf(closeTag, nameEnd,
                StringComparison.Ordinal);

            if (closeStart < 0)
                return false;

            elemName  = name;
            elemValue = line.Substring(nameEnd + 1,
                closeStart - nameEnd - 1).Trim();
            return true;
        }

        /// <summary>
        /// Returns the correct field dictionary for a given section name.
        /// </summary>
        private static Dictionary<string, string> GetSectionDict(
            ConfigGlobalOverrides overrides, string section)
        {
            switch (section)
            {
                case "General":                return overrides.GeneralFields;
                case "Grids":                  return overrides.GridsFields;
                case "SpaceCargoShips":        return overrides.SpaceCargoShipFields;
                case "RandomEncounters":       return overrides.RandomEncounterFields;
                case "PlanetaryCargoShips":    return overrides.PlanetaryCargoShipFields;
                case "PlanetaryInstallations": return overrides.PlanetaryInstallationFields;
                case "BossEncounters":         return overrides.BossEncounterFields;
                case "Combat":                 return overrides.CombatFields;
                case "Cleanup":                return overrides.CleanupFields;
                default:                       return null;
            }
        }

        // ─── Config parse types ───────────────────────────────────────────────

        private class ConfigDocument
        {
            public ConfigGlobalOverrides GlobalOverrides;
            public List<ConfigFaction>   Factions;
        }

        private class ConfigGlobalOverrides
        {
            public Dictionary<string, string> GeneralFields;
            public Dictionary<string, string> GridsFields;
            public Dictionary<string, string> SpaceCargoShipFields;
            public Dictionary<string, string> RandomEncounterFields;
            public Dictionary<string, string> PlanetaryCargoShipFields;
            public Dictionary<string, string> PlanetaryInstallationFields;
            public Dictionary<string, string> BossEncounterFields;
            public Dictionary<string, string> CombatFields;
            public Dictionary<string, string> CleanupFields;
        }

        private class ConfigFaction
        {
            public string           Tag;
            public bool             FactionEnabled;
            public List<ConfigSgEntry> SpawnGroups;
        }

        private struct ConfigSgEntry
        {
            public string  SubtypeId;
            public bool    Enabled;
            public float?  FrequencyOverride;
            public bool?   WeaponRandomizerOverride;
        }

        // ─── MES API wrapper ──────────────────────────────────────────────────

        /// <summary>
        /// Minimal MES API wrapper. Only the methods MESDC actually uses are
        /// wired up. The full MESApi.cs from the MES repo can be dropped in
        /// as a replacement if more methods are needed later.
        /// </summary>
        private class MesApi
        {
            public bool Ready { get; private set; }

            private const long MES_ID = 1521905890L;

            private Action<string, bool> _toggleSpawnGroupEnabled;
            private Action<bool, string, Func<string, string, string,
                VRageMath.Vector3D, bool>> _registerCustomSpawnCondition;

            public MesApi()
            {
                MyAPIGateway.Utilities.RegisterMessageHandler(
                    MES_ID, OnMessage);
            }

            public void UnregisterListener()
            {
                MyAPIGateway.Utilities.UnregisterMessageHandler(
                    MES_ID, OnMessage);
            }

            public void ToggleSpawnGroupEnabled(string subtypeId, bool enabled)
            {
                _toggleSpawnGroupEnabled?.Invoke(subtypeId, enabled);
            }

            public void RegisterCustomSpawnCondition(bool register,
                string id,
                Func<string, string, string, VRageMath.Vector3D, bool> func)
            {
                _registerCustomSpawnCondition?.Invoke(register, id, func);
            }

            private void OnMessage(object data)
            {
                try
                {
                    var dict = data as Dictionary<string, Delegate>;
                    if (dict == null)
                        return;

                    _toggleSpawnGroupEnabled =
                        (Action<string, bool>)dict["ToggleSpawnGroupEnabled"];

                    _registerCustomSpawnCondition =
                        (Action<bool, string, Func<string, string, string,
                        VRageMath.Vector3D, bool>>)
                        dict["RegisterCustomSpawnCondition"];

                    Ready = true;
                    MyLog.Default.WriteLineAndConsole(
                        "[MESDC] MES API ready.");
                }
                catch (Exception ex)
                {
                    MyLog.Default.WriteLineAndConsole(
                        "[MESDC] MES API init failed: " + ex.Message);
                }
            }
        }

        // ─── Chat Command Handler ─────────────────────────────────────────────

        private void OnChatMessageEntered(string messageText,
            ref bool sendToOthers)
        {
            if (!messageText.StartsWith(CHAT_PREFIX,
                    StringComparison.OrdinalIgnoreCase))
                return;

            sendToOthers = false;

            if (!IsCallerAdmin())
            {
                SendReply("MESDC commands are admin-only.");
                return;
            }

            string command = messageText.Trim();

            if (string.Equals(command, CHAT_PREFIX + " INIT CONFIRM",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (_awaitingInitConfirm)
                {
                    _awaitingInitConfirm = false;
                    HandleInitConfirmed();
                }
                else
                {
                    SendReply("No pending INIT request. "
                            + "Type /MESDC INIT first.");
                }
                return;
            }

            if (string.Equals(command, CHAT_PREFIX + " INIT",
                    StringComparison.OrdinalIgnoreCase))
            {
                HandleInit();
                return;
            }

            SendReply("Unknown command. Available commands: /MESDC INIT");
        }

        private void HandleInit()
        {
            if (!_initDone)
            {
                SendReply("MESDC is still initialising. "
                        + "Try again in a moment.");
                return;
            }

            if (!BaselineFileExists())
            {
                SendReply("ERROR: No baseline file found. "
                        + "Restart the server to regenerate.");
                return;
            }

            _awaitingInitConfirm = true;
            Log("/MESDC INIT invoked — awaiting confirmation.");
            SendReply("WARNING: /MESDC INIT will overwrite MESDC-Config.xml "
                    + "and discard ALL existing overrides. "
                    + "Back up the file first. "
                    + "Type /MESDC INIT CONFIRM to continue.");
        }

        private void HandleInitConfirmed()
        {
            if (!_initDone)
            {
                SendReply("MESDC is still initialising.");
                return;
            }

            Log("/MESDC INIT CONFIRM: regenerating config from baseline.");
            SendReply("Regenerating MESDC-Config.xml from baseline...");

            try
            {
                GenerateConfigFromBaseline();
                SendReply("Done. MESDC-Config.xml regenerated. "
                        + "Restart the server for changes to take effect.");
                Log("/MESDC INIT CONFIRM: complete.");
            }
            catch (Exception ex)
            {
                Log("/MESDC INIT CONFIRM failed: " + ex.ToString());
                SendReply("ERROR: Config regeneration failed. "
                        + "Check server log.");
            }
        }

        // ─── Utilities ────────────────────────────────────────────────────────

        private bool IsCallerAdmin()
        {
            var player = MyAPIGateway.Session.LocalHumanPlayer;
            if (player == null)
                return true;

            return MyAPIGateway.Session.IsUserAdmin(player.SteamUserId);
        }

        private void SendReply(string message)
        {
            MyAPIGateway.Utilities.ShowMessage(MOD_TAG, message);
        }

        private static void Log(string message)
        {
            MyLog.Default.WriteLineAndConsole(
                string.Format("{0} {1}", MOD_TAG, message));
        }
    }
}