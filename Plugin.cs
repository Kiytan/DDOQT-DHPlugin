using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Ddo.Enums;

namespace QuestTracker;

/// <summary>
/// Quest Tracker Plugin for Dungeon Helper
/// Tracks completed quests and exports them in DDOQT-compatible format
/// </summary>
[PluginId("f006af62-5b8a-4298-9017-e4e588df4d7d")]
[PluginIcon("QTIcon.png")]
public class Plugin : IPlugin
{
    private IDdoGameDataProvider? _gameDataProvider;
    private string? _folder;
    private QuestTrackerUI? _ui;
    private readonly PluginLog _log = new(typeof(Plugin));
    private PluginSettings _settings = new();

    // Stores current quest data
    private List<QuestCompletion> _completedQuests = new();
    private IReadOnlyCollection<IStoryQuestRecord>? _lastQuestRecords;
    // The character _lastQuestRecords belong to, 0 when they arrived before any character was known
    private ulong _lastQuestRecordsCharacterId;
    // Completions recorded live since this character logged in (keys from CompletionKey). The login
    // snapshot can't show these yet, so they're the only in-record quests a re-process may keep.
    private readonly HashSet<string> _sessionCompletionKeys = new();
    private readonly object _questLock = new();

    // Track current instance quest for completion detection
    private uint? _currentInstanceQuestDid;
    private uint? _currentQuestAreaId;
    private string _currentInstanceDifficulty = "Elite";

    // When the last quest Experience Report arrived. A "Quest Completed" alert only records the
    // completion itself if no report turns up around the same time.
    private DateTime _lastExperienceReportUtc = DateTime.MinValue;
    private static readonly TimeSpan AlertFallbackDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReportMatchWindow = TimeSpan.FromSeconds(15);

    // Character context
    private ulong _currentCharacterId = 0;
    
    // Mapping from known name to actual name (for alias matching)
    private Dictionary<uint, IStoryQuestRecord> _questRecordsByDid = new();

    // Map game quest names to DDOQT quest names (for cases where they differ)
    private static readonly Dictionary<string, string> _questNameAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Disambiguation (quest) suffixes
        { "Vecna Unleashed", "Vecna Unleashed (quest)" },
        { "Devil Assault", "Devil Assault (quest)" },
        { "Redemption", "Redemption (quest)" },
        { "Against the Demon Queen", "Against the Demon Queen (quest)" },
        { "The Dreaming Dark", "The Dreaming Dark (quest)" },
        { "Turn the Page", "Turn the Page (quest)" },
        { "Return to Delera's Tomb", "Return to Delera's Tomb (quest)" },
        
        // Missing "The" prefix
        { "Curse of Strahd", "The Curse of Strahd" },
        { "Jungle of Khyber", "The Jungle of Khyber" },
        { "Sane Asylum", "The Sane Asylum" },
        { "Faithful Departed", "The Faithful Departed" },
        { "Mask of Deception", "The Mask of Deception" },
        
        // Extra "The" prefix
        { "The Chains of Flame", "Chains of Flame" },
        
        // Shortened names
        { "Archer Point", "Archer Point Defense" },
        { "Gladewatch Outpost", "Gladewatch Outpost Defense" },
        
        // Apostrophe differences
        { "Sykros Jewel", "Sykros' Jewel" },
        
        // Capitalization differences
        { "In the Flesh", "In The Flesh" },
        { "Murder By Night", "Murder by Night" },
        { "To Curse The Sky", "To Curse the Sky" },
        { "Trial By Fury", "Trial by Fury" },
        { "A Mad Tea Party", "Mad Tea Party" },
        { "Fresh-Baked Dreams", "Fresh-baked Dreams" },
        { "Durk's Got a Secret", "Durk's Got A Secret" },
        { "Freshen The Air", "Freshen the Air" },
        { "The Lord Of Blades", "The Lord of Blades" },
        { "In The Belly of the Beast", "In the Belly of the Beast" },
        
        // Other name differences
        { "End of the Road", "The End of the Road" },
        { "The Madstone Crater", "Madstone Crater" },
        { "Garl's Tomb", "Old Grey Garl" },
        { "Delera's Tomb", "Free Delera" },
        { "The Legendary Shroud", "The Codex and the Shroud" },
        
        // The Dragon's Hand chain (record name -> weenie / DDOQT name)
        { "The Dragon's Hand: Going Rogue", "Going Rogue" },
        { "The Dragon's Hand: Wish Upon a Card", "Wish Upon a Card" },
        { "The Dragon's Hand: Burning Down the House", "Burning Down the House" },
        { "The Dragon's Hand: Thrall of Duty", "Thrall of Duty" },
        { "The Dragon's Hand: The Final Draw", "The Final Draw" },

        // Same chain; game sometimes uses plural possessive "Dragons'" in weenie / UI strings
        { "The Dragons' Hand: Going Rogue", "Going Rogue" },
        { "The Dragons' Hand: Wish Upon a Card", "Wish Upon a Card" },
        { "The Dragons' Hand: Burning Down the House", "Burning Down the House" },
        { "The Dragons' Hand: Thrall of Duty", "Thrall of Duty" },
        { "The Dragons' Hand: The Final Draw", "The Final Draw" },

        // Chain dungeon name differences
        { "The Cloven-jaw Scourge: Caverns of Shaagh", "The Cloven-jaw Scourge: The Caverns of Shaagh" },
        { "The Kobolds' Den: Clan Gnashtooth", "The Kobold's Den: Clan Gnashtooth" },
        { "The Kobolds' Den: Rescuing Arlos", "The Kobold's Den: Rescuing Arlos" },
        
