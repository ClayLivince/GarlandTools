﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Saint = SaintCoinach.Xiv;
using Garland.Data.Models;
using System.IO;
using System.Text.RegularExpressions;

namespace Garland.Data.Modules
{
    public class Quests : Module
    {
        Dictionary<int, List<int>> _npcsByQuestKey = new Dictionary<int, List<int>>();

        public override string Name => "Quests";

        public override void Start()
        {
            IndexInvolvedNpcs();
            BuildQuests();
            LinkQuestRequirements();
            BuildJournalGenres();
        }

        void IndexInvolvedNpcs()
        {
            foreach (var lENpcResident_Quest in _builder.Libra.Table<Libra.ENpcResident_Quest>())
            {
                if (!_npcsByQuestKey.TryGetValue(lENpcResident_Quest.Quest_Key, out var npcs))
                    _npcsByQuestKey[lENpcResident_Quest.Quest_Key] = npcs = new List<int>();
                npcs.Add(lENpcResident_Quest.ENpcResident_Key);
            }
        }

        void BuildQuests()
        {
            var lQuestsByKey = _builder.Libra.Table<Libra.Quest>().ToDictionary(q => q.Key);

            foreach (var sQuest in _builder.Sheet<Saint.Quest>())
            {
                if (sQuest.Key == 65536 || sQuest.Name == "")
                    continue; // Test quests

                dynamic quest = new JObject();
                quest.id = sQuest.Key;
                _builder.Localize.Strings((JObject)quest, sQuest, Utils.SanitizeQuestName, "Name");
                quest.patch = PatchDatabase.Get("quest", sQuest.Key);
                quest.sort = sQuest.SortKey;

                // Quest location
                var questIssuer = (sQuest.IssuingENpc?.Locations?.Count() ?? 0) > 0 ? sQuest.IssuingENpc : null;
                var sPlaceName = sQuest.PlaceName;
                if (sPlaceName.Name == "" && questIssuer != null)
                    sPlaceName = questIssuer.Locations.First().PlaceName;

                _builder.Localize.Column((JObject)quest, sPlaceName, "Name", "location",
                    x => x == "" ? "???" : x.ToString());

                // Repeatability
                if (sQuest.RepeatInterval == Saint.QuestRepeatInterval.Daily)
                    quest.interval = "daily";
                else if (sQuest.RepeatInterval == Saint.QuestRepeatInterval.Weekly)
                    quest.interval = "weekly";

                if (sQuest.IsRepeatable)
                    quest.repeatable = 1;

                // Miscellaneous
                if (sQuest.Icon != null)
                    quest.icon = IconDatabase.EnsureEntry("quest", sQuest.Icon);

                if (sQuest.BeastTribe.Key != 0)
                    quest.beast = sQuest.BeastTribe.Key;

                ImportQuestEventIcon(quest, sQuest);

                // Quest issuer
                if (questIssuer != null)
                {
                    var npc = AddQuestNpc(quest, questIssuer);
                    if (npc != null)
                        quest.issuer = questIssuer.Key;
                }

                // Quest target
                var questTarget = sQuest.TargetENpc;
                if (questTarget != null)
                {
                    var npc = AddQuestNpc(quest, questTarget);
                    if (npc != null)
                        quest.target = questTarget.Key;
                }

                // Involved
                if (_npcsByQuestKey.TryGetValue(sQuest.Key, out var involvedNpcKeys))
                {
                    foreach (var npcKey in involvedNpcKeys)
                    {
                        var sInvolvedEnpc = _builder.Realm.GameData.ENpcs[npcKey];
                        if (sInvolvedEnpc == null || sInvolvedEnpc == questTarget || sInvolvedEnpc == questIssuer)
                            continue;

                        var npc = AddQuestNpc(quest, sInvolvedEnpc);
                        if (npc == null)
                            continue;

                        if (quest.involved == null)
                            quest.involved = new JArray();
                        quest.involved.Add(npcKey);
                    }
                }

                // Journal Genre
                quest.genre = sQuest.JournalGenre.Key;

                // Rewards
                dynamic rewards = new JObject();
                if (sQuest.Rewards.Gil > 0)
                    rewards.gil = sQuest.Rewards.Gil;

                if (sQuest.Rewards.Emote.Key > 0)
                    rewards.emote = sQuest.Rewards.Emote.Name.ToString();

                if (sQuest.Rewards.ClassJob.Key > 0)
                    rewards.job = sQuest.Rewards.ClassJob.Key;

                if (sQuest.AsInt32("CurrencyRewardCount") > 0)
                    rewards.gcseal = sQuest.AsInt32("CurrencyRewardCount");

                if (sQuest.Rewards.Action.Key > 0)
                {
                    rewards.action = sQuest.Rewards.Action.Key;

                    _builder.Db.AddReference(quest, "action", sQuest.Rewards.Action.Key, false);
                }

                var sInstanceContentReward = sQuest.Rewards.InstanceContent;
                if (sInstanceContentReward.Key > 0)
                {
                    var instance = _builder.Db.Instances.FirstOrDefault(i => i.id == sInstanceContentReward.Key);
                    if (instance != null)
                    {
                        instance.unlockedByQuest = sQuest.Key;
                        rewards.instance = sInstanceContentReward.Key;

                        _builder.Db.AddReference(quest, "instance", sInstanceContentReward.Key, false);
                        _builder.Db.AddReference(instance, "quest", sQuest.Key, false);
                    }
                }

                if (sQuest.Rewards.Reputation > 0)
                    rewards.reputation = sQuest.Rewards.Reputation;

                if (sQuest.Rewards.QuestRewardOther.Name == "Aether Current")
                    rewards.aetherCurrent = 1;

                foreach (var sQuestRewardItemGroup in sQuest.Rewards.Items)
                {
                    foreach (var sQuestRewardItem in sQuestRewardItemGroup.Items)
                    {
                        if (rewards.items == null)
                            rewards.items = new JArray();

                        var maxCount = sQuestRewardItem.Counts.Max();

                        dynamic o = new JObject();
                        if (maxCount > 1)
                            o.num = maxCount;
                        o.id = sQuestRewardItem.Item.Key;

                        if (sQuestRewardItemGroup.Type == Saint.QuestRewardGroupType.One)
                            o.one = 1;

                        if (sQuestRewardItem.IsHq)
                            o.hq = 1;

                        rewards.items.Add(o);

                        try
                        {
                            var item = _builder.Db.ItemsById[sQuestRewardItem.Item.Key];
                            if (item.quests == null)
                                item.quests = new JArray();
                            JArray quests = item.quests;

                            if (!quests.Any(id => ((int)id) == sQuest.Key))
                                quests.Add(sQuest.Key);
                            _builder.Db.AddReference(item, "quest", sQuest.Key, false);

                        }
                        catch (KeyNotFoundException ignored)
                        {
                            DatabaseBuilder.PrintLine($"Reward item '{sQuestRewardItem.Item.Key}' not found for Quest '{quest.Key}'.");
                        }

                        _builder.Db.AddReference(quest, "item", sQuestRewardItem.Item.Key, false);
                    }
                }

                // Libra supplemental rewards.
                if (lQuestsByKey.TryGetValue(sQuest.Key, out var lQuest))
                {
                    dynamic data = JsonConvert.DeserializeObject(lQuest.data);
                    int xp = 0;
                    if (data.exp != null && int.TryParse((string)data.exp, out xp))
                        rewards.xp = xp;
                }

                // Scripts
                var instructions = ScriptInstruction.Read(sQuest, 50);

                // Script instance unlocks.
                if (!sQuest.IsRepeatable)
                {
                    var instanceReferences = instructions.Where(i => i.Label.StartsWith("INSTANCEDUNGEON")).ToArray();
                    foreach (var instanceReference in instanceReferences)
                    {
                        var key = (int)instanceReference.Argument;
                        var instance = _builder.Db.Instances.FirstOrDefault(i => ((int)i.id) == key);
                        if (instance == null)
                            continue; // Probably a guildhest.

                        if (instance.unlockedByQuest != null)
                            continue;

                        if (!instructions.Any(i => i.Label == "UNLOCK_ADD_NEW_CONTENT_TO_CF" || i.Label.StartsWith("UNLOCK_DUNGEON")))
                        {
                            // Some quests reference instances for the retrieval of items.
                            // Don't treat these as unlocks.
                            if (instructions.Any(i => i.Label.StartsWith("LOC_ITEM")))
                                continue;
                        }

                        instance.unlockedByQuest = sQuest.Key;
                        rewards.instance = key;

                        _builder.Db.AddReference(quest, "instance", key, false);
                        _builder.Db.AddReference(instance, "quest", sQuest.Key, false);
                    }
                }

                // Used items.
                foreach (var instruction in instructions)
                {
                    if (!instruction.Label.StartsWith("RITEM") && !instruction.Label.StartsWith("QUEST_ITEM"))
                        continue;

                    var key = (int)instruction.Argument;
                    if (_builder.Db.ItemsById.TryGetValue(key, out var item))
                    {
                        if (item.usedInQuest == null)
                            item.usedInQuest = new JArray();

                        JArray usedInQuest = item.usedInQuest;
                        if (usedInQuest.Any(i => (int)i == sQuest.Key))
                            continue;

                        item.usedInQuest.Add(sQuest.Key);

                        if (quest.usedItems == null)
                            quest.usedItems = new JArray();
                        quest.usedItems.Add(key);

                        _builder.Db.AddReference(item, "quest", sQuest.Key, false);
                        _builder.Db.AddReference(quest, "item", key, false);
                    }
                }

                ImportQuestLoreUsedItems(quest, sQuest, instructions);

                if (((JObject)rewards).Count > 0)
                    quest.reward = rewards;

                ImportQuestRequirements(quest, sQuest);

                _builder.Db.Quests.Add(quest);
                _builder.Db.QuestsById[sQuest.Key] = quest;
            }
        }

        void LinkQuestUsedItems(dynamic quest, Saint.Quest sQuest, int key)
        {
            if (_builder.Db.ItemsById.TryGetValue(key, out var item))
            {
                if (item.usedInQuest == null)
                    item.usedInQuest = new JArray();

                JArray usedInQuest = item.usedInQuest;
                if (usedInQuest.Any(i => (int)i == sQuest.Key))
                    return;

                item.usedInQuest.Add(sQuest.Key);

                if (quest.usedItems == null)
                    quest.usedItems = new JArray();
                quest.usedItems.Add(key);

                _builder.Db.AddReference(item, "quest", sQuest.Key, false);
                _builder.Db.AddReference(quest, "item", key, false);
            }
        }

        void LinkQuestRequirements()
        {
            foreach (var quest in _builder.Db.Quests)
            {
                var requirements = quest.reqs;
                if (requirements != null && requirements.quests != null)
                {
                    var questId = (int)quest.id;

                    foreach (int requiredId in requirements.quests)
                    {
                        var required = _builder.Db.QuestsById[requiredId];
                        if (required.next == null)
                            required.next = new JArray();
                        required.next.Add(questId);

                        _builder.Db.AddReference(required, "quest", questId, false);
                    }
                }
            }
        }

        void BuildJournalGenres()
        {
            foreach (var sJournalGenre in _builder.Sheet<Saint.JournalGenre>())
            {
                dynamic genre = new JObject();
                genre.id = sJournalGenre.Key;
                genre.name = sJournalGenre.Name.ToString();

                if (sJournalGenre.Icon != null)
                    genre.icon = IconDatabase.EnsureEntry("journal", sJournalGenre.Icon);

                genre.category = sJournalGenre.JournalCategory.Name.ToString();
                genre.section = sJournalGenre.JournalCategory?.JournalSection?.Name?.ToString() ?? "Other Quests";
                _builder.Db.QuestJournalGenres.Add(genre);
            }
        }

