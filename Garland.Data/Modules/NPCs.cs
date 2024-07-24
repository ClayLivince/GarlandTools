﻿using Garland.Data.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SaintCoinach.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Saint = SaintCoinach.Xiv;

namespace Garland.Data.Modules
{
    public class NPCs : Module
    {
        Saint.IXivSheet<Saint.IXivRow> _sCharaMakeCustomize;
        Saint.IXivSheet<Saint.IXivRow> _sCharaMakeType;
        SaintCoinach.Graphics.ColorMap _colorMap;

        const int EyeColorOffset = 0 * 256; // Might just be for the left eye.
        const int HairHighlightColorOffset = 1 * 256; // Might be 6 *.
        const int DarkLipFacePaintColorOffset = 2 * 256;
        const int LightLipFacePaintColorOffset = 7 * 256;

        Dictionary<string, List<dynamic>> _alternatesByName = new Dictionary<string, List<dynamic>>();
        Dictionary<int, List<dynamic>> _alternatesByAppearance = new Dictionary<int, List<dynamic>>();
        Dictionary<int, int> _zoneByNpcId = new Dictionary<int, int>();
        Dictionary<int, Saint.Level> _levelByNpcObjectKey = new Dictionary<int, Saint.Level>();
        Dictionary<int, Libra.ENpcResident> _libraNpcIndex;
        Dictionary<int, string> _photoFileNameById = new Dictionary<int, string>();

        public override string Name => "NPCs";

        public override void Start()
        {
            var lines = Utils.Csv(Path.Combine(Config.SupplementalPath, "ENpcPhotoRef.csv"));
            foreach (var line in lines.Skip(1))
            {
                var eNpcID = line[0];
                var photoFileName = line[1];
                _photoFileNameById[int.Parse(eNpcID)] = photoFileName;
            }

            _sCharaMakeCustomize = _builder.Sheet("CharaMakeCustomize");
            _sCharaMakeType = _builder.Sheet("CharaMakeType");
            _colorMap = new SaintCoinach.Graphics.ColorMap(_builder.Realm.GameData.PackCollection.GetFile("chara/xls/charamake/human.cmp"));
            _alternatesByAppearance.Add(0, new List<dynamic>());

            IndexLevels();
            BuildNpcs();
            BuildSupplementalData();
            LinkAlternates();
        }

        void IndexLevels()
        {
            foreach (var sLevel in _builder.Sheet<Saint.Level>())
            {
                // NPC
                if (sLevel.Type == 8 && sLevel.Object != null && !_levelByNpcObjectKey.ContainsKey(sLevel.Object.Key))
                    _levelByNpcObjectKey[sLevel.Object.Key] = sLevel;
            }

            // Supplemental Libra places.
            _libraNpcIndex = _builder.Libra.Table<Libra.ENpcResident>().ToDictionary(e => e.Key);

            foreach (var lPlaceName in _builder.Libra.Table<Libra.ENpcResident_PlaceName>())
                _zoneByNpcId[lPlaceName.ENpcResident_Key] = lPlaceName.PlaceName_Key;

            // Some NPC locations are wrong or ambiguous - this helps.
            _zoneByNpcId[1004418] = 698;
            _zoneByNpcId[1006747] = 698;
            _zoneByNpcId[1002299] = 698;
            _zoneByNpcId[1002281] = 698;
            _zoneByNpcId[1001766] = 698;
            _zoneByNpcId[1001945] = 698;
            _zoneByNpcId[1001821] = 698;
        }