        // ToEE prefix differences
        { "Temple of Elemental Evil: Fire Node", "ToEE: Fire Node" },
        { "Temple of Elemental Evil: Water Node", "ToEE: Water Node" },
    };

    public static Plugin? Instance { get; private set; }

    public Plugin()
    {
        Instance = this;
    }

    // Dungeon Helper toggles this when the toolbar icon is clicked, and reads it to decide whether to
    // call ImGui_Render. The interface default ignores sets, so the window could never open without it.
    public bool WindowVisible { get; set; }

    // The icon comes from [PluginIcon]; this is only for changing it at runtime
    public byte[]? ToolbarImage => null;

    public void Initialize(IDdoGameDataProvider gameDataProvider, IDdoEventProvider eventProvider, string pluginFolder)
    {
        _gameDataProvider = gameDataProvider;
        _folder = pluginFolder;
        _log.SetFolder(pluginFolder);
        LoadSettings();
        _ui = new QuestTrackerUI();

        _log.Info($"Quest Tracker plugin {GetType().Assembly.GetName().Version} initialized");

        // If the plugin was loaded with a character already logged in, show that character's cache.
        // Otherwise nothing is shown until OnLogin says whose quests to load.
        var liveCharacterId = GetLiveCharacterId();
        if (liveCharacterId != 0)
            SwitchCharacter(liveCharacterId);
        _ui.RefreshQuestCount();

        // Subscribe to events
        try
        {
            eventProvider.OnAllStoryQuestRecords.AddHandler(OnQuestRecordsReceived);
            eventProvider.OnLogin.AddHandler(OnLoginReceived);
            eventProvider.OnLogout.AddHandler(OnLogoutReceived);
            eventProvider.OnMapAreaChange.AddHandler(OnMapAreaChange);
            eventProvider.OnAddAlert.AddHandler(OnAddAlert);
            eventProvider.OnPortalActivate.AddHandler(OnPortalActivate);
            eventProvider.OnQuestExperienceReport.AddHandler(OnQuestExperienceReport);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to subscribe to quest events", ex);
        }
    }

    public void ImGui_Render()
    {
        // The plugin is responsible for honouring WindowVisible
        if (!WindowVisible || _ui == null) return;

        WindowVisible = _ui.Render();
    }

    public void ClientFrameStep()
    {
    }

    private async Task OnMapAreaChange(uint areaId)
    {
        try
        {
            // Wait a moment for SDK to populate instance data
            await Task.Delay(1000);

            var newDid = _gameDataProvider == null ? null : await _gameDataProvider.GetInstanceQuestDidAsync();
            var inTown = _gameDataProvider == null ? null : await _gameDataProvider.InTownAsync();

            _log.Info($"MapAreaChange - areaId: {areaId}, previousAreaId: {_currentQuestAreaId}, " +
                      $"previousDid: {_currentInstanceQuestDid}, newDid: {newDid}, " +
                      $"InTown: {inTown}");
            
            if (newDid.HasValue && newDid.Value != 0)
            {
                
                _currentInstanceQuestDid = newDid;
                _currentQuestAreaId = areaId;
                var recordName = _questRecordsByDid.TryGetValue(newDid.Value, out var record) 
                    ? record.QuestName 
                    : "Wilderness Area";
                var actualName = GetActualQuestName(newDid.Value);
                var questName = actualName ?? recordName;
                
                _log.Info($"Captured quest DID {newDid.Value} from map area change, AreaId: {areaId}, CurrentDifficulty: {_currentInstanceDifficulty}, " +
                          $"RecordName: '{recordName}', WeenieName: '{actualName}', UsingName: '{questName}'");
                _ui?.UpdateInstanceDisplay(questName);
            }
            else
            {
                _log.Info("Clearing instance display - public area");
                _currentQuestAreaId = null;
                _ui?.UpdateInstanceDisplay(null);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Error getting instance quest DID", ex);
        }
    }
    
    
    
    private async Task OnPortalActivate(IPortalInfo portalInfo)
    {
        try
        {
            if (portalInfo != null)
            {
                var currentQuestDid = _gameDataProvider == null ? null : await _gameDataProvider.GetCurrentQuestDidAsync();

                // Log all available portal info including QuestBestowed for research
                _log.Info($"PortalActivate - ActiveInstanceDid: {portalInfo.ActiveInstanceDid}, " +
                          $"QuestGenericDifficulty: {portalInfo.QuestGenericDifficulty}, " +
                          $"AvailableQuests: [{string.Join(",", portalInfo.AvailableQuests ?? new List<uint>())}], " +
                          $"GetCurrentQuestDid: {currentQuestDid}");
                
                // Reset tracked DID so new portal data always takes effect
                var previousDid = _currentInstanceQuestDid;
                _currentInstanceQuestDid = null;
                
                // Try to capture quest DID from portal info (cascading priority)
                var didSource = "none";
                var questDid = portalInfo.ActiveInstanceDid;
                if (questDid.HasValue && questDid.Value != 0)
                {
                    _currentInstanceQuestDid = questDid.Value;
                    didSource = "ActiveInstanceDid";
                }
                
                // Also try AvailableQuests property
                if ((!_currentInstanceQuestDid.HasValue || _currentInstanceQuestDid.Value == 0))
                {
                    var availableQuests = portalInfo.AvailableQuests;
                    if (availableQuests != null && availableQuests.Count > 0)
                    {
                        _currentInstanceQuestDid = availableQuests.First();
                        didSource = "AvailableQuests";
                    }
                }
                
                // Fallback to GetCurrentQuestDid
                if (!_currentInstanceQuestDid.HasValue || _currentInstanceQuestDid.Value == 0)
                {
                    if (currentQuestDid.HasValue && currentQuestDid.Value != 0)
                    {
                        _currentInstanceQuestDid = currentQuestDid.Value;
                        didSource = "GetCurrentQuestDid";
                    }
                }
                
                _log.Info($"PortalActivate - Selected DID: {_currentInstanceQuestDid} from source: {didSource} (was: {previousDid})");
                
                // Store the difficulty for when quest completes
                var difficulty = portalInfo.QuestGenericDifficulty;
                if (difficulty.HasValue && difficulty.Value >= 1 && difficulty.Value <= 14)
                {
                    var previousDifficulty = _currentInstanceDifficulty;
                    
                    _currentInstanceDifficulty = difficulty.Value switch
                    {
                        1 => "Normal",
                        2 => "Hard",
                        3 => "Elite",
                        >= 4 => "Reaper",
                        _ => "Elite"
                    };
                    _log.Info($"Difficulty captured from portal: {difficulty.Value} -> {_currentInstanceDifficulty} (was: {previousDifficulty})");
                }
                else if (difficulty.HasValue)
                {
                    _log.Info($"Ignoring out-of-range difficulty value from portal: {difficulty.Value}, keeping current: {_currentInstanceDifficulty}");
                }
                else
                {
                    _log.Info($"No difficulty in portal info, keeping current: {_currentInstanceDifficulty}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Error processing PortalActivate", ex);
        }
    }

    /// <summary>
    /// The Experience Report shown when an instanced quest is completed. This is the main completion
    /// signal: unlike the alert, it says exactly which difficulty the quest was run on, Reaper included.
    /// </summary>
    private async Task OnQuestExperienceReport(IQuestExperienceReport report)
    {
        try
        {
            if (report == null) return;

            var summary = $"Experience report - QuestName: '{report.QuestName}', QuestLevel: {report.QuestLevel}, " +
                          $"Difficulty: {report.Difficulty}, ExperienceType: {report.ExperienceType}, " +
                          $"ReaperSkullLevel: {report.ReaperSkullLevel}, Status: '{report.Status}', IsCompleted: {report.IsCompleted}";

            // The report also arrives every time XP is earned while the quest is underway. Only a
            // completed one counts, so those mid-quest reports can't stop the alert fallback.
            if (!report.IsCompleted)
            {
                _log.Debug(summary);
                return;
            }

            _lastExperienceReportUtc = DateTime.UtcNow;
            _log.Info(summary);

            var difficulty = DetermineDifficulty(report);
            var ranHigherTier = RanHigherTier(report);
            _log.Info($"Experience report difficulty: {difficulty}, higher tier: {ranHigherTier}");

            await HandleQuestCompletion(difficulty, ranHigherTier, report.QuestName);
        }
        catch (Exception ex)
        {
            _log.Error("Error processing quest experience report", ex);
        }
    }

    /// <summary>
    /// Maps the report's DifficultyType onto the difficulties DDOQT tracks
    /// </summary>
    private static string DetermineDifficulty(IQuestExperienceReport report)
    {
        if (report.ReaperSkullLevel > 0)
            return "Reaper";

        return (DifficultyType?)report.Difficulty switch
        {
            DifficultyType.Reaper or DifficultyType.Epic_Reaper => "Reaper",
            DifficultyType.Elite or DifficultyType.EliteFinished or DifficultyType.Epic_Elite => "Elite",
            DifficultyType.Hard or DifficultyType.Epic_Hard => "Hard",
            _ => "Normal" // Normal, Casual, Solo and their epic versions
        };
    }

    /// <summary>
    /// Whether the run was the epic or legendary version of the quest rather than the heroic one.
    /// Null when the report doesn't say.
    /// </summary>
    private static bool? RanHigherTier(IQuestExperienceReport report)
    {
        if (report.QuestLevel.HasValue)
            return report.QuestLevel.Value >= 20;

        if (report.ExperienceType is ExperienceType.Epic or ExperienceType.Legendary)
            return true;

        if (report.ExperienceType is ExperienceType.Heroic)
            return false;

        return (DifficultyType?)report.Difficulty switch
        {
            DifficultyType.Epic or DifficultyType.Epic_Casual or DifficultyType.Epic_Hard
                or DifficultyType.Epic_Elite or DifficultyType.Epic_Reaper => true,
            null => null,
            _ => false
        };
    }

    private async Task OnAddAlert(IAddAlert alert)
    {
        try
        {
            if (alert != null)
            {
                var desc = alert.Description?.Trim() ?? "";
                var title = alert.Title?.Trim() ?? "";

                // Log all alerts for research - some may contain difficulty info
                _log.Debug($"Alert received - Title: '{title}', Description: '{desc}', Did: {alert.Did}");

                // Check for quest/adventure completion
                if (desc.StartsWith("Quest Completed") || desc.StartsWith("Adventure Completed"))
                {
                    _log.Info($"Quest completion alert - Title: '{title}', Description: '{desc}'");
                    var alertUtc = DateTime.UtcNow;

                    // Try to extract difficulty from alert text (e.g., "Quest Completed on Elite")
                    var alertDifficulty = ExtractDifficultyFromAlert(desc) ?? ExtractDifficultyFromAlert(title);
                    if (!string.IsNullOrEmpty(alertDifficulty))
                    {
                        _log.Info($"Difficulty extracted from alert text: {alertDifficulty}");
                        _currentInstanceDifficulty = alertDifficulty;
                    }

                    // Give the Experience Report a chance to arrive, as it knows the real difficulty
                    await Task.Delay(AlertFallbackDelay);
                    if (alertUtc - _lastExperienceReportUtc < ReportMatchWindow)
                    {
                        _log.Info("Completion already recorded from the Experience Report, ignoring alert");
                        return;
                    }

                    _log.Info("No Experience Report for this completion, falling back to the alert");
                    await HandleQuestCompletion(_currentInstanceDifficulty, null);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Error processing Alert", ex);
        }
    }
    
    /// <summary>
    /// Attempts to extract difficulty from alert text.
    /// DDO alerts often contain text like "Quest Completed on Elite" or "on Reaper 3".
    /// </summary>
    private static string? ExtractDifficultyFromAlert(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        
        // Check for Reaper difficulties (R1-R10)
        // Must match "on Reaper" or "Reaper difficulty" to avoid false positives on quests with "Reaper" in the name
        if (text.Contains("on Reaper", StringComparison.OrdinalIgnoreCase) || 
            text.Contains("Reaper difficulty", StringComparison.OrdinalIgnoreCase))
            return "Reaper";
        
        // Check for standard difficulties
        if (text.Contains("on Elite", StringComparison.OrdinalIgnoreCase) || 
            text.Contains("Elite difficulty", StringComparison.OrdinalIgnoreCase))
            return "Elite";
        
        if (text.Contains("on Hard", StringComparison.OrdinalIgnoreCase) || 
            text.Contains("Hard difficulty", StringComparison.OrdinalIgnoreCase))
            return "Hard";
        
        if (text.Contains("on Normal", StringComparison.OrdinalIgnoreCase) || 
            text.Contains("Normal difficulty", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("on Casual", StringComparison.OrdinalIgnoreCase))
            return "Normal";
        
        return null;
    }
    
    /// <param name="difficulty">The difficulty the quest was completed on</param>
    /// <param name="ranHigherTier">True for an epic or legendary run, false for heroic, null if unknown</param>
    /// <param name="reportQuestName">The quest the Experience Report names, null when there was no report</param>
    private async Task HandleQuestCompletion(string difficulty, bool? ranHigherTier, string? reportQuestName = null)
    {
        try
        {
            // Try multiple sources for quest DID
            var questDid = _currentInstanceQuestDid;
            var instanceDid = _gameDataProvider == null ? null : await _gameDataProvider.GetInstanceQuestDidAsync();
            var currentDid = _gameDataProvider == null ? null : await _gameDataProvider.GetCurrentQuestDidAsync();

            _log.Info($"Quest completion - tracked DID: {questDid}, instance DID: {instanceDid}, current DID: {currentDid}, difficulty: {difficulty}");
            
            var didSource = "tracked";
            if (!questDid.HasValue || questDid.Value == 0)
            {
                questDid = instanceDid;
                didSource = "instanceDid";
            }
            
            if (!questDid.HasValue || questDid.Value == 0)
            {
                questDid = currentDid;
                didSource = "currentDid";
            }
            
            // The tracked DID is the last quest entered, which is stale when a quest completes after
            // leaving its dungeon (handed in to an NPC in town, say). The report names the quest that
            // actually completed, so make sure the DID agrees with it.
            if (!string.IsNullOrWhiteSpace(reportQuestName))
            {
                var (matchedDid, matchedSource) = FindDidForReportedQuest(reportQuestName,
                    (questDid, didSource), (instanceDid, "instanceDid"), (currentDid, "currentDid"));

                if (matchedDid.HasValue && matchedDid != questDid)
                {
                    _log.Warn($"Experience report is for '{reportQuestName}' but the {didSource} DID {questDid} is " +
                              $"'{DescribeQuest(questDid)}', crediting DID {matchedDid} from {matchedSource} instead");
                    questDid = matchedDid;
                    didSource = matchedSource;
                }
                else if (!matchedDid.HasValue)
                {
                    _log.Warn($"Couldn't find the Experience Report's quest '{reportQuestName}', crediting " +
                              $"'{DescribeQuest(questDid)}' (DID {questDid}) as before");
                }
            }

            _log.Info($"Quest completion - final DID: {questDid} from source: {didSource}");

            if (questDid.HasValue && questDid.Value != 0 && _questRecordsByDid.TryGetValue(questDid.Value, out var questRecord))
            {
                // Prefer weenie name (individual dungeon) over record name (chain name)
                var actualName = GetActualQuestName(questDid.Value);
                var usingName = actualName ?? questRecord.QuestName ?? "";
                
                _log.Info($"Quest completed - RecordName: '{questRecord.QuestName}', WeenieName: '{actualName}', UsingName: '{usingName}', " +
                          $"difficulty: {difficulty}, DID: {questDid.Value}");

                // Pass the resolved name but keep the record's favor/level metadata
                AddCompletedQuestFromRecord(questRecord, difficulty, usingName, ranHigherTier);
                _ui?.RefreshQuestCount();

                // Reset difficulty after completion for next quest
                _currentInstanceDifficulty = "Elite";
            }
            else if (questDid.HasValue && questDid.Value != 0)
            {
                // Fallback: DID not in quest records - try weenie name lookup (for chain quest individual dungeons)
                var questName = GetActualQuestName(questDid.Value);

                if (!string.IsNullOrEmpty(questName))
                {
                    _log.Info($"Quest completed (via weenie name): {questName} at {difficulty}");
                    AddCompletedQuestByName(questDid.Value, questName, difficulty);
                    _ui?.RefreshQuestCount();
                }
                else
                {
                    _log.Warn($"Quest completion detected but could not resolve name for DID {questDid.Value}");
                }
                
                // Reset difficulty after completion for next quest
                _currentInstanceDifficulty = "Elite";
            }
        }
        catch (Exception ex)
        {
            _log.Error("Error handling quest completion", ex);
        }
    }
    
    /// <summary>
    /// Finds the DID of the quest an Experience Report names. The candidate DIDs are tried first, in
    /// order, then every quest record. Null if nothing has that name.
    /// </summary>
    private (uint? Did, string Source) FindDidForReportedQuest(string reportQuestName, params (uint? Did, string Source)[] candidates)
    {
        var wanted = QuestNameKey(reportQuestName);

        foreach (var (did, source) in candidates)
        {
            if (did is > 0 && QuestNameKeys(did.Value).Contains(wanted))
                return (did, source);
        }

        // Not one of the DIDs the game pointed at, so look through the records. Quests first, as a
        // wilderness area or explorer record could share a name. The record names are checked before
        // the slower weenie lookup.
        var records = _questRecordsByDid.Values.OrderBy(r => r.QuestType == 1 ? 0 : 1).ToList();
        var byRecordName = records.FirstOrDefault(r =>
            QuestNameKey(r.QuestName) == wanted || QuestNameKey(r.AdventureName) == wanted);
        if (byRecordName != null)
            return (byRecordName.QuestDid, "report name");

        var byWeenieName = records.FirstOrDefault(r => QuestNameKey(GetActualQuestName(r.QuestDid)) == wanted);
        if (byWeenieName != null)
            return (byWeenieName.QuestDid, "report name");

        return (null, "none");
    }

    /// <summary>
    /// Every name a quest DID goes by (weenie, record and adventure names), as comparison keys
    /// </summary>
    private HashSet<string> QuestNameKeys(uint questDid)
    {
        var keys = new HashSet<string> { QuestNameKey(GetActualQuestName(questDid)) };
        if (_questRecordsByDid.TryGetValue(questDid, out var record))
        {
            keys.Add(QuestNameKey(record.QuestName));
            keys.Add(QuestNameKey(record.AdventureName));
        }
        keys.Remove("");
        return keys;
    }

    /// <summary>
    /// A quest name reduced for comparison: apostrophes and spacing normalised, aliases applied and
    /// case ignored. Empty for a missing name.
    /// </summary>
    private static string QuestNameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var normalised = Regex.Replace(NormalizeApostrophes(name.Trim()), @"\s+", " ");
        if (_questNameAliases.TryGetValue(normalised, out var alias))
            normalised = alias;
        return normalised.ToLowerInvariant();
    }

    private string DescribeQuest(uint? questDid)
    {
        if (questDid is not > 0)
            return "no quest";

        return GetActualQuestName(questDid.Value)
               ?? (_questRecordsByDid.TryGetValue(questDid.Value, out var record) ? record.QuestName : null)
               ?? "unknown";
    }

    private void AddCompletedQuestFromRecord(IStoryQuestRecord quest, string difficulty, string? overriddenName = null, bool? ranHigherTier = null)
    {
        lock (_questLock)
        {
            var questName = overriddenName ?? quest.QuestName ?? "";
            var baseLevel = (int)quest.QuestLevel;
            var favor = quest.CurrentFavor ?? 0;

            // Check if this quest has heroic/epic/legendary variants
            var variantLevels = QuestVariantLookup.GetVariantLevels(questName);

            if (variantLevels != null && variantLevels.Length > 1)
            {
                // Quest has variants - determine which tier was completed using the encoded difficulty (matching login sync logic)
                var rawDifficulty = quest.HighestDifficultyCompleted;

                foreach (var level in variantLevels)
                {
                    bool shouldInclude = false;

                    if (ranHigherTier.HasValue)
                    {
                        // The Experience Report says exactly which tier was run
                        shouldInclude = ranHigherTier.Value == (level > 19);
                    }
                    else if (level <= 19) // Heroic
                    {
                        shouldInclude = true;
                    }
                    else // Epic (20-29) or Legendary (30+)
                    {
                        shouldInclude = rawDifficulty >= 5;
                    }
                    
                    if (shouldInclude)
                    {
                        var (questId, matched) = ConvertToQuestId(questName, level);
                        
                        // Check if already exists for this level
                        var existingEntry = _completedQuests.FirstOrDefault(q => q.QuestDid == quest.QuestDid && q.QuestLevel == level);
                        
                        if (existingEntry != null)
                        {
                            // If it exists, only update if the new difficulty is higher
                            if (GetDifficultyRank(difficulty) > GetDifficultyRank(existingEntry.Difficulty))
                            {
                                existingEntry.Difficulty = difficulty;
                                existingEntry.CurrentFavor = favor;
                                _log.Info($"Upgraded existing quest variant: {questName} (Level {level}) to {difficulty}");
                                SaveQuestDataToFile();
                            }
                        }
                        else
                        {
                            // Add new entry
                            _completedQuests.Add(new QuestCompletion
                            {
                                QuestDid = quest.QuestDid,
                                QuestName = questName,
                                QuestId = questId,
                                Difficulty = difficulty,
                                CurrentFavor = favor,
                                QuestLevel = level,
                                MatchedInDdoqt = matched
                            });
                            _log.Info($"Added newly completed quest variant: {questName} (Level {level}, {difficulty})");
                            SaveQuestDataToFile();
                        }
                        _sessionCompletionKeys.Add(CompletionKey(quest.QuestDid, level));
                    }
                }
            }
            else
            {
                var (questId, matched) = ConvertToQuestId(questName, baseLevel);
                
                // Check if already exists for this level
                var existingEntry = _completedQuests.FirstOrDefault(q => q.QuestDid == quest.QuestDid && q.QuestLevel == baseLevel);
                
                if (existingEntry != null)
                {
                    // If it exists, only update if the new difficulty is higher
                    if (GetDifficultyRank(difficulty) > GetDifficultyRank(existingEntry.Difficulty))
                    {
                        existingEntry.Difficulty = difficulty;
                        existingEntry.CurrentFavor = favor;
                        _log.Info($"Upgraded existing quest: {questName} (Level {baseLevel}) to {difficulty}");
                        SaveQuestDataToFile();
                    }
                }
                else
                {
                    // Add new entry
                    _completedQuests.Add(new QuestCompletion
                    {
                        QuestDid = quest.QuestDid,
                        QuestName = questName,
                        QuestId = questId,
                        Difficulty = difficulty,
                        CurrentFavor = favor,
                        QuestLevel = baseLevel,
                        MatchedInDdoqt = matched
                    });
                    _log.Info($"Added newly completed quest: {questName} (Level {baseLevel}, {difficulty})");
                    SaveQuestDataToFile();
                }
                _sessionCompletionKeys.Add(CompletionKey(quest.QuestDid, baseLevel));
            }
        }
    }
    
    private void AddCompletedQuestByName(uint questDid, string questName, string difficulty)
    {
        lock (_questLock)
        {

            // Resolve aliases before variant lookup (chain titles like "The Dragons' Hand: …" are not variant keys)
            var resolvedName = NormalizeApostrophes(questName.Trim());
            if (_questNameAliases.TryGetValue(resolvedName, out var aliasTarget))
                resolvedName = aliasTarget;

            // Try to find base level from variant lookup, default to 0
            var variantLevels = QuestVariantLookup.GetVariantLevels(resolvedName);
            var baseLevel = variantLevels?.FirstOrDefault() ?? 0;

            var (questId, matched) = ConvertToQuestId(resolvedName, baseLevel);
            
            // Check if already exists for this level
            var existingEntry = _completedQuests.FirstOrDefault(q => q.QuestDid == questDid && q.QuestLevel == baseLevel);
            
            if (existingEntry != null)
            {
                // If it exists, only update if the new difficulty is higher
                if (GetDifficultyRank(difficulty) > GetDifficultyRank(existingEntry.Difficulty))
                {
                    existingEntry.Difficulty = difficulty;
                    _log.Info($"Upgraded existing quest (by name): {resolvedName} (Level {baseLevel}) to {difficulty}");
                    SaveQuestDataToFile();
                }
            }
            else
            {
                // Add new entry
                _completedQuests.Add(new QuestCompletion
                {
                    QuestDid = questDid,
                    QuestName = resolvedName,
                    QuestId = questId,
                    Difficulty = difficulty,
                    CurrentFavor = 0, // Unknown for chain quest dungeons
                    QuestLevel = baseLevel,
                    MatchedInDdoqt = matched
                });
                _log.Info($"Added newly completed quest (by name): {resolvedName} (Level {baseLevel}, {difficulty})");
                SaveQuestDataToFile();
            }
            _sessionCompletionKeys.Add(CompletionKey(questDid, baseLevel));
        }
    }
    
    private Task OnLoginReceived(ulong characterId)
    {
        _log.Info($"Character login - character ID {characterId}");

        try
        {
            // A no-op when the quest records for this character already arrived and switched to it
            SwitchCharacter(characterId);

            // On a first login the records can arrive before OnLogin. They're this character's if
            // they were tagged with it, or arrived while no character was logged in. Records tagged
            // with another character are the previous character's, left over from before a swap.
            IReadOnlyCollection<IStoryQuestRecord>? records;
            lock (_questLock)
            {
                records = _lastQuestRecords;
                if (records != null && _lastQuestRecordsCharacterId != characterId && _lastQuestRecordsCharacterId != 0)
                {
                    _log.Info($"Ignoring quest records held for character {_lastQuestRecordsCharacterId}, waiting for this character's");
                    _lastQuestRecords = null;
                    _lastQuestRecordsCharacterId = 0;
                    records = null;
                }
            }

            if (records != null)
            {
                _log.Info("Processing quest records received before login");
                ApplyQuestRecords(records, characterId);
            }
        }
        catch (Exception ex)
        {
            _log.Error("Error handling character login", ex);
        }

        _ui?.RefreshQuestCount();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Back at character selection. Drop everything about the character so the next login starts clean.
    /// </summary>
    private Task OnLogoutReceived()
    {
        _log.Info($"Character logout - character ID {_currentCharacterId}");

        lock (_questLock)
        {
            _currentCharacterId = 0;
            _completedQuests = new List<QuestCompletion>();
            _sessionCompletionKeys.Clear();
            _questRecordsByDid = new Dictionary<uint, IStoryQuestRecord>();
            _lastQuestRecords = null;
            _lastQuestRecordsCharacterId = 0;
        }

        ResetInstanceState();
        _ui?.RefreshQuestCount();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The character logged in now according to the game, 0 at character selection or if unknown
    /// </summary>
    private ulong GetLiveCharacterId()
    {
        try
        {
            return _gameDataProvider?.GetCurrentCharacterId() ?? 0;
        }
        catch (Exception ex)
        {
            _log.Error("Failed to read the current character ID", ex);
            return 0;
        }
    }

    /// <summary>
    /// Makes characterId the current character: clears the previous character's quests and loads
    /// this one's cache. Does nothing if it's already the current character.
    /// </summary>
    private void SwitchCharacter(ulong characterId)
    {
        if (characterId == 0) return;

        lock (_questLock)
        {
            if (characterId == _currentCharacterId) return;

            _log.Info($"Switching character from {_currentCharacterId} to {characterId}");
            _currentCharacterId = characterId;
            _completedQuests = new List<QuestCompletion>();
            _sessionCompletionKeys.Clear();
            _questRecordsByDid = new Dictionary<uint, IStoryQuestRecord>();
            LoadQuestDataFromFile();
        }

        ResetInstanceState();
    }

    private void ResetInstanceState()
    {
        _currentInstanceQuestDid = null;
        _currentQuestAreaId = null;
        _currentInstanceDifficulty = "Elite";
        _ui?.UpdateInstanceDisplay(null);
    }

    /// <summary>
    /// Rebuilds the current character's completed quests from a set of quest records
    /// </summary>
    private void ApplyQuestRecords(IReadOnlyCollection<IStoryQuestRecord> questRecords, ulong characterId)
    {
        lock (_questLock)
        {
            _lastQuestRecords = questRecords;
            _lastQuestRecordsCharacterId = characterId;
            _questRecordsByDid = questRecords.ToDictionary(q => q.QuestDid, q => q);
        }

        GenerateQuestNameMismatchReport(questRecords);
        ProcessQuestRecords(questRecords);
        _ui?.RefreshQuestCount();
    }

    private Task OnQuestRecordsReceived(IReadOnlyCollection<IStoryQuestRecord> questRecords)
    {
        try
        {

            // How often each raw HighestDifficultyCompleted value appears, to check DetermineDifficulty's
            // mapping against real data (the SDK's DifficultyType has Reaper as 11 and Epic_Reaper as 12)
            var rawDifficultyCounts = questRecords
                .Where(q => q.QuestType == 1 && IsQuestCompleted(q))
                .GroupBy(q => q.HighestDifficultyCompleted)
                .OrderBy(g => g.Key)
                .Select(g => $"{g.Key}: {g.Count()}");
            _log.Info($"Quest records received: {questRecords.Count}, completed quests by raw difficulty - {string.Join(", ", rawDifficultyCounts)}");

            var knownRawDifficulties = new HashSet<uint> { 1, 2, 3, 4, 5, 6, 9, 10, 20 };
            var unrecognised = questRecords
                .Where(q => q.QuestType == 1 && IsQuestCompleted(q) && !knownRawDifficulties.Contains(q.HighestDifficultyCompleted ?? 0))
                .Take(30)
                .Select(q => $"'{q.QuestName}' = {q.HighestDifficultyCompleted}");
            var unrecognisedText = string.Join(", ", unrecognised);
            if (unrecognisedText.Length > 0)
                _log.Info($"Quests with a raw difficulty DetermineDifficulty doesn't map: {unrecognisedText}");

            // Work out whose records these are. The game's own answer comes first. Failing that, the
            // character from OnLogin, which OnLogout resets, so after a logout this stays 0.
            var owner = GetLiveCharacterId();
            if (owner == 0)
                owner = _currentCharacterId;

            if (owner == 0)
            {
                // Before the first OnLogin. Hold the records until OnLogin says whose they are.
                lock (_questLock)
                {
                    _lastQuestRecords = questRecords;
                    _lastQuestRecordsCharacterId = 0;
                }
                _log.Info("Quest records arrived before login, holding them until the character is known");
                return Task.CompletedTask;
            }

            // On a character swap the new records can beat OnLogin, so switch here too
            SwitchCharacter(owner);
            ApplyQuestRecords(questRecords, owner);
        }
        catch (Exception ex)
        {
            _log.Error("Error processing quest records", ex);
        }
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Generates a report of game quest names that don't match DDOQT lookup.
    /// Writes to quest_name_mismatches.txt in the plugin folder.
    /// </summary>
    private void GenerateQuestNameMismatchReport(IReadOnlyCollection<IStoryQuestRecord> questRecords)
    {
        try
        {
            var mismatches = new List<string>();
            var matched = 0;
            var total = 0;
            var skippedSeasonal = 0;
            
            // Only check Type 1 quests (actual quests, not slayers/explorers/rares)
            var quests = questRecords.Where(q => q.QuestType == 1).ToList();
            
            foreach (var quest in quests)
            {
                // Use weenie name lookup to get actual dungeon name (for chain quests)
                var gameName = GetActualQuestName(quest.QuestDid) ?? quest.QuestName ?? "";
                var originalName = quest.QuestName ?? "";
                if (string.IsNullOrEmpty(gameName)) continue;
                
                // Skip seasonal/internal quests (Night Revels, Festivult/Snowpeaks, Anniversary, internal test quests)
                if (gameName.Contains("Night Revels", StringComparison.OrdinalIgnoreCase) ||
                    gameName.Contains("Snowpeaks", StringComparison.OrdinalIgnoreCase) ||
                    gameName.Contains("Too Cold To Continue", StringComparison.OrdinalIgnoreCase) ||
                    gameName.Contains("Anniversary Party", StringComparison.OrdinalIgnoreCase) ||
                    gameName.StartsWith("DNT", StringComparison.OrdinalIgnoreCase))
                {
                    skippedSeasonal++;
                    continue;
                }
                
                // Skip Challenges (no favor, not tracked in DDOQT)
                if (IsChallenge(gameName))
                {
                    skippedSeasonal++;
                    continue;
                }
                
                // Skip removed quests
                if (gameName.Contains("Memoirs of an Illusory Larcener", StringComparison.OrdinalIgnoreCase))
                {
                    skippedSeasonal++;
                    continue;
                }
                
                total++;
                var normalizedName = NormalizeApostrophes(gameName);
                var level = (int)quest.QuestLevel;
                
                // Check if alias exists
                var lookupName = _questNameAliases.TryGetValue(normalizedName, out var alias) ? alias : normalizedName;
                
                // Try to find in DDOQT lookup
                var questId = QuestIdLookup.GetQuestId(lookupName, level);
                
                if (questId == null)
                {
                    var nameInfo = gameName != originalName ? $"\"{gameName}\" (was \"{originalName}\")" : $"\"{gameName}\"";
                    mismatches.Add($"Game: {nameInfo} (Level {level}) -> Not found in DDOQT");
                }
                else
                {
                    matched++;
                }
            }
            
            // Write report
            var reportPath = Path.Combine(_folder ?? "", "quest_name_mismatches.txt");
            var lines = new List<string>
            {
                $"Quest Name Mismatch Report - Generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"Total quests: {total}, Matched: {matched}, Mismatched: {mismatches.Count}, Skipped seasonal: {skippedSeasonal}",
                "",
                "=== MISMATCHED QUESTS ===",
                "(These need aliases added to _questNameAliases, or added to DDOQT)",
                ""
            };
            lines.AddRange(mismatches.OrderBy(m => m));
            
            File.WriteAllLines(reportPath, lines);
            _log.Info($"Quest name mismatch report: {matched}/{total} matched, {mismatches.Count} mismatches, {skippedSeasonal} seasonal skipped");
        }
        catch (Exception ex)
        {
            _log.Error("Failed to generate quest name mismatch report", ex);
        }
    }

    /// <summary>
    /// Re-processes the last received quest records. Call this to refresh the quest list.
    /// </summary>
    public void RefreshQuestData()
    {
        if (_lastQuestRecords != null && _currentCharacterId != 0 && _lastQuestRecordsCharacterId == _currentCharacterId)
        {
            _log.Info("Manually refreshing quest data");
            ProcessQuestRecords(_lastQuestRecords);
            _ui?.RefreshQuestCount();
        }
        else
        {
            _log.Warn("No quest records available to refresh");
        }
    }

    private bool IsQuestCompleted(IStoryQuestRecord quest)
    {
        // Quest is completed if HighestDifficultyCompleted has a value > 0. Solo is 0 though, the same as
        // never run, so solo-only quests (The Miller's Debt, Arachnophobia) only show up through their favour
        return quest.HighestDifficultyCompleted > 0 || quest.CurrentFavor > 0;
    }

    /// <summary>
    /// Gets the actual quest/dungeon name from weenie properties.
    /// For chain quests, this returns the individual dungeon name (e.g., "Halls of Shan-To-Kor")
    /// instead of the chain name (e.g., "The Seal of Shan-To-Kor").
    /// </summary>
    private string? GetActualQuestName(uint questDid)
    {
        try
        {
            var name = _gameDataProvider?.GetWeenieProperties(questDid)?.Name;
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        catch { }
        return null;
    }

    // Known challenge names (Eveningstar Challenges, Cannith Challenges, etc.) - static HashSet for O(1) lookup
    private static readonly HashSet<string> _challengeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Azure Skies", "Defenseless", "Fight to the Finish", "The Great Tree",
        "The Ring of Fire", "The Sunset Ritual", "Buying Time", "Colossal Crystals",
        "Labor Shortage", "Moving Targets", "Picture Portals", "Short Cuts",
        "The Disruptor", "The Dragon's Hoard", "Time is Money", "Behind the Door",
        "Circles of Power", "Kobold Chaos", "Lava Caves: Colossal Crystals",
        "Lava Caves: Time is Money", "Kobold Island: Short Cuts",
        "Extraplanar Palace: Labor Shortage", "Extraplanar Palace: Buying Time",
        "Kobold Island: The Disruptor", "Rushmore's Mansion: Behind the Door",
        "Rushmore's Mansion: The Dragon's Hoard", "Rushmore's Mansion: Picture Portals",
        "Dr. Rushmore's Mansion: Moving Targets", "Hayweird Foundry"
    };

    /// <summary>
    /// Checks if a quest name is a Challenge (no favor reward, not tracked in DDOQT).
    /// </summary>
    private static bool IsChallenge(string questName)
    {
        // Challenge epic versions have "- EPIC" suffix
        if (questName.EndsWith("- EPIC", StringComparison.OrdinalIgnoreCase))
            return true;
        
        return _challengeNames.Contains(questName);
    }

    private string DetermineDifficulty(IStoryQuestRecord quest)
    {
        // HighestDifficultyCompleted encoding from the VoK SDK:
        // Heroic tier: 0 = Solo (counted as Normal), 1 = Normal, 2 = Hard, 3 = Elite, 4 = Reaper
        // Higher tier (Epic or Legendary): 5 = Normal, 6 or 9 = Hard, 10 = Elite, 20 = Reaper
        return quest.HighestDifficultyCompleted switch
        {
            1 or 5 => "Normal",
            2 or 6 or 9 => "Hard",
            3 or 10 => "Elite",
            4 or 20 => "Reaper",
            _ => "Normal"
        };
    }

    /// <summary>
    /// Normalize apostrophes to standard straight apostrophe
    /// </summary>
    private static string NormalizeApostrophes(string text)
    {
        // Handle various apostrophe/quote characters: ' ' ` ʼ ʻ ′
        return Regex.Replace(text, @"[\u0027\u0060\u00B4\u2018\u2019\u201B\u02BC\u02BB\u2032]", "'");
    }

    /// <summary>
    /// Convert quest name to DDOQT-compatible ID using lookup table
    /// </summary>
    private (string QuestId, bool Matched) ConvertToQuestId(string questName, int questLevel)
    {
        if (string.IsNullOrEmpty(questName))
            return ("", false);

        // Normalize apostrophes (game may use curly quotes)
        questName = NormalizeApostrophes(questName);

        // Apply name alias if one exists (game names differ from DDOQT names)
        if (_questNameAliases.TryGetValue(questName, out var aliasedName))
        {
            questName = aliasedName;
        }

        // First try the lookup table (generated from DDOQT quests.json)
        var lookupId = QuestIdLookup.GetQuestId(questName, questLevel);
        if (lookupId != null)
        {
            return (lookupId, true);
        }

        // Fallback: generate ID using standard conversion for unknown quests
        // This handles any quests not in the lookup table
        _log.Warn($"Quest not found in lookup: '{questName}' level {questLevel}");
        
        var id = questName.ToLowerInvariant();

        // Remove apostrophes and special quotes
        id = Regex.Replace(id, @"[''`']", "");
        // Remove parentheses and their content, but keep the word before underscore
        id = Regex.Replace(id, @"\s*\([^)]*\)", "");
        // Remove other special chars except dash and spaces
        id = Regex.Replace(id, @"[^\w\s-]", "");
        // Replace spaces and dashes with underscores
        id = Regex.Replace(id, @"[\s-]+", "_");
        // Collapse multiple underscores
        id = Regex.Replace(id, @"_+", "_");
        // Remove leading/trailing underscores
        id = id.Trim('_');

        return (id, false);
    }

    /// <summary>
    /// Generate DDOQT-compatible export hash
    /// </summary>
    public string GenerateExportHash()
    {
        lock (_questLock)
        {
            // Build the DDOQT format: { questId: { difficulty } }
            var exportData = new Dictionary<string, DdoqtCompletion>();

            foreach (var quest in _completedQuests)
            {
                // Only export quests that matched in DDOQT lookup
                if (string.IsNullOrEmpty(quest.QuestId) || !quest.MatchedInDdoqt)
                    continue;

                // Only keep highest difficulty per quest
                if (!exportData.ContainsKey(quest.QuestId) ||
                    GetDifficultyRank(quest.Difficulty) > GetDifficultyRank(exportData[quest.QuestId].difficulty))
                {
                    exportData[quest.QuestId] = new DdoqtCompletion
                    {
                        difficulty = quest.Difficulty
                    };
                }
            }

            var json = JsonSerializer.Serialize(exportData);
            _log.Info($"Export JSON (first 500 chars): {json.Substring(0, Math.Min(500, json.Length))}");
            return LZString.CompressToEncodedURIComponent(json);
        }
    }

    /// <summary>
    /// Generate a CSV of all completed quests, including ones not matched in DDOQT
    /// </summary>
    public string GenerateCsv()
    {
        var csv = new StringBuilder();
        csv.AppendLine("Quest Name,Quest ID,Level,Difficulty,Favour,Matched in DDOQT");

        lock (_questLock)
        {
            var quests = _completedQuests
                .OrderBy(q => q.QuestLevel)
                .ThenBy(q => q.QuestName, StringComparer.OrdinalIgnoreCase);

            foreach (var quest in quests)
            {
                csv.AppendLine(string.Join(",",
                    CsvField(quest.QuestName),
                    CsvField(quest.QuestId),
                    quest.QuestLevel.ToString(CultureInfo.InvariantCulture),
                    CsvField(quest.Difficulty),
                    quest.CurrentFavor.ToString(CultureInfo.InvariantCulture),
                    quest.MatchedInDdoqt ? "Yes" : "No"));
            }
        }

        return csv.ToString();
    }

    // Quote fields containing commas, quotes or line breaks, doubling any embedded quotes
    private static string CsvField(string? value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private int GetDifficultyRank(string difficulty)
    {
        return difficulty switch
        {
            "Reaper" => 4,
            "Elite" => 3,
            "Hard" => 2,
            "Normal" => 1,
            _ => 0
        };
    }

    public string GetExportUrl()
    {
        var hash = GenerateExportHash();
        return $"https://qt.ddotools.xyz/#{hash}";
    }
    
    public string GetAutoSyncUrl(string action)
    {
        var hash = GenerateExportHash();
        return $"https://qt.ddotools.xyz/?action={action}#{hash}";
    }

    public int GetCompletedQuestCount()
    {
        lock (_questLock)
        {
            // Only count quests that matched in DDOQT lookup
            return _completedQuests.Count(q => q.MatchedInDdoqt);
        }
    }
    
    /// <summary>
    /// Whether the plugin saves QuestTracker.log in its folder
    /// </summary>
    public bool LoggingEnabled => _settings.LoggingEnabled;

    public void SetLoggingEnabled(bool enabled)
    {
        if (enabled == _settings.LoggingEnabled) return;

        // Log the change while the file is still being written to, either side of the switch
        if (!enabled) _log.Info("Log file turned off");
        _settings.LoggingEnabled = enabled;
        _log.FileEnabled = enabled;
        if (enabled) _log.Info($"Log file turned on (Quest Tracker {GetType().Assembly.GetName().Version})");

        SaveSettings();
    }

    private string GetSettingsFilePath() => Path.Combine(_folder ?? "", "settings.json");

    private void LoadSettings()
    {
        try
        {
            var settingsFile = GetSettingsFilePath();
            if (File.Exists(settingsFile))
                _settings = JsonSerializer.Deserialize<PluginSettings>(File.ReadAllText(settingsFile)) ?? new PluginSettings();
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load settings, using defaults", ex);
            _settings = new PluginSettings();
        }

        _log.FileEnabled = _settings.LoggingEnabled;
    }

    private void SaveSettings()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(GetSettingsFilePath(), json);
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save settings", ex);
        }
    }

    private string GetCacheFilePath()
    {
        return Path.Combine(_folder ?? "", $"quest_cache_{_currentCharacterId}.json");
    }

    private void SaveQuestDataToFile()
    {
        try
        {
            lock (_questLock)
            {
                // Nothing to save to without a character, and guessing one is how caches get mixed up
                if (_currentCharacterId == 0)
                {
                    _log.Warn("Not saving quests, no character is logged in");
                    return;
                }

                var cacheFile = GetCacheFilePath();
                var json = JsonSerializer.Serialize(_completedQuests, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(cacheFile, json);
                _log.Info($"Saved {_completedQuests.Count} quests to cache file");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to save quest data to file", ex);
        }
    }
    
    /// <summary>
    /// Replaces the quest list with the current character's cache, or an empty list if there isn't one
    /// </summary>
    private void LoadQuestDataFromFile()
    {
        var quests = new List<QuestCompletion>();
        try
        {
            var cacheFile = GetCacheFilePath();
            if (_currentCharacterId != 0 && File.Exists(cacheFile))
            {
                var json = File.ReadAllText(cacheFile);
                quests = JsonSerializer.Deserialize<List<QuestCompletion>>(json) ?? new List<QuestCompletion>();
                _log.Info($"Loaded {quests.Count} quests from cache file for character {_currentCharacterId}");
            }
            else
            {
                _log.Info($"No quest cache file for character {_currentCharacterId}, waiting for the quest records");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load quest data from file", ex);
        }

        lock (_questLock)
        {
            _completedQuests = quests;
        }
    }

    private void ProcessQuestRecords(IEnumerable<IStoryQuestRecord> questRecords)
    {
        lock (_questLock)
        {
            // Keep completions the records can't show. These are chain dungeons (their DIDs aren't in the
            // records at all) and quests completed live since login, which the login snapshot predates.
            // Anything else the records list as not completed is dropped. That also cleans out quests
            // a cache picked up from another character.
            var recordDids = new HashSet<uint>(questRecords.Select(q => q.QuestDid));
            var sdkCompletedDids = new HashSet<uint>(
                questRecords.Where(q => IsQuestCompleted(q)).Select(q => q.QuestDid));
            var preservedCompletions = _completedQuests
                .Where(c => !sdkCompletedDids.Contains(c.QuestDid) &&
                            (!recordDids.Contains(c.QuestDid) ||
                             _sessionCompletionKeys.Contains(CompletionKey(c.QuestDid, c.QuestLevel))))
                .ToList();
            var dropped = _completedQuests
                .Where(c => !sdkCompletedDids.Contains(c.QuestDid))
                .Except(preservedCompletions)
                .Select(c => $"{c.QuestName} (L{c.QuestLevel})")
                .ToList();
            if (dropped.Count > 0)
            {
                const int maxNamesLogged = 100;
                var names = string.Join(", ", dropped.Take(maxNamesLogged));
                var more = dropped.Count > maxNamesLogged ? $" and {dropped.Count - maxNamesLogged} more" : "";
                _log.Info($"Dropped {dropped.Count} cached completions that this character's quest records show as not completed: {names}{more}");
            }

            // The snapshot only says Elite for a Reaper run (and is stale until the next login), so
            // remember what was already recorded to avoid overwriting it with a lower difficulty
            var previousByKey = new Dictionary<string, QuestCompletion>();
            foreach (var previous in _completedQuests.Where(c => sdkCompletedDids.Contains(c.QuestDid)))
                previousByKey[CompletionKey(previous.QuestDid, previous.QuestLevel)] = previous;

            _completedQuests.Clear();
            
            // Add back preserved completions (chain dungeon completions detected in real-time)
            _completedQuests.AddRange(preservedCompletions);
            if (preservedCompletions.Count > 0)
            {
                _log.Info($"Preserved {preservedCompletions.Count} chain dungeon completions not in SDK records");
            }

            foreach (var quest in questRecords)
            {
                // Only include completed quests (HighestDifficultyCompleted > 0)
                if (IsQuestCompleted(quest))
                {
                    // Use weenie name lookup to get actual dungeon name (for chain quests)
                    // Falls back to quest.QuestName if weenie lookup fails
                    var questName = GetActualQuestName(quest.QuestDid) ?? quest.QuestName ?? "";
                    var baseLevel = (int)quest.QuestLevel;
                    var rawDifficulty = quest.HighestDifficultyCompleted;
                    var difficultyStr = DetermineDifficulty(quest);
                    var favor = quest.CurrentFavor ?? 0;
                    
                    // Check if this quest has heroic/epic/legendary variants
                    var variantLevels = QuestVariantLookup.GetVariantLevels(questName);
                    
                    if (variantLevels != null && variantLevels.Length > 1)
                    {
                        // Quest has variants - determine which tiers to mark as completed
                        // Quests are either Heroic/Epic or Heroic/Legendary (never all three).
                        // rawDifficulty 1-4 = Heroic only, 5+ = higher tier also completed.
                        
                        foreach (var level in variantLevels)
                        {
                            bool shouldInclude = false;
                            
                            if (level <= 19) // Heroic
                            {
                                shouldInclude = true;
                            }
                            else // Epic (20-29) or Legendary (30+)
                            {
                                shouldInclude = rawDifficulty >= 5;
                            }
                            
                            if (shouldInclude)
                            {
                                var (questId, matched) = ConvertToQuestId(questName, level);
                                AddKeepingHigherDifficulty(previousByKey, new QuestCompletion
                                {
                                    QuestDid = quest.QuestDid,
                                    QuestName = questName,
                                    QuestId = questId,
                                    Difficulty = difficultyStr,
                                    CurrentFavor = favor,
                                    QuestLevel = level,
                                    MatchedInDdoqt = matched
                                });
                            }
                        }
                    }
                    else
                    {
                        // Single version quest - add normally
                        var (questId, matched) = ConvertToQuestId(questName, baseLevel);
                        AddKeepingHigherDifficulty(previousByKey, new QuestCompletion
                        {
                            QuestDid = quest.QuestDid,
                            QuestName = questName,
                            QuestId = questId,
                            Difficulty = difficultyStr,
                            CurrentFavor = favor,
                            QuestLevel = baseLevel,
                            MatchedInDdoqt = matched
                        });
                    }
                }
            }

            _log.Info($"Processed {_completedQuests.Count} completed quests, {_completedQuests.Count(q => q.MatchedInDdoqt)} matched in DDOQT");
        }

        // Save to cache file for persistence across DH restarts
        SaveQuestDataToFile();
    }

    private static string CompletionKey(uint questDid, int questLevel) => $"{questDid}:{questLevel}";

    /// <summary>
    /// Adds a completion rebuilt from the quest records, keeping the difficulty already recorded
    /// for that quest and level when it was higher. Call with _questLock held.
    /// </summary>
    private void AddKeepingHigherDifficulty(Dictionary<string, QuestCompletion> previousByKey, QuestCompletion completion)
    {
        if (previousByKey.TryGetValue(CompletionKey(completion.QuestDid, completion.QuestLevel), out var previous) &&
            GetDifficultyRank(previous.Difficulty) >= GetDifficultyRank(completion.Difficulty))
        {
            completion.Difficulty = previous.Difficulty;
        }

        _completedQuests.Add(completion);
    }

    public IEnumerable<QuestCompletion> GetCompletedQuests()
    {
        lock (_questLock)
        {
            return _completedQuests.ToList();
        }
    }

    public void Dispose()
    {
        _log.Info("Quest Tracker plugin terminated");
    }
}

/// <summary>
/// Internal quest completion data
/// </summary>
public class QuestCompletion
{
    public uint QuestDid { get; set; }
    public string QuestName { get; set; } = "";
    public string QuestId { get; set; } = "";
    public string Difficulty { get; set; } = "Normal";
    public int CurrentFavor { get; set; }
    public int QuestLevel { get; set; }
    public bool MatchedInDdoqt { get; set; }
}

/// <summary>
/// Player settings, saved as settings.json in the plugin folder
/// </summary>
public class PluginSettings
{
    public bool LoggingEnabled { get; set; }
}

/// <summary>
/// DDOQT format for quest completion
/// </summary>
public class DdoqtCompletion
{
    public string difficulty { get; set; } = "Normal";
}
