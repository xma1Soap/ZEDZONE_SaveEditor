using System;
using System.Collections.Generic;

namespace ZedZoneSaveEditor
{
    /// <summary>
    /// 游戏里 CharacterData 的字段号（来自游戏 1.0.12 反编译的 ProtoMember 编号）。
    /// 单机存档和联机角色档里存的是同一种人物数据。
    /// </summary>
    internal static class F
    {
        // CharacterData
        public const int Name = 11, Inventory = 14, Camp = 16, Status = 17, Equipment = 18, Property = 19;
        public const int CharacterId = 34, PerkList = 51, SkillNodes = 52, Buffs = 60;
        public const int Gender = 100, AttrPoint = 105, SkillPoint = 106, PerkPoint = 107;
        public const int BodyParts = 148, DeadFlag = 149, Hardcore = 208;

        // CharacterPropertyData
        public const int Strength = 30, Agility = 31, Constitution = 36;

        // CharacterStatusData
        public const int Hp = 20, Energy = 21, Fatigue = 22, Hunger = 23, Thirst = 24;
        public const int BloodVolume = 40, BodyTemperature = 41, Radiation = 42;

        // CharacterBodyPartsData -> CharacterBodyPartData[]
        public const int BodyPartArray = 149;
        public const int BpId = 150, BpTextId = 151, BpHp = 152, BpBandage = 153, BpBleedStop = 154, BpDefaultHp = 155, BpBandageExpire = 156;

        // BuffData
        public const int BuffId = 61, BuffEnd = 62;
    }

    /// <summary>游戏里算上限的公式（从游戏代码里抠出来的常数）。</summary>
    internal static class Rules
    {
        public const int MaxLegalAttr = 70;          // 单项超过 70 会被判"改过"（PlayfabAvatarGate.maxLegalAttrPoint）
        public const int MaxLegalSkillNodes = 220;
        public const float MaxBlood = 5000f;         // CharacterStatusData.maxBloodVolume
        public const float DeadBlood = 1900f;
        public const float NormalTemperature = 36.5f;
        public const float MinTemperature = 35.2f, MaxTemperature = 41.8f;
        public const float MaxFatigue = 100f;

        public static float MaxHunger(int con) => con * 2f + 80f;      // CharacterData.SetMaxHunger
        public static float MaxThirst(int con) => con * 2f + 80f;      // CharacterData.SetMaxThirst
        public static float MaxEnergyBase(int con) => con * 6f + 60f;  // SetMaxEnergy，另加技能树里的体力加成
        public static float MaxHpBase(int con) => con * 40f + 200f;    // SetMaxHP，另乘天赋、技能、难度系数
        public static float BodyPartMax(float defaultHp, int con) => defaultHp + con * 4f; // RefreshBodyPartsStatus

        public static readonly string[] BodyPartNames = { "头部", "躯干", "左臂", "右臂", "左腿", "右腿" };
    }

    internal sealed class BodyPart
    {
        public PMsg Msg;
        public int Id => Msg.GetInt(F.BpId);
        public string Name => Id >= 0 && Id < Rules.BodyPartNames.Length ? Rules.BodyPartNames[Id] : "部位" + Id;
        public float Hp { get => Msg.GetFloat(F.BpHp); set => Msg.SetFloat(F.BpHp, value); }
        public float DefaultHp => Msg.GetFloat(F.BpDefaultHp);
        public float BleedStop => Msg.GetFloat(F.BpBleedStop);
        public bool HasBandage => Msg.Has(F.BpBandage);

        public float Max(int con) => Rules.BodyPartMax(DefaultHp, con);

        /// <summary>照游戏自己的 HealAllInjuries：回满、止血、去掉已用的绷带。</summary>
        public void Heal(int con)
        {
            Hp = Max(con);
            StopBleeding();
        }

        public void StopBleeding()
        {
            if (Msg.GetFloat(F.BpBleedStop) != 0f) Msg.SetFloat(F.BpBleedStop, 0f);
            if (Msg.GetFloat(F.BpBandageExpire) != 0f) Msg.SetFloat(F.BpBandageExpire, 0f);
            Msg.RemoveAll(F.BpBandage);
        }
    }

    /// <summary>对一份人物数据（CharacterData）的读写。</summary>
    internal sealed class CharacterView
    {
        public readonly PMsg Msg;
        public CharacterView(PMsg msg) { Msg = msg ?? throw new ArgumentNullException(nameof(msg)); }

        public string Name { get => Msg.GetString(F.Name) ?? ""; set => Msg.SetString(F.Name, value); }
        public string Id => Msg.GetString(F.CharacterId) ?? "";
        public int Gender => Msg.GetInt(F.Gender);
        public bool Hardcore => Msg.GetBool(F.Hardcore);
        public bool DeadFlag => Msg.GetBool(F.DeadFlag);

        public int AttrPoint { get => Msg.GetInt(F.AttrPoint); set => Msg.SetInt(F.AttrPoint, value); }
        public int SkillPoint { get => Msg.GetInt(F.SkillPoint); set => Msg.SetInt(F.SkillPoint, value); }
        public int PerkPoint { get => Msg.GetInt(F.PerkPoint); set => Msg.SetInt(F.PerkPoint, value); }

        // 读的时候不新建子结构，免得什么都没改也被当成"改过"
        private PMsg PropR => Msg.GetMsg(F.Property);
        private PMsg PropW => Msg.GetOrAddMsg(F.Property);
        private PMsg StatusR => Msg.GetMsg(F.Status);
        private PMsg StatusW => Msg.GetOrAddMsg(F.Status);

        public int Strength { get => PropR?.GetInt(F.Strength) ?? 0; set => PropW.SetInt(F.Strength, value); }
        public int Agility { get => PropR?.GetInt(F.Agility) ?? 0; set => PropW.SetInt(F.Agility, value); }
        public int Constitution { get => PropR?.GetInt(F.Constitution) ?? 0; set => PropW.SetInt(F.Constitution, value); }

        public float GetStatus(int field) => StatusR?.GetFloat(field) ?? 0f;
        public void SetStatus(int field, float v) => StatusW.SetFloat(field, v);

        public List<BodyPart> BodyParts
        {
            get
            {
                var r = new List<BodyPart>();
                var bp = Msg.GetMsg(F.BodyParts);
                if (bp == null) return r;
                foreach (var m in bp.GetMsgs(F.BodyPartArray)) r.Add(new BodyPart { Msg = m });
                return r;
            }
        }

        public List<int> BuffIds
        {
            get
            {
                var r = new List<int>();
                foreach (var m in Msg.GetMsgs(F.Buffs)) r.Add(m.GetInt(F.BuffId));
                return r;
            }
        }

        public void ClearBuffs() => Msg.RemoveAll(F.Buffs);

        public int PerkCount => Msg.GetLongList(F.PerkList).Count;
        public int SkillNodeCount => Msg.All(F.SkillNodes).Count;
    }
}
