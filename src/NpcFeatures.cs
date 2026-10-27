using System;
using System.IO;
using UnityEngine;

namespace BaldiTogether
{
    // One dispatcher for the per-NPC ability sync files (BeansSync, CraftersSync, PompSync, TheTestSync, ReflexSync, ChalklesSync, ItemsSync, HallMonitorSync).
    // Each feature owns its file, its two Op codes and its own length-prefixed section of the World snapshot,
    // so features can change their formats without affecting each other.
    public sealed partial class TogetherRuntime
    {
        // One feature's exception must not skip the other features, ResetWorld or the rest of Update.
        float nextFeatureErrorLog;
        void Guard(string name, Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                if (Time.realtimeSinceStartup < nextFeatureErrorLog) return;
                nextFeatureErrorLog = Time.realtimeSinceStartup + 5;
                Log(name + " failed: " + ex);
            }
        }

        void ResetNpcFeatures()
        {
            Guard("ResetBeans", ResetBeans);
            Guard("ResetCrafters", ResetCrafters);
            Guard("ResetPomp", ResetPomp);
            Guard("ResetEvents", ResetEvents);
            Guard("ResetTheTest", ResetTheTest);
            Guard("ResetReflex", ResetReflex);
            Guard("ResetChalkles", ResetChalkles);
            Guard("ResetItems", ResetItems);
            Guard("ResetHallMonitor", ResetHallMonitor);
        }

        void UpdateNpcFeatures()
        {
            Guard("UpdateBeans", UpdateBeans);
            Guard("UpdateCrafters", UpdateCrafters);
            Guard("UpdatePomp", UpdatePomp);
            Guard("UpdateTheTest", UpdateTheTest);
            Guard("UpdateReflex", UpdateReflex);
            Guard("UpdateChalkles", UpdateChalkles);
            Guard("UpdateItems", UpdateItems);
            Guard("UpdateHallMonitor", UpdateHallMonitor);
            Guard("UpdateEventLab", UpdateEventLab);
        }

        void UpdateEncounterLabGuarded() { Guard("UpdateEncounterLab", UpdateEncounterLab); }

        bool HandleNpcFeatureClient(int id, Peer sender, Op op, BinaryReader r)
        {
            return HandleBeansClient(id, sender, op, r) || HandleCraftersClient(id, sender, op, r) || HandlePompClient(id, sender, op, r) || HandleTheTestClient(id, sender, op, r) || HandleReflexClient(id, sender, op, r) || HandleChalklesClient(id, sender, op, r) || HandleItemsClient(id, sender, op, r);
        }

        bool HandleNpcFeatureHost(Op op, BinaryReader r)
        {
            return HandleBeansHost(op, r) || HandleCraftersHost(op, r) || HandlePompHost(op, r) || HandleTheTestHost(op, r) || HandleReflexHost(op, r) || HandleChalklesHost(op, r) || HandleItemsHost(op, r) || HandleEventHost(op, r);
        }

        static void WriteFeatureSection(BinaryWriter w, Action<BinaryWriter> write)
        {
            using (var memory = new MemoryStream())
            using (var section = new BinaryWriter(memory))
            {
                write(section); section.Flush();
                if (memory.Length > ushort.MaxValue) throw new IOException("NPC feature section too large.");
                w.Write((ushort)memory.Length); w.Write(memory.GetBuffer(), 0, (int)memory.Length);
            }
        }

        static void ReadFeatureSection(BinaryReader r, Action<BinaryReader> read)
        {
            int length = r.ReadUInt16();
            if (length > r.BaseStream.Length - r.BaseStream.Position) throw new IOException("Truncated NPC feature section.");
            byte[] data = r.ReadBytes(length);
            using (var memory = new MemoryStream(data, false))
            using (var section = new BinaryReader(memory))
            {
                read(section);
                if (memory.Position != memory.Length) throw new IOException("Unread NPC feature data.");
            }
        }

        void WriteNpcFeatureWorld(BinaryWriter w)
        {
            WriteFeatureSection(w, WriteBeansWorld);
            WriteFeatureSection(w, WriteCraftersWorld);
            WriteFeatureSection(w, WritePompWorld);
            WriteFeatureSection(w, WriteTheTestWorld);
            WriteFeatureSection(w, WriteReflexWorld);
            WriteFeatureSection(w, WriteChalklesWorld);
            WriteFeatureSection(w, WriteItemsWorld);
            WriteFeatureSection(w, WriteHallMonitorWorld);
        }

        void ReadNpcFeatureWorld(BinaryReader r, bool apply)
        {
            ReadFeatureSection(r, s => ReadBeansWorld(s, apply));
            ReadFeatureSection(r, s => ReadCraftersWorld(s, apply));
            ReadFeatureSection(r, s => ReadPompWorld(s, apply));
            ReadFeatureSection(r, s => ReadTheTestWorld(s, apply));
            ReadFeatureSection(r, s => ReadReflexWorld(s, apply));
            ReadFeatureSection(r, s => ReadChalklesWorld(s, apply));
            ReadFeatureSection(r, s => ReadItemsWorld(s, apply));
            ReadFeatureSection(r, s => ReadHallMonitorWorld(s, apply));
        }

        string NpcFeatureDiagnostic()
        {
            return BeansDiagnostic() + CraftersDiagnostic() + PompDiagnostic() + TheTestDiagnostic() + ReflexDiagnostic() + ChalklesDiagnostic() + ItemsDiagnostic() + HallMonitorDiagnostic() + HostOnlyStartDiagnostic() + EncounterDiagnostic() + EventDiagnostic();
        }

        void UpdateNpcFeatureLabs()
        {
            UpdateBeansLab();
            UpdateCraftersLab();
            UpdatePompLab();
            UpdateTheTestLab();
            UpdateReflexLab();
            UpdateChalklesLab();
            UpdateItemsLab();
            UpdateHallMonitorLab();
        }
    }
}
