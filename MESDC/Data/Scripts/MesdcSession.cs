using System;
using System.IO;
using System.Text;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Utils;

namespace MESDC
{
    [MySessionComponentDescriptor(MyUpdateOrder.NoUpdate)]
    public class MesdcSession : MySessionComponentBase
    {
        // ─── Constants ────────────────────────────────────────────────────────

        private const string MOD_TAG        = "[MESDC]";
        private const string FOLDER_NAME    = "MESDC";
        private const string BASELINE_FILE  = "MESDC-Baseline.xml";
        private const string CONFIG_FILE    = "MESDC-Config.xml";
        private const string CHAT_PREFIX    = "/MESDC";

        // ─── State ────────────────────────────────────────────────────────────

        private bool _isServer   = false;
        private bool _initDone   = false;
        private string _storagePath = null;

        // ─── Lifecycle ────────────────────────────────────────────────────────

        public override void LoadData()
        {
            // LoadData fires on both client and server.
            // MESDC only does work server-side; clients download the mod for
            // definition registration only.
            _isServer = MyAPIGateway.Multiplayer.IsServer ||
                        MyAPIGateway.Utilities.IsDedicated;

            if (!_isServer)
                return;

            Log("LoadData: server detected, beginning startup sequence.");

            try
            {
                EnsureStorageFolder();
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

            Log("UnloadData: MESDC unloaded.");
        }

        // ─── Startup Sequence ────────────────────────────────────────────────

        /// <summary>
        /// Full startup sequence. Called once per server boot from LoadData.
        /// Order is critical — baseline must be written before apply pass runs.
        /// </summary>
        private void RunStartupSequence()
        {
            // Step 1: Write MESDC-Baseline.xml from raw MES definition state.
            // This must happen before the apply pass so the baseline reflects
            // unmodified MES mod values, not MESDC-patched values.
            WriteBaseline();

            // Step 2: If MESDC-Config.xml does not exist, generate it from
            // the baseline we just wrote. First-run path only.
            if (!ConfigFileExists())
            {
                Log("No config file found. Generating from baseline.");
                GenerateConfigFromBaseline();
            }

            // Step 3: Apply MESDC-Config.xml overrides to live MES definitions
            // and write patched values to MES config files on disk.
            ApplyConfig();

            // Step 4: Register chat command handler for /MESDC commands.
            MyAPIGateway.Utilities.MessageEntered += OnChatMessageEntered;

            _initDone = true;
            Log("Startup sequence complete.");
        }

        // ─── Storage Path ────────────────────────────────────────────────────

        private void EnsureStorageFolder()
        {
            // MyAPIGateway.Utilities.GamePaths.UserDataPath gives the world
            // save folder. Storage lives under that.
            string worldStorage = Path.Combine(
                MyAPIGateway.Utilities.GamePaths.UserDataPath,
                "Storage");

            _storagePath = Path.Combine(worldStorage, FOLDER_NAME);

            if (!Directory.Exists(_storagePath))
            {
                Directory.CreateDirectory(_storagePath);
                Log("Created storage folder: " + _storagePath);
            }
            else
            {
                Log("Storage folder exists: " + _storagePath);
            }
        }

        private string BaselinePath
        {
            get { return Path.Combine(_storagePath, BASELINE_FILE); }
        }

        private string ConfigPath
        {
            get { return Path.Combine(_storagePath, CONFIG_FILE); }
        }

        private bool ConfigFileExists()
        {
            return File.Exists(ConfigPath);
        }

        // ─── Startup Steps (stubs for later layers) ──────────────────────────

        /// <summary>
        /// Layer 2: Read raw MES SpawnGroup definitions from MyDefinitionManager
        /// and write MESDC-Baseline.xml.
        /// </summary>
        private void WriteBaseline()
        {
            Log("WriteBaseline: STUB — not yet implemented.");
            // TODO Layer 2: enumerate MyDefinitionManager.GetSpawnGroupDefinitions()
            // grouped by FactionTag, read MES Config-*.xml files for GlobalOverrides,
            // serialise to MESDC-Baseline.xml.
        }

        /// <summary>
        /// Layer 3: Generate a clean MESDC-Config.xml from the current baseline.
        /// Used on first run and by /MESDC INIT.
        /// </summary>
        private void GenerateConfigFromBaseline()
        {
            Log("GenerateConfigFromBaseline: STUB — not yet implemented.");
            // TODO Layer 3: parse MESDC-Baseline.xml, produce MESDC-Config.xml
            // with SPRT fully annotated as GOBY, all lists commented, override
            // fields at safe defaults.
        }

        /// <summary>
        /// Layer 4: Read MESDC-Config.xml and apply overrides to live MES
        /// definitions in memory and MES Config-*.xml files on disk.
        /// </summary>
        private void ApplyConfig()
        {
            Log("ApplyConfig: STUB — not yet implemented.");
            // TODO Layer 4: parse MESDC-Config.xml, for each present element:
            //   - GlobalOverrides.* → write to corresponding MES Config-*.xml
            //   - Faction.FactionEnabled=false → NpcSpawnGroupBlacklist
            //   - SpawnGroup.Enabled=false → NpcSpawnGroupBlacklist
            //   - SpawnGroup.SpawnFrequency → patch Frequency in memory
            //   - SpawnGroup.WeaponRandomizerEnabled → patch UseWeaponRandomizerOnSpawn
            //   - RegionGating → register with spawn-intercept layer (Layer 5)
        }

        // ─── Chat Command Handler ─────────────────────────────────────────────

        private void OnChatMessageEntered(string messageText, ref bool sendToOthers)
        {
            if (!messageText.StartsWith(CHAT_PREFIX,
                    StringComparison.OrdinalIgnoreCase))
                return;

            // Consume the message — don't broadcast MESDC commands to chat.
            sendToOthers = false;

            if (!IsCallerAdmin())
            {
                SendReply("MESDC commands are admin-only.");
                return;
            }

            string command = messageText.Trim();

            if (string.Equals(command, CHAT_PREFIX + " INIT",
                    StringComparison.OrdinalIgnoreCase))
            {
                HandleInit();
            }
            else
            {
                SendReply("Unknown command. Available commands: /MESDC INIT");
            }
        }

        /// <summary>
        /// /MESDC INIT: regenerate MESDC-Config.xml from the current baseline.
        /// Discards all existing config edits. Requires server restart for
        /// changes to take effect. Does NOT regenerate the baseline.
        /// </summary>
        private void HandleInit()
        {
            if (!_initDone)
            {
                SendReply("MESDC is still initialising. Try again in a moment.");
                return;
            }

            Log("/MESDC INIT invoked by admin.");
            SendReply("WARNING: This will overwrite MESDC-Config.xml and " +
                      "discard all existing overrides. " +
                      "Type /MESDC INIT CONFIRM to proceed.");

            // Confirmation is handled as a two-step: the first call sets a
            // pending flag, the second call with CONFIRM executes.
            // TODO Layer 3: implement two-step confirmation and call
            // GenerateConfigFromBaseline() on confirm.
        }

        // ─── Utilities ───────────────────────────────────────────────────────

        private bool IsCallerAdmin()
        {
            // MESDC commands are restricted to the server console only.
            // On a dedicated Keen server, MessageEntered fires for console
            // input with no local human player present. Any message that
            // arrives while a local human player exists is from an in-game
            // client and is rejected.
            return MyAPIGateway.Session.LocalHumanPlayer == null;
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