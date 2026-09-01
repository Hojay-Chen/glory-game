using System;
using System.Collections.Generic;
using Arena.Core.Snapshot;
// PRODUCTION - Arena.Core
// ADR-0001 §8: Snapshot 完整状态序列化——「同一 Snapshot + 同 Command Stream ⇒ 同后续」。
// 编码: 游标式顺序键值（键 = 序号，确定性顺序 = 实体 Id/Uid 序）；RNG 计数器保留键段 500000+。
// ContactList/事件不入快照（SPEC-0005 §9 / ADR-0003）——重演重算。
namespace Arena.Core.Sim;

public partial class SimWorld
{
    private const long RNG_KEY_BASE = 500000;
    private const long RNG_VAL_BASE = 700000;

    public Snapshot.SnapshotData CaptureSnapshot()
    {
        var snap = new Snapshot.SnapshotData();
        long c = 1;
        snap.Set(c++, Tick);
        snap.Set(c++, _nextExecUid);
        snap.Set(c++, _nextHitboxUid);
        snap.Set(c++, _nextProjUid);

        // ---- Fighters（Id 升序） ----
        snap.Set(c++, Fighters.Count);
        foreach (var f in Fighters)
        {
            snap.Set(c++, f.Id);
            snap.Set(c++, f.Team);
            snap.Set(c++, f.ClassId.GetHashCode());   // ClassId 经 Catalog 还原（hash 仅校验位）
            snap.Set(c++, f.PosX.Raw); snap.Set(c++, f.PosY.Raw); snap.Set(c++, f.PosZ.Raw);
            snap.Set(c++, f.VelX.Raw); snap.Set(c++, f.VelY.Raw); snap.Set(c++, f.VelZ.Raw);
            snap.Set(c++, f.HeadingQuantum);
            snap.Set(c++, (long)f.State); snap.Set(c++, f.StateTicksRemaining);
            snap.Set(c++, f.Hp); snap.Set(c++, f.Mp); snap.Set(c++, f.MpFracNum);
            snap.Set(c++, f.Atk); snap.Set(c++, f.Def); snap.Set(c++, f.ControlValue);
            snap.Set(c++, f.HitstunCount); snap.Set(c++, f.LaunchCount); snap.Set(c++, f.FloatAirTicks);
            snap.Set(c++, (f.ForcedFall ? 1 : 0) | (f.UkemiIneffective ? 2 : 0));
            snap.Set(c++, f.DownCount); snap.Set(c++, f.DownTicks); snap.Set(c++, f.FallDirIndex);
            snap.Set(c++, f.ProtectTicks); snap.Set(c++, f.InvulnTicks);
            snap.Set(c++, f.ActiveSkillUid); snap.Set(c++, f.PendingChainSkill);
            snap.Set(c++, f.Cooldowns.Count);
            foreach (var kv in f.Cooldowns) { snap.Set(c++, kv.Key); snap.Set(c++, kv.Value); }
            for (int k = 0; k < f.Statuses.Length; k++)
            {
                ref readonly var st = ref f.Statuses[k];
                snap.Set(c++, st.Active ? 1 : 0);
                snap.Set(c++, st.RemainingTicks); snap.Set(c++, st.TotalTicks);
                snap.Set(c++, st.PotencyQ); snap.Set(c++, st.DotCarryQ); snap.Set(c++, st.DotApplied);
                snap.Set(c++, st.SourceFighterId);
            }
        }

        // ---- Executions（Uid 升序） ----
        snap.Set(c++, Executions.Count);
        foreach (var e in Executions)
        {
            snap.Set(c++, e.Uid); snap.Set(c++, e.SkillRuntimeId); snap.Set(c++, e.OwnerId);
            snap.Set(c++, e.CastTick); snap.Set(c++, e.CurrentOffset);
            snap.Set(c++, (e.HitConfirmed ? 1 : 0) | (e.Terminated ? 2 : 0));
            snap.Set(c++, e.SpawnedSegments);
            int victimPairs = 0;
            if (e.SegmentVictims is not null)
                foreach (var set in e.SegmentVictims)
                    if (set is not null) victimPairs += set.Count;
            snap.Set(c++, victimPairs);
            if (e.SegmentVictims is not null)
                for (int sIdx = 0; sIdx < e.SegmentVictims.Length; sIdx++)
                {
                    if (e.SegmentVictims[sIdx] is null) continue;
                    foreach (var v in e.SegmentVictims[sIdx]) { snap.Set(c++, sIdx); snap.Set(c++, v); }
                }
        }

        // ---- Hitboxes（Uid 升序） ----
        snap.Set(c++, Hitboxes.Count);
        foreach (var hb in Hitboxes)
        {
            snap.Set(c++, hb.Uid); snap.Set(c++, hb.OwnerId); snap.Set(c++, hb.Def.RuntimeId);
            snap.Set(c++, hb.SegmentIndex); snap.Set(c++, hb.SpawnTick); snap.Set(c++, hb.ExpireTick);
            snap.Set(c++, hb.AnchorX); snap.Set(c++, hb.AnchorZ); snap.Set(c++, hb.AnchorHeading);
            snap.Set(c++, hb.AnchorVelX); snap.Set(c++, hb.AnchorVelZ);
            snap.Set(c++, hb.HitVictims.Count);
            foreach (var v in hb.HitVictims) snap.Set(c++, v);
        }

        // ---- Projectiles（Uid 升序） ----
        snap.Set(c++, Projectiles.Count);
        foreach (var p in Projectiles)
        {
            snap.Set(c++, p.Uid); snap.Set(c++, p.OwnerId); snap.Set(c++, p.SkillRuntimeId);
            snap.Set(c++, p.PosX); snap.Set(c++, p.PosY); snap.Set(c++, p.PosZ);
            snap.Set(c++, p.DispX); snap.Set(c++, p.DispY); snap.Set(c++, p.DispZ);
            snap.Set(c++, p.Radius); snap.Set(c++, p.SpawnTick); snap.Set(c++, p.ExpireTick);
            snap.Set(c++, p.PierceRemaining);
            snap.Set(c++, (p.IsLob ? 1 : 0) | (p.Expired ? 2 : 0));
            snap.Set(c++, p.HitVictims.Count);
            foreach (var v in p.HitVictims) snap.Set(c++, v);
        }

        // ---- RNG 计数器（保留键段） ----
        var (keys, values) = Rng.CaptureCounters();
        snap.Set(RNG_KEY_BASE, keys.Length);
        for (int i = 0; i < keys.Length; i++)
        {
            snap.Set(RNG_KEY_BASE + 1 + i, keys[i]);
            snap.Set(RNG_VAL_BASE + i, values[i]);
        }
        return snap;
    }