        void BuildNpcs()
        {
            var sENpcs = _builder.Realm.GameData.ENpcs
                .Where(n => !Hacks.IsNpcSkipped(n))
                .ToArray();

            foreach (var sNpc in sENpcs)
            {
                dynamic npc = new JObject();
                npc.id = sNpc.Key;
                if (sNpc.Resident == null || !string.IsNullOrWhiteSpace(sNpc.Resident?.Singular))
                {
                    _builder.Localize.Column((JObject)npc, sNpc.Resident, "Singular", "name", Utils.CapitalizeWords);

                    string name = npc.en.name;
                    npc.patch = PatchDatabase.Get("npc", sNpc.Key);

                    // Set base information.
                    if (!_alternatesByName.TryGetValue(name, out var alts))
                    {
                        alts = new List<dynamic>();
                        _alternatesByName[name] = alts;
                    }
                    alts.Add(npc);
                }
                else
                {
                    foreach (var langTuple in _builder.Localize.Langs)
                    {
                        var code = langTuple.Item1;
                        var lang = langTuple.Item2;
                        npc[code] = new JObject();
                        switch (lang)
                        {
                            case SaintCoinach.Ex.Language.English:
                                npc[code].name = $"Nameless #{sNpc.Key}";
                                break;
                            case SaintCoinach.Ex.Language.Japanese:
                                npc[code].name = $"無名 #{sNpc.Key}";
                                break;
                            case SaintCoinach.Ex.Language.French:
                                npc[code].name = $"Sans nom #{sNpc.Key}";
                                break;
                            case SaintCoinach.Ex.Language.German:
                                npc[code].name = $"Namenlos #{sNpc.Key}";
                                break;
                            default:
                                npc[code].name = $"Unnamed #{sNpc.Key}";
                                break;
                        }

                    }
                    npc.patch = "1.9";
                }

                var title = sNpc.Title.ToString();
                if (!string.IsNullOrEmpty(title))
                    npc.title = title;

                // Map and coordinates.
                if (_levelByNpcObjectKey.TryGetValue(sNpc.Key, out var level))
                {
                    npc.coords = _builder.GetCoords(level);

                    if (level.Map.PlaceName.Key > 0)
                    {
                        npc.zoneid = level.Map.PlaceName.Key;
                        _builder.Db.AddLocationReference(level.Map.PlaceName.Key);
                    }

                    UpdateArea(_builder, npc, level.Map, level.MapX, level.MapY);
                }
                else
                {
                    if (_libraNpcIndex.TryGetValue(sNpc.Key, out var lNpc))
                    {
                        dynamic lData = JsonConvert.DeserializeObject((string)lNpc.data);
                        var lZone = Utils.GetPair(lData.coordinate);
                        npc.coords = Utils.GetFirst(lZone.Value);
                    }

                    if (_zoneByNpcId.TryGetValue(sNpc.Key, out var zoneid))
                    {
                        npc.zoneid = zoneid;
                        _builder.Db.AddLocationReference(zoneid);
                    }
                }

                // Other work.
                BuildAppearanceData(npc, sNpc);
                if (_photoFileNameById.TryGetValue(sNpc.Key, out var photoFileName))
                {
                    npc.photo = photoFileName;
                }

                _builder.Db.Npcs.Add(npc);
                _builder.Db.NpcsById[sNpc.Key] = npc;
            }
        }

        public static void UpdateArea(DatabaseBuilder builder, dynamic npc, Saint.Map sMap, double mapX, double mapY)
        {
            var marker = MapMarker.FindClosest(builder, sMap, mapX, mapY);
            if (marker != null)
            {
                npc.areaid = marker.PlaceName.Key;
                builder.Db.AddLocationReference(marker.PlaceName.Key);
            }
        }

        void BuildSupplementalData()
        {
            var lines = Utils.Tsv(System.IO.Path.Combine(Config.SupplementalPath, "FFXIV Data - NPCs.tsv"));
            foreach (var line in lines.Skip(1))
            {
                var id = int.Parse(line[1]);
                var isEventNpc = int.Parse(line[2]) == 1;

                var npc = _builder.Db.NpcsById[id];

                if (isEventNpc)
                    npc["event"] = 1;
            }
        }