        dynamic AddQuestNpc(dynamic quest, Saint.ENpc sNpc)
        {
            if (Hacks.IsNpcSkipped(sNpc))
                return null;

            var npc = _builder.Db.NpcsById[sNpc.Key];

            if (npc.quests == null)
                npc.quests = new JArray();

            var questId = (int)quest.id;
            JArray quests = npc.quests;
            if (!quests.Any(id => ((int)id) == questId))
            {
                quests.Add(questId);
                _builder.Db.AddReference(npc, "quest", questId, false);
                _builder.Db.AddReference(quest, "npc", (int)npc.id, false);
            }

            return npc;
        }

        void ImportQuestEventIcon(dynamic quest, Saint.Quest sQuest)
        {
            var sEventIconType = (Saint.IXivRow)sQuest["EventIconType"];
            if (sEventIconType == null)
            {
                byte index = (byte)sQuest.GetRaw("EventIconType");
                if (index > 32)
                {
                    index -= 32;
                    sEventIconType = _builder.Sheet("EventIconType").ElementAt(index);
                }
                else
                    throw new NotImplementedException();
            } 
            var baseIconIndex = (int)(UInt32)sEventIconType.GetRaw(0);

            // Mark function quests
            if (baseIconIndex == 071340)
                quest.unlocksFunction = 1;

            // Calculate the event icon to record.
            var questIconIndex = 0;
            if (sEventIconType.Key == 4)
                questIconIndex = baseIconIndex;
            else if (sQuest.IsRepeatable)
                questIconIndex = baseIconIndex + 2;
            else
                questIconIndex = baseIconIndex + 1;

            var eventIcon = SaintCoinach.Imaging.IconHelper.GetIcon(sQuest.Sheet.Collection.PackCollection, questIconIndex);
            quest.eventIcon = IconDatabase.EnsureEntry("event", eventIcon);
        }