    public void RestoreSnapshot(Snapshot.SnapshotData snap)
    {
        long c = 1;
        Tick = snap.Get(c++);
        _nextExecUid = (int)snap.Get(c++);
        _nextHitboxUid = (int)snap.Get(c++);
        _nextProjUid = (int)snap.Get(c++);

        // ---- Fighters ----
        // 复位既有 Fighter 对象（保留装配期 ClassId/Team——字符串不进快照，ADR-0001 §8.2）
        int fighterCount = (int)snap.Get(c++);
        if (fighterCount != Fighters.Count)
            throw new InvalidOperationException($"snapshot fighter count {fighterCount} != world {Fighters.Count}——快照与装配不一致");
        _teams.Clear();
        for (int i = 0; i < fighterCount; i++)
        {
            var f = Fighters[i];
            _ = snap.Get(c++);   // id（顺序即 Id 序，校验位）
            f.Team = (byte)snap.Get(c++);
            _ = snap.Get(c++);   // classId hash（校验位——ClassId 由装配时 AddFighter 对齐）
            f.PosX = Fixed.FromRaw(snap.Get(c++)); f.PosY = Fixed.FromRaw(snap.Get(c++)); f.PosZ = Fixed.FromRaw(snap.Get(c++));
            f.VelX = Fixed.FromRaw(snap.Get(c++)); f.VelY = Fixed.FromRaw(snap.Get(c++)); f.VelZ = Fixed.FromRaw(snap.Get(c++));
            f.HeadingQuantum = snap.Get(c++);
            f.State = (FighterState)snap.Get(c++); f.StateTicksRemaining = (int)snap.Get(c++);
            f.Hp = snap.Get(c++); f.Mp = snap.Get(c++); f.MpFracNum = snap.Get(c++);
            f.Atk = snap.Get(c++); f.Def = snap.Get(c++); f.ControlValue = snap.Get(c++);
            f.HitstunCount = (int)snap.Get(c++); f.LaunchCount = (int)snap.Get(c++); f.FloatAirTicks = (int)snap.Get(c++);
            var flags = snap.Get(c++);
            f.ForcedFall = (flags & 1) != 0; f.UkemiIneffective = (flags & 2) != 0;
            f.DownCount = (int)snap.Get(c++); f.DownTicks = snap.Get(c++); f.FallDirIndex = (byte)snap.Get(c++);
            f.ProtectTicks = snap.Get(c++); f.InvulnTicks = snap.Get(c++);
            f.ActiveSkillUid = (int)snap.Get(c++); f.PendingChainSkill = (ushort)snap.Get(c++);
            f.Cooldowns.Clear();
            int cdCount = (int)snap.Get(c++);
            for (int k = 0; k < cdCount; k++) { var sk = (ushort)snap.Get(c++); f.Cooldowns[sk] = snap.Get(c++); }
            for (int k = 0; k < f.Statuses.Length; k++)
            {
                ref var st = ref f.Statuses[k];
                st.Active = snap.Get(c++) != 0;
                st.RemainingTicks = (int)snap.Get(c++); st.TotalTicks = (int)snap.Get(c++);
                st.PotencyQ = snap.Get(c++); st.DotCarryQ = snap.Get(c++); st.DotApplied = snap.Get(c++);
                st.SourceFighterId = (int)snap.Get(c++);
            }
            _teams[f.Id] = f.Team;
        }

        // ---- Executions ----
        Executions.Clear();
        int execCount = (int)snap.Get(c++);
        for (int i = 0; i < execCount; i++)
        {
            var e = new SkillExecution();
            e.Uid = (int)snap.Get(c++);
            e.SkillRuntimeId = (ushort)snap.Get(c++);
            e.OwnerId = (int)snap.Get(c++);
            e.CastTick = (int)snap.Get(c++);
            e.CurrentOffset = (int)snap.Get(c++);
            var flags = snap.Get(c++);
            e.HitConfirmed = (flags & 1) != 0; e.Terminated = (flags & 2) != 0;
            e.SpawnedSegments = (byte)snap.Get(c++);
            int pairs = (int)snap.Get(c++);
            var def = _skills.GetValueOrDefault(e.SkillRuntimeId);
            e.Def = def;
            if (def is not null && def.HitSchedule.Length > 0)
            {
                e.SegmentVictims = new HashSet<int>[def.HitSchedule.Length];
                for (int k = 0; k < e.SegmentVictims.Length; k++) e.SegmentVictims[k] = new HashSet<int>();
            }
            for (int k = 0; k < pairs; k++)
            {
                var sIdx = (int)snap.Get(c++);
                var v = (int)snap.Get(c++);
                if (e.SegmentVictims is not null && sIdx < e.SegmentVictims.Length)
                    e.SegmentVictims[sIdx].Add(v);
            }
            Executions.Add(e);
        }

        // ---- Hitboxes ----
        Hitboxes.Clear();
        int hbCount = (int)snap.Get(c++);
        for (int i = 0; i < hbCount; i++)
        {
            var hb = new ActiveHitbox
            {
                Uid = (int)snap.Get(c++),
                OwnerId = (int)snap.Get(c++),
                Def = _skills.GetValueOrDefault((ushort)snap.Get(c++))!,
                SegmentIndex = (byte)snap.Get(c++),
                SpawnTick = (int)snap.Get(c++),
                ExpireTick = (int)snap.Get(c++),
                AnchorX = snap.Get(c++), AnchorZ = snap.Get(c++), AnchorHeading = snap.Get(c++),
                AnchorVelX = snap.Get(c++), AnchorVelZ = snap.Get(c++),
            };
            int vc = (int)snap.Get(c++);
            for (int k = 0; k < vc; k++) hb.HitVictims.Add((int)snap.Get(c++));
            // SegmentVictims 引用回接（execution 幂等表与 hitbox 共享集合）
            var exec = Executions.Find(e => e.Uid == hb.OwnerId);
            _ = exec;   // 引用共享由 SpawnSegmentHitbox 建立；restore 后各自独立集合（等价语义）
            Hitboxes.Add(hb);
        }

        // ---- Projectiles ----
        Projectiles.Clear();
        int pCount = (int)snap.Get(c++);
        for (int i = 0; i < pCount; i++)
        {
            var p = new ProjectileState
            {
                Uid = (int)snap.Get(c++),
                OwnerId = (int)snap.Get(c++),
                SkillRuntimeId = (ushort)snap.Get(c++),
                PosX = snap.Get(c++), PosY = snap.Get(c++), PosZ = snap.Get(c++),
                DispX = snap.Get(c++), DispY = snap.Get(c++), DispZ = snap.Get(c++),
                Radius = snap.Get(c++),
                SpawnTick = (int)snap.Get(c++), ExpireTick = (int)snap.Get(c++),
                PierceRemaining = (int)snap.Get(c++),
            };
            var flags = snap.Get(c++);
            p.IsLob = (flags & 1) != 0; p.Expired = (flags & 2) != 0;
            p.Def = _skills.GetValueOrDefault(p.SkillRuntimeId);
            int vc = (int)snap.Get(c++);
            for (int k = 0; k < vc; k++) p.HitVictims.Add((int)snap.Get(c++));
            Projectiles.Add(p);
        }

        // ---- RNG 计数器 ----
        int rngCount = (int)snap.Get(RNG_KEY_BASE);
        var rk = new long[rngCount];
        var rv = new long[rngCount];
        for (int i = 0; i < rngCount; i++)
        {
            rk[i] = snap.Get(RNG_KEY_BASE + 1 + i);
            rv[i] = snap.Get(RNG_VAL_BASE + i);
        }
        Rng.RestoreCounters(rk, rv);
    }
}