        void BuildAppearanceData(dynamic npc, Saint.ENpc sNpc)
        {
            var race = (Saint.Race)sNpc.Base["Race"];
            if (race == null || race.Key == 0)
                return; // Unique or beast NPCs, can't do appearance now.

            dynamic appearance = new JObject();
            npc.appearance = appearance;

            var gender = (byte)sNpc.Base["Gender"];
            var isMale = gender == 0;
            appearance.gender = isMale ? "Male" : "Female";

            appearance.race = isMale ? race.Masculine.ToString() : race.Feminine.ToString();

            var tribe = (Saint.Tribe)sNpc.Base["Tribe"];
            appearance.tribe = isMale ? tribe.Masculine.ToString() : tribe.Feminine.ToString();

            appearance.height = sNpc.Base["Height"];

            var bodyType = (byte)sNpc.Base["BodyType"];
            if (bodyType != 1)
                appearance.bodyType = GetBodyType(bodyType);

            // Faces
            var baseFace = (byte)sNpc.Base["Face"];
            var face = baseFace % 100; // Value matches the asset number, % 100 approximate face # nicely.
            appearance.face = face;

            var isValidFace = face < 8;
            var isCustomFace = baseFace > 7;
            if (isCustomFace)
                appearance.customFace = 1;

            appearance.jaw = 1 + (byte)sNpc.Base["Jaw"];

            appearance.eyebrows = 1 + (byte)sNpc.Base["Eyebrows"];

            appearance.nose = 1 + (byte)sNpc.Base["Nose"];

            appearance.skinColor = FormatColorCoordinates((byte)sNpc.Base["SkinColor"]);
            appearance.skinColorCode = FormatColor((byte)sNpc.Base["SkinColor"], GetSkinColorMapIndex(tribe.Key, isMale));

            // Bust & Muscles - flex fields.
            if (race.Key == 5 || race.Key == 1)
            {
                // Roegadyn & Hyur
                appearance.muscle = (byte)sNpc.Base["BustOrTone1"];
                if (!isMale)
                    appearance.bust = (byte)sNpc.Base["ExtraFeature2OrBust"];
            }
            else if (!isMale)
            {
                // Other female bust sizes
                appearance.bust = (byte)sNpc.Base["ExtraFeature2OrBust"];
            }

            // Hair & Highlights
            var hairstyle = (byte)sNpc.Base["HairStyle"];
            var hairstyleIcon = CustomizeIcon(GetHairstyleCustomizeIndex(tribe.Key, isMale), 100, hairstyle, npc, appearance);
            if (hairstyleIcon > 0)
            {
                appearance.hairStyle = hairstyleIcon;
            }
             
            appearance.hairColor = FormatColorCoordinates((byte)sNpc.Base["HairColor"]);
            appearance.hairColorCode = FormatColor((byte)sNpc.Base["HairColor"], GetHairColorMapIndex(tribe.Key, isMale));

            var highlights = Unpack2((byte)sNpc.Base["HairHighlight"]);
            if (highlights.Item1 == 1)
            {
                appearance.highlightColor = FormatColorCoordinates((byte)sNpc.Base["HairHighlightColor"]);
                appearance.highlightColorCode = FormatColor((byte)sNpc.Base["HairHighlightColor"], HairHighlightColorOffset);
            }

            // Eyes & Heterochromia
            var eyeShape = Unpack2((byte)sNpc.Base["EyeShape"]);
            appearance.eyeSize = eyeShape.Item1 == 1 ? "Small" : "Large";
            appearance.eyeShape = 1 + eyeShape.Item2;

            var eyeColor = (byte)sNpc.Base["EyeColor"];
            appearance.eyeColor = FormatColorCoordinates(eyeColor);
            appearance.eyeColorCode = FormatColor(eyeColor, EyeColorOffset);

            var heterochromia = (byte)sNpc.Base["EyeHeterochromia"];
            if (heterochromia != eyeColor)
            {
                appearance.heterochromia = FormatColorCoordinates(heterochromia);
                appearance.heterochromiaCode = FormatColor(heterochromia, EyeColorOffset);
            }

            // Mouth & Lips
            var mouth = Unpack2((byte)sNpc.Base["Mouth"]);
            appearance.mouth = 1 + mouth.Item2;

            if (mouth.Item1 == 1)
            {
                var lipColor = Unpack2((byte)sNpc.Base["LipColor"]);
                appearance.lipShade = lipColor.Item1 == 1 ? "Light" : "Dark";
                appearance.lipColor = FormatColorCoordinates(lipColor.Item2);
                appearance.lipColorCode = FormatColor(lipColor.Item2, lipColor.Item1 == 1 ? LightLipFacePaintColorOffset : DarkLipFacePaintColorOffset);
            }

            // Extra Features
            var extraFeatureName = ExtraFeatureName(race.Key);
            if (extraFeatureName != null)
            {
                appearance.extraFeatureName = extraFeatureName;

                appearance.extraFeatureShape = (byte)sNpc.Base["ExtraFeature1"];
                appearance.extraFeatureSize = (byte)sNpc.Base["BustOrTone1"];
            }

            // Facepaint
            var facepaint = Unpack2((byte)sNpc.Base["FacePaint"]);
            var facepaintIcon = CustomizeIcon(GetFacePaintCustomizeIndex(tribe.Key, isMale), 50, facepaint.Item2, npc, appearance);
            if (facepaintIcon > 0)
            {
                appearance.facepaint = facepaintIcon;

                if (facepaint.Item1 == 1)
                    appearance.facepaintReverse = 1;

                var facepaintColor = Unpack2((byte)sNpc.Base["FacePaintColor"]);
                appearance.facepaintShade = facepaintColor.Item1 == 1 ? "Light" : "Dark";
                appearance.facepaintColor = FormatColorCoordinates(facepaintColor.Item2);
                appearance.facepaintColorCode = FormatColor(facepaintColor.Item2, facepaintColor.Item1 == 1 ? LightLipFacePaintColorOffset : DarkLipFacePaintColorOffset);
            }

            // Facial Features
            var facialfeature = (byte)sNpc.Base["FacialFeature"];
            if (facialfeature != 0 && isValidFace)
            {
                var type = CharaMakeTypeRow(tribe.Key, gender);

                appearance.facialfeatures = new JArray();

                // There are only 7 groups of facial features at the moment.
                var facialfeatures = new System.Collections.BitArray(new byte[] { facialfeature });
                for (var i = 0; i < 7; i++)
                {
                    if (!facialfeatures[i])
                        continue;

                    var iconIndex = face;
                    // If it's not hrotgar, shift to -1
                    if (race.Key != 7)
                    {
                        iconIndex --;
                    }

                    var column = "FacialFeatureOption[" + iconIndex + "][" + i + "]";

                    var icon = (ImageFile)type[column];
                    if (icon == null)
                        continue; // Nothing to show.
                    appearance.facialfeatures.Add(IconDatabase.EnsureEntry("customize", icon));
                }

                appearance.facialfeatureColor = FormatColorCoordinates((byte)sNpc.Base["FacialFeatureColor"]);
                appearance.facialfeatureColorCode = FormatColor((byte)sNpc.Base["FacialFeatureColor"], 0);
            }

            // todo: CharaMakeType ExtraFeatureData for faces, extra feature icons.

            string appearanceJsonString = appearance.ToString();
            int appearanceHashCode = Utils.GetDeterministicHashCode(appearanceJsonString);
            appearance.hash = appearanceHashCode;
            if (!_alternatesByAppearance.TryGetValue(appearanceHashCode, out var alts))
            {
                alts = new List<dynamic>();
                _alternatesByAppearance[appearanceHashCode] = alts;
            }
            alts.Add(npc);

        }

