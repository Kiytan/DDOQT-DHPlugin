using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using log4net;
using VoK.Sdk;
using VoK.Sdk.Ddo;
using VoK.Sdk.Events;
using VoK.Sdk.Plugins;

namespace QuestTracker;

/// <summary>
/// Quest Tracker Plugin for Dungeon Helper
/// Tracks completed quests and exports them in DDOQT-compatible format
/// </summary>
public class Plugin : IDdoPlugin
{
    public Guid PluginId => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    public GameId Game => GameId.DDO;
    public string PluginKey => "quest-tracker-ddoqt";
    public string Name => "Quest Tracker";
    public string Description => "Tracks completed quests and exports for DDOQT (qt.ddotools.xyz)";
    public string Author => "QuestTracker";
    public Version Version => GetType().Assembly.GetName().Version ?? new Version(1, 0, 0);

    private IDdoGameDataProvider? _gameDataProvider;
    private string? _folder;
    private QuestTrackerUI? _ui;
    private readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod()?.DeclaringType);

    // Stores current quest data
    private List<QuestCompletion> _completedQuests = new();
    private IReadOnlyCollection<IStoryQuestRecord>? _lastQuestRecords;
    private readonly object _questLock = new();
    private IDisposable? _eventRegistration;
    
    // Track current instance quest for completion detection
    private uint? _currentInstanceQuestDid;
    private uint? _currentQuestAreaId;
    private string _currentInstanceDifficulty = "Elite";
    
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

    public void Initialize(IDdoGameDataProvider gameDataProvider, string folder)
    {
        _gameDataProvider = gameDataProvider;
        _folder = folder;
        _ui = new QuestTrackerUI();

        _log.Info("Quest Tracker plugin initialized");
        
        // Load cached quest data from previous session
        LoadQuestDataFromFile();

        // Subscribe to events
        try
        {
            if (_gameDataProvider.EventProvider != null)
            {
                _gameDataProvider.EventProvider.OnAllStoryQuestRecords?.AddHandler(OnQuestRecordsReceived);
                _gameDataProvider.EventProvider.OnLogin?.AddHandler(OnLoginReceived);
                _gameDataProvider.EventProvider.OnMapAreaChange?.AddHandler(OnMapAreaChange);
                _gameDataProvider.EventProvider.OnAddAlert?.AddHandler(OnAddAlert);
                _gameDataProvider.EventProvider.OnPortalActivate?.AddHandler(OnPortalActivate);
            }
            else
            {
                _log.Warn("EventProvider is null");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to subscribe to quest events", ex);
        }
    }
    
    private async Task OnMapAreaChange(uint areaId)
    {
        try
        {
            // Wait a moment for SDK to populate instance data
            await Task.Delay(1000);
            
            var newDid = _gameDataProvider?.GetInstanceQuestDid();
            
            _log.Info($"MapAreaChange - areaId: {areaId}, previousAreaId: {_currentQuestAreaId}, " +
                      $"previousDid: {_currentInstanceQuestDid}, newDid: {newDid}, " +
                      $"InTown: {_gameDataProvider?.InTown()}");
            
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
    
    
    
    private Task OnPortalActivate(IPortalInfo portalInfo)
    {
        try
        {
            if (portalInfo != null)
            {
                // Log all available portal info including QuestBestowed for research
                _log.Info($"PortalActivate - ActiveInstanceDid: {portalInfo.ActiveInstanceDid}, " +
                          $"QuestGenericDifficulty: {portalInfo.QuestGenericDifficulty}, " +
                          $"AvailableQuests: [{string.Join(",", portalInfo.AvailableQuests ?? new List<uint>())}], " +
                          $"GetCurrentQuestDid: {_gameDataProvider?.GetCurrentQuestDid()}");
                
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
                    var currentQuestDid = _gameDataProvider?.GetCurrentQuestDid();
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
        return Task.CompletedTask;
    }
    
    private Task OnAddAlert(IAddAlert alert)
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
                    
                    // Try to extract difficulty from alert text (e.g., "Quest Completed on Elite")
                    var alertDifficulty = ExtractDifficultyFromAlert(desc) ?? ExtractDifficultyFromAlert(title);
                    if (!string.IsNullOrEmpty(alertDifficulty))
                    {
                        _log.Info($"Difficulty extracted from alert text: {alertDifficulty}");
                        _currentInstanceDifficulty = alertDifficulty;
                    }
                    
                    HandleQuestCompletion();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error("Error processing Alert", ex);
        }
        return Task.CompletedTask;
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
    
    private void HandleQuestCompletion()
    {
        try
        {
            // Try multiple sources for quest DID
            var questDid = _currentInstanceQuestDid;
            var instanceDid = _gameDataProvider?.GetInstanceQuestDid();
            var currentDid = _gameDataProvider?.GetCurrentQuestDid();
            
            _log.Info($"Quest completion - tracked DID: {questDid}, instance DID: {instanceDid}, current DID: {currentDid}, difficulty: {_currentInstanceDifficulty}");
            
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
            
            _log.Info($"Quest completion - final DID: {questDid} from source: {didSource}");
            
            if (questDid.HasValue && questDid.Value != 0 && _questRecordsByDid.TryGetValue(questDid.Value, out var questRecord))
            {
                // Prefer weenie name (individual dungeon) over record name (chain name)
                var actualName = GetActualQuestName(questDid.Value);
                var usingName = actualName ?? questRecord.QuestName ?? "";
                
                _log.Info($"Quest completed - RecordName: '{questRecord.QuestName}', WeenieName: '{actualName}', UsingName: '{usingName}', " +
                          $"difficulty: {_currentInstanceDifficulty}, DID: {questDid.Value}");
                
                // Pass the resolved name but keep the record's favor/level metadata
                AddCompletedQuestFromRecord(questRecord, _currentInstanceDifficulty, usingName);
                _ui?.RefreshQuestCount();
                
                // Reset difficulty after completion for next quest
                _currentInstanceDifficulty = "Elite";
            }
            else if (questDid.HasValue && questDid.Value != 0)
            {
                // Fallback: DID not in quest records - try weenie name lookup (for chain quest individual dungeons)
                var weenieProps = _gameDataProvider?.GetWeenieProperties(questDid.Value);
                var questName = weenieProps?.GetType().GetProperty("Name")?.GetValue(weenieProps) as string;
                
                if (!string.IsNullOrEmpty(questName))
                {
                    _log.Info($"Quest completed (via weenie name): {questName} at {_currentInstanceDifficulty}");
                    AddCompletedQuestByName(questDid.Value, questName, _currentInstanceDifficulty);
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
    
    private void AddCompletedQuestFromRecord(IStoryQuestRecord quest, string difficulty, string? overriddenName = null)
    {
        lock (_questLock)
        {
            var questName = overriddenName ?? quest.QuestName ?? "";
            var baseLevel = (int)quest.QuestLevel;
            var completedDate = DateTime.UtcNow.ToString("o");
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
                        
                        // Check if already exists for this level
                        var existingEntry = _completedQuests.FirstOrDefault(q => q.QuestDid == quest.QuestDid && q.QuestLevel == level);
                        
                        if (existingEntry != null)
                        {
                            // If it exists, only update if the new difficulty is higher
                            if (GetDifficultyRank(difficulty) > GetDifficultyRank(existingEntry.Difficulty))
                            {
                                existingEntry.Difficulty = difficulty;
                                existingEntry.CompletedDate = completedDate;
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
                                CompletedDate = completedDate,
                                CurrentFavor = favor,
                                QuestLevel = level,
                                MatchedInDdoqt = matched
                            });
                            _log.Info($"Added newly completed quest variant: {questName} (Level {level}, {difficulty})");
                            SaveQuestDataToFile();
                        }
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
                        existingEntry.CompletedDate = completedDate;
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
                        CompletedDate = completedDate,
                        CurrentFavor = favor,
                        QuestLevel = baseLevel,
                        MatchedInDdoqt = matched
                    });
                    _log.Info($"Added newly completed quest: {questName} (Level {baseLevel}, {difficulty})");
                    SaveQuestDataToFile();
                }
            }
        }
    }
    
    private void AddCompletedQuestByName(uint questDid, string questName, string difficulty)
    {
        lock (_questLock)
        {
            var completedDate = DateTime.UtcNow.ToString("o");
            
            // Try to find base level from variant lookup, default to 0
            var variantLevels = QuestVariantLookup.GetVariantLevels(questName);
            var baseLevel = variantLevels?.FirstOrDefault() ?? 0;
            
            var (questId, matched) = ConvertToQuestId(questName, baseLevel);
            
            // Check if already exists for this level
            var existingEntry = _completedQuests.FirstOrDefault(q => q.QuestDid == questDid && q.QuestLevel == baseLevel);
            
            if (existingEntry != null)
            {
                // If it exists, only update if the new difficulty is higher
                if (GetDifficultyRank(difficulty) > GetDifficultyRank(existingEntry.Difficulty))
                {
                    existingEntry.Difficulty = difficulty;
                    existingEntry.CompletedDate = completedDate;
                    _log.Info($"Upgraded existing quest (by name): {questName} (Level {baseLevel}) to {difficulty}");
                    SaveQuestDataToFile();
                }
            }
            else
            {
                // Add new entry
                _completedQuests.Add(new QuestCompletion
                {
                    QuestDid = questDid,
                    QuestName = questName,
                    QuestId = questId,
                    Difficulty = difficulty,
                    CompletedDate = completedDate,
                    CurrentFavor = 0, // Unknown for chain quest dungeons
                    QuestLevel = baseLevel,
                    MatchedInDdoqt = matched
                });
                _log.Info($"Added newly completed quest (by name): {questName} (Level {baseLevel}, {difficulty})");
                SaveQuestDataToFile();
            }
        }
    }
    
    private Task OnLoginReceived(ulong characterId)
    {
        _log.Info($"Character login - character ID {characterId}");
        
        lock (_questLock)
        {
            _currentCharacterId = characterId;
            
            // Clear previous character's state to prevent cross-contamination
            _completedQuests.Clear();
            _questRecordsByDid.Clear();
            
            // Load character-specific cache
            LoadQuestDataFromFile();
        }
        
        // If quest records were already received (fires before login), re-process them
        // now that we have the correct character ID for cache file naming
        if (_lastQuestRecords != null)
        {
            _log.Info("Re-processing quest records after login (received before character ID was set)");
            _questRecordsByDid = _lastQuestRecords.ToDictionary(q => q.QuestDid, q => q);
            ProcessQuestRecords(_lastQuestRecords);
            _ui?.RefreshQuestCount();
        }
        
        return Task.CompletedTask;
    }

    private Task OnQuestRecordsReceived(IReadOnlyCollection<IStoryQuestRecord> questRecords)
    {
        try
        {
            _lastQuestRecords = questRecords;
            
            // Build DID lookup dictionary for all quests
            _questRecordsByDid = questRecords.ToDictionary(q => q.QuestDid, q => q);
            
            // Generate mismatch report for debugging name differences
            GenerateQuestNameMismatchReport(questRecords);
            
            ProcessQuestRecords(questRecords);
            _ui?.RefreshQuestCount();
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
        if (_lastQuestRecords != null)
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
        // Quest is completed if HighestDifficultyCompleted has a value > 0
        return quest.HighestDifficultyCompleted > 0;
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
            var weenieProps = _gameDataProvider?.GetWeenieProperties(questDid);
            if (weenieProps != null)
            {
                var name = weenieProps.GetType().GetProperty("Name")?.GetValue(weenieProps) as string;
                if (!string.IsNullOrEmpty(name))
                {
                    return name;
                }
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
        // Heroic tier: 1 = Normal, 2 = Hard, 3 = Elite, 4 = Reaper
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
            // Build the DDOQT format: { questId: { difficulty, completedDate } }
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
                        difficulty = quest.Difficulty,
                        completedDate = quest.CompletedDate
                    };
                }
            }

            var json = JsonSerializer.Serialize(exportData);
            _log.Info($"Export JSON (first 500 chars): {json.Substring(0, Math.Min(500, json.Length))}");
            return LZString.CompressToEncodedURIComponent(json);
        }
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
    
    private string GetCacheFilePath()
    {
        var filename = _currentCharacterId != 0 ? $"quest_cache_{_currentCharacterId}.json" : "quest_cache.json";
        return Path.Combine(_folder ?? "", filename);
    }
    
    private void SaveQuestDataToFile()
    {
        try
        {
            var cacheFile = GetCacheFilePath();
            lock (_questLock)
            {
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
    
    private void LoadQuestDataFromFile()
    {
        try
        {
            var cacheFile = GetCacheFilePath();
            if (File.Exists(cacheFile))
            {
                var json = File.ReadAllText(cacheFile);
                var quests = JsonSerializer.Deserialize<List<QuestCompletion>>(json);
                if (quests != null && quests.Count > 0)
                {
                    lock (_questLock)
                    {
                        _completedQuests = quests;
                    }
                    _log.Info($"Loaded {quests.Count} quests from cache file");
                }
            }
            else
            {
                _log.Info("No quest cache file found - will load on character login");
            }
        }
        catch (Exception ex)
        {
            _log.Error("Failed to load quest data from file", ex);
        }
    }

    private void ProcessQuestRecords(IEnumerable<IStoryQuestRecord> questRecords)
    {
        lock (_questLock)
        {
            // Preserve real-time completions that the SDK snapshot doesn't reflect yet.
            // Only exclude completions whose DID the SDK shows as *completed* (HighestDifficultyCompleted > 0).
            // Quests completed during this session won't be in the SDK snapshot.
            var sdkCompletedDids = new HashSet<uint>(
                questRecords.Where(q => IsQuestCompleted(q)).Select(q => q.QuestDid));
            var preservedCompletions = _completedQuests
                .Where(c => !sdkCompletedDids.Contains(c.QuestDid))
                .ToList();
            
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
                    var completedDate = DateTime.UtcNow.ToString("o");
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
                                _completedQuests.Add(new QuestCompletion
                                {
                                    QuestDid = quest.QuestDid,
                                    QuestName = questName,
                                    QuestId = questId,
                                    Difficulty = difficultyStr,
                                    CompletedDate = completedDate,
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
                        _completedQuests.Add(new QuestCompletion
                        {
                            QuestDid = quest.QuestDid,
                            QuestName = questName,
                            QuestId = questId,
                            Difficulty = difficultyStr,
                            CompletedDate = completedDate,
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

    public IEnumerable<QuestCompletion> GetCompletedQuests()
    {
        lock (_questLock)
        {
            return _completedQuests.ToList();
        }
    }

    public IPluginUI? GetPluginUI()
    {
        return _ui;
    }

    public void Terminate()
    {
        _eventRegistration?.Dispose();
        _eventRegistration = null;
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
    public string CompletedDate { get; set; } = "";
    public int CurrentFavor { get; set; }
    public int QuestLevel { get; set; }
    public bool MatchedInDdoqt { get; set; }
}

/// <summary>
/// DDOQT format for quest completion
/// </summary>
public class DdoqtCompletion
{
    public string difficulty { get; set; } = "Normal";
    public string completedDate { get; set; } = "";
}