        void ImportQuestRequirements(dynamic quest, Saint.Quest sQuest)
        {
            dynamic requirements = new JObject();

            if (sQuest.Requirements.BeastReputationRank.Key > 0)
                requirements.beastrank = sQuest.Requirements.BeastReputationRank.Key;

            if (sQuest.Requirements.RequiresHousing)
                requirements.house = 1;

            var requiresJobs = sQuest.Requirements.ClassJobs
                .Where(r => r.ClassJobCategory.Key != 1 || r.Level > 1)
                .Select(r =>
                {
                    dynamic rjob = new JObject();
                    rjob.lvl = r.Level;
                    rjob.id = r.ClassJobCategory.Key;
                    return rjob;
                })
                .ToArray();

            if (requiresJobs.Length > 0)
                requirements.jobs = new JArray(requiresJobs);

            if (sQuest.Requirements.GrandCompany.Key > 0)
                requirements.gc = sQuest.Requirements.GrandCompany.Key;

            if (sQuest.Requirements.GrandCompanyRank.Key > 0)
                requirements.gcrank = sQuest.Requirements.GrandCompanyRank.Key;

            if (sQuest.Requirements.Mount.Key > 0)
                requirements.mount = sQuest.Requirements.Mount.Key;

            // Instance Requirements
            var instanceContentRequirements = sQuest.Requirements.InstanceContent.InstanceContents
                .Select(i => i.Key)
                .ToArray();
            if (instanceContentRequirements.Length > 0)
            {
                requirements.instances = new JArray(instanceContentRequirements);
                foreach (var instanceKey in instanceContentRequirements)
                {
                    var instance = _builder.Db.Instances.First(i => i.id == instanceKey);
                    if (instance.requiredForQuest == null)
                        instance.requiredForQuest = new JArray();
                    instance.requiredForQuest.Add(sQuest.Key);

                    _builder.Db.AddReference(quest, "instance", instanceKey, false);
                    _builder.Db.AddReference(instance, "quest", sQuest.Key, false);
                }
            }

            // Quest Requirements
            var sPreviousQuestRequirements = sQuest.Requirements.PreviousQuest.PreviousQuests
                .Select(q => q.Key)
                .ToArray();
            if (sPreviousQuestRequirements.Length > 0)
            {
                if (sPreviousQuestRequirements.Length > 1)
                    requirements.questsType = sQuest.Requirements.PreviousQuest.Type.ToString().ToLower();

                requirements.quests = new JArray(sPreviousQuestRequirements);

                _builder.Db.AddReference(quest, "quest", sPreviousQuestRequirements, false);
            }

            if (((JObject)requirements).Count > 0)
                quest.reqs = requirements;

            // sQuest.Requirements.StartBell / EndBell: Usually seasonal events like halloween stuff at night.
            // sQuest.Requirements.QuestExclusion: For excluding other GCs / city quests.  Not important.
            // sQuest.Requirements.QuestLevelOffset: Not sure what this is for.
        }

        void ImportQuestLoreUsedItems(dynamic quest, Saint.Quest sQuest, ScriptInstruction[] instructions)
        {
            // Lore is imported through QuestsLore Module seperately, not mix it here, because it is way too slow.

            var idParts = sQuest.Id.ToString().Split('_');
            var idPath = new string(idParts[1].Take(3).ToArray());
            var textSheetId = string.Format("quest/{0}/{1}", idPath, sQuest.Id);
            var textSheet = _builder.Sheet(textSheetId)
                .Select(r => new { r.Key, Tokens = r[0].ToString().Split('_'), XivString = (SaintCoinach.Text.XivString)r[1] });

            foreach (var row in textSheet)
            {
                var rawString = row.XivString.ToString();
                if (rawString == "dummy" || rawString == "Dummy"
                    || rawString == "deleted" || rawString == "placeholder"
                    || rawString == "Marked for deletion"
                    || string.IsNullOrWhiteSpace(rawString))
                    continue;

                // We need to get indexed other sheets, so we can get
                //  some related Item which not listed in instructions.
                HtmlStringFormatter fmter = new HtmlStringFormatter();
                var str = row.XivString.Accept(fmter);
                foreach (var pair in fmter.IndexedSheetRows())
                {
                    if (pair.Item1.Equals("Item"))
                        LinkQuestUsedItems(quest, sQuest, pair.Item2);
                    else
                    {
                        if (quest.otherIndexes == null)
                        {
                            quest.otherIndexes = new JArray();
                        }
                        dynamic obj = new JObject();
                        obj.sheet = pair.Item1;
                        obj.row = pair.Item2;
                        quest.otherIndexes.Add(obj);
                    }
                }
            }

            // Script instructions
            //if (instructions.Length > 0)
            //{
            //    quest.script = new JArray();
            //    foreach (var instruction in instructions)
            //    {
            //        if (string.IsNullOrEmpty(instruction.Instruction))
            //            continue;

            //        quest.script.Add(ImportInstruction(_builder, instruction));
            //    }
            //}
        }
    }
}