        void LinkAlternates()
        {
            foreach (var npc in _builder.Db.Npcs)
            {
                string name = npc.en.name ?? "";

                if(_alternatesByName.TryGetValue(name, out var alts)){
                    var otherAlts = alts.Where(a => a != npc).OrderBy(a => (int)a.id).ToArray();

                    if (otherAlts.Length > 0)
                    {
                        npc.alts = new JArray();

                        foreach (var alt in otherAlts)
                        {
                            int altId = alt.id;
                            npc.alts.Add(altId);
                            _builder.Db.AddReference(npc, "npc", altId, false);
                        }
                    }
                }

                int appHash = npc.appearance?.hash ?? 0;
                if (_alternatesByAppearance.TryGetValue(appHash, out var appAlts)){
                    var otherAppAlts = appAlts.Where(a => a != npc).OrderBy(a => (int)a.id).ToArray();
                    if (otherAppAlts.Length > 0)
                    {
                        npc.appalts = new JArray();

                        foreach (var alt in otherAppAlts)
                        {
                            int altId = alt.id;
                            npc.appalts.Add(altId);
                            _builder.Db.AddReference(npc, "npc", altId, false);
                        }
                    }
                }
            }
        }

        #region Appearance Utilities
        Saint.IXivRow CharaMakeTypeRow(int tribeKey, byte gender)
        {
            foreach (var row in _sCharaMakeType)
            {
                var tribe = (Saint.Tribe)row["Tribe"];
                if (tribe.Key == tribeKey && (sbyte)row["Gender"] == gender)
                    return row;
            }

            throw new NotImplementedException();
        }

        static string ExtraFeatureName(int raceKey)
        {
            switch (raceKey)
            {
                case 1: // Hyur
                case 5: // Roegadyn
                    return null;

                case 2: // Elezen
                case 3: // Lalafell
                case 8: // Viera
                    return "Ears";

                case 4: // Miqo'te
                case 6: // Au Ra
                case 7: // Hrothgar
                    return "Tail";
            }

            throw new NotImplementedException();
        }

        static Tuple<byte, byte> Unpack2(byte value)
        {
            if (value >= 128)
                return Tuple.Create((byte)1, (byte)(value - 128));
            else
                return Tuple.Create((byte)0, value);
        }

        int CustomizeIcon(int startIndex, int length, byte dataKey, dynamic npc, dynamic appearence)
        {
            if (dataKey == 0)
                return 0; // Custom or not specified.

            for (var i = 1; i < length; i++)
            {
                var row = _sCharaMakeCustomize[startIndex + i];
                if ((byte)row[0] == dataKey)
                {
                    var icon = (ImageFile)row["Icon"];
                    var hintitem = row["HintItem"] as Saint.Item;
                    if (hintitem != null && hintitem.Key != 0)
                    {
                        appearence.hairstyleItem = hintitem.Key;
                        _builder.Db.AddReference(npc, "item", hintitem.Key, false);
                    }
                    return IconDatabase.EnsureEntry("customize", icon);
                }
            }

            //System.Diagnostics.Debug.WriteLine("{0} has custom hair {1}", (string)npc.name, hairstyle);
            return 0; // Not found - custom.
        }

        static int GetSkinColorMapIndex(int tribeKey, bool isMale)
        {
            var genderValue = isMale ? 0 : 1;
            var listIndex = (tribeKey * 2 + genderValue) * 5 + 3;
            return listIndex * 256;
        }

        static int GetHairColorMapIndex(int tribeKey, bool isMale)
        {
            var genderValue = isMale ? 0 : 1;
            var listIndex = (tribeKey * 2 + genderValue) * 5 + 4;
            return listIndex * 256;
        }

        static int GetHairstyleCustomizeIndex(int tribeKey, bool isMale)
        {
            switch (tribeKey)
            {
                case 1: // Midlander
                    return isMale ? 0 : 100;
                case 2: // Highlander
                    return isMale ? 200 : 300;
                case 3: // Wildwood
                case 4: // Duskwight
                    return isMale ? 400 : 500;
                case 5: // Plainsfolks
                case 6: // Dunesfolk
                    return isMale ? 600 : 700;
                case 7: // Seeker of the Sun
                case 8: // Keeper of the Moon
                    return isMale ? 800 : 900;
                case 9: // Sea Wolf
                case 10: // Hellsguard
                    return isMale ? 1000 : 1100;
                case 11: // Raen
                case 12: // Xaela
                    return isMale ? 1200 : 1300;

                // No alternate genders for Hrothgar, Viera.
                // For Hrothgar, these might be faces too?
                case 13: // Helions 
                case 14: // The Lost
                    return isMale ? 1400 : 1500;
                case 15: // Rava
                case 16: // Veena
                    return isMale ? 1600 : 1700;
            }

            throw new NotImplementedException();
        }

        static int GetFacePaintCustomizeIndex(int tribeKey, bool isMale)
        {
            const int baseRowKey = 2000; // EW - [update by patch required]

            switch (tribeKey)
            {
                case 1: // Midlander
                case 2: // Highlander
                case 3: // Wildwood
                case 4: // Duskwight
                case 5: // Plainsfolks
                case 6: // Dunesfolk
                case 7: // Seeker of the Sun
                case 8: // Keeper of the Moon
                case 9: // Sea Wolf
                case 10: // Hellsguard
                case 11: // Raen
                case 12: // Xaela
                case 13: // Helions
                case 14: // The Lost
                case 15: // Rava
                case 16: // Veena
                    var tribeOffset = baseRowKey + ((tribeKey - 1) * 100);
                    return isMale ? tribeOffset : tribeOffset + 50;
                
                /*
                case 13: // Helions
                    return 3200;
                case 14: // The Lost
                    return 3300;
                case 15: // Rava
                    return 3400;
                case 16: // Veena
                    return 3500;
                */
            }

            throw new NotImplementedException();
        }

        static string GetBodyType(byte type)
        {
            switch (type)
            {
                case 3: return "Elderly";
                case 4: return "Child";
                default:
                    throw new ArgumentException("Invalid body type " + type, "type");
            }
        }

        static string FormatColorCoordinates(byte color)
        {
            var row = 1 + (color / 8);
            var column = 1 + (color % 8);
            return $"{row}, {column}";
        }

        string FormatColor(byte colorIndex, int offset)
        {
            var c = _colorMap.Colors[offset + colorIndex];
            return $"#{c.R.ToString("X2")}{c.G.ToString("X2")}{c.B.ToString("X2")}";
        }
        #endregion
    }
}
