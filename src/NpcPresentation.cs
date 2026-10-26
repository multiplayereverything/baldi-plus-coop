using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        const int MaxNpcAnimators = 8, MaxNpcLayers = 8, MaxNpcParameters = 32, MaxNpcSprites = 24;
        sealed class NpcPresentationCache
        {
            public Animator[] Animators;
            public SpriteRenderer[] Sprites;
            public bool Replica;
        }
        readonly Dictionary<NPC, NpcPresentationCache> npcPresentation = new Dictionary<NPC, NpcPresentationCache>();
        readonly Dictionary<string, Sprite> npcSpriteAssets = new Dictionary<string, Sprite>();
        int npcPresentationFrames, npcAnimationCorrections;
        static readonly System.Reflection.FieldInfo npcInvisibles = AccessTools.Field(typeof(Entity), "invisibles");
        static readonly System.Reflection.FieldInfo npcInteractionDisables = AccessTools.Field(typeof(Entity), "interactionDisables");
        static readonly System.Reflection.MethodInfo npcUpdateLayer = AccessTools.Method(typeof(Entity), "UpdateLayer");

        static string NpcSpriteKey(Sprite sprite)
        {
            if (sprite == null) return "";
            Rect rect = sprite.rect;
            return sprite.name + ":" + sprite.texture.name + ":" + (int)rect.x + ":" + (int)rect.y + ":" + (int)rect.width + ":" + (int)rect.height;
        }

        void ResetNpcPresentation()
        {
            npcPresentation.Clear();
            npcSpriteAssets.Clear();
            npcPresentationFrames = npcAnimationCorrections = 0;
        }

        NpcPresentationCache PresentationFor(NPC npc)
        {
            NpcPresentationCache result;
            if (!npcPresentation.TryGetValue(npc, out result))
            {
                result = new NpcPresentationCache
                {
                    Animators = npc.GetComponentsInChildren<Animator>(true),
                    Sprites = npc.GetComponentsInChildren<SpriteRenderer>(true)
                };
                if (result.Animators.Length > MaxNpcAnimators || result.Sprites.Length > MaxNpcSprites)
                {
                    TogetherRuntime.Log("NPC " + npc.name + " has more animators/sprites than multiplayer syncs; extra parts stay local.");
                    result.Animators = result.Animators.Take(MaxNpcAnimators).ToArray();
                    result.Sprites = result.Sprites.Take(MaxNpcSprites).ToArray();
                }
                npcPresentation.Add(npc, result);
            }
            return result;
        }

        void WriteNpcPresentation(BinaryWriter writer)
        {
            if (npcs.Length > 128) throw new IOException("NPC presentation limit exceeded.");
            writer.Write((ushort)npcs.Length);
            foreach (NPC npc in npcs)
            {
                writer.Write((int)npc.Character);
                Entity entity = npc.Entity;
                writer.Write(entity == null || entity.Visible);
                writer.Write(entity != null && entity.InteractionDisabled);
                NpcPresentationCache cache = PresentationFor(npc);
                writer.Write((byte)cache.Animators.Length);
                foreach (Animator animator in cache.Animators)
                {
                    bool valid = animator != null && animator.runtimeAnimatorController != null;
                    writer.Write(valid);
                    if (!valid) continue;
                    int layers = animator.layerCount;
                    AnimatorControllerParameter[] parameters = animator.parameters;
                    if (layers > MaxNpcLayers || parameters.Length > MaxNpcParameters)
                        throw new IOException("NPC animator limit exceeded.");
                    writer.Write(animator.speed);
                    writer.Write((byte)layers);
                    for (int layer = 0; layer < layers; layer++)
                    {
                        // The destination state captures transitions initiated by a trigger without
                        // replaying triggers or gameplay animation events on another machine.
                        AnimatorStateInfo state = animator.IsInTransition(layer)
                            ? animator.GetNextAnimatorStateInfo(layer) : animator.GetCurrentAnimatorStateInfo(layer);
                        writer.Write(state.fullPathHash);
                        writer.Write(state.normalizedTime);
                        writer.Write(animator.GetLayerWeight(layer));
                    }
                    int count = 0;
                    foreach (AnimatorControllerParameter parameter in parameters)
                        if (parameter.type != AnimatorControllerParameterType.Trigger) count++;
                    writer.Write((byte)count);
                    foreach (AnimatorControllerParameter parameter in parameters)
                    {
                        if (parameter.type == AnimatorControllerParameterType.Trigger) continue;
                        writer.Write(parameter.nameHash); writer.Write((byte)parameter.type);
                        if (parameter.type == AnimatorControllerParameterType.Float) writer.Write(animator.GetFloat(parameter.nameHash));
                        else if (parameter.type == AnimatorControllerParameterType.Int) writer.Write(animator.GetInteger(parameter.nameHash));
                        else if (parameter.type == AnimatorControllerParameterType.Bool) writer.Write(animator.GetBool(parameter.nameHash));
                        else throw new IOException("Unknown NPC animator parameter type.");
                    }
                }
                writer.Write((byte)cache.Sprites.Length);
                foreach (SpriteRenderer renderer in cache.Sprites)
                {
                    writer.Write(renderer != null && renderer.gameObject.activeSelf);
                    Wire.Text(writer, renderer == null ? "" : NpcSpriteKey(renderer.sprite));
                }
                WriteNpcWind(writer,npc);
            }
        }

        void ReadNpcPresentation(BinaryReader reader, bool apply)
        {
            int count = reader.ReadUInt16();
            if (count > 128) throw new IOException("NPC presentation limit exceeded.");
            if (apply && npcSpriteAssets.Count == 0)
            {
                // Only already-loaded game assets are referenced; no game artwork is distributed.
                foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    string key = NpcSpriteKey(sprite);
                    if (!npcSpriteAssets.ContainsKey(key)) npcSpriteAssets.Add(key, sprite);
                }
            }
            for (int i = 0; i < count; i++)
            {
                int character = reader.ReadInt32();
                bool visible = reader.ReadBoolean(), interactionDisabled = reader.ReadBoolean();
                NPC npc = apply && i < npcs.Length && npcs[i] != null && (int)npcs[i].Character == character ? npcs[i] : null;
                NpcPresentationCache cache = npc == null ? null : PresentationFor(npc);
                if (cache != null)
                {
                    npcPresentationFrames++;
                    if (!cache.Replica)
                    {
                        npc.StopAllCoroutines();
                        foreach (Animator animator in cache.Animators) if (animator != null) animator.fireEvents = false;
                        cache.Replica = true;
                    }
                    Entity entity = npc.Entity;
                    if (entity != null)
                    {
                        // Do not call SetVisible each packet: it increments/decrements a counter.
                        // Only authoritative semantic visibility changes refresh renderers, leaving
                        // distance/chunk culling entirely local between changes.
                        if (entity.Visible != visible)
                        { npcInvisibles.SetValue(entity, visible ? 0 : 1); entity.UpdateVisibility(); }
                        if (entity.InteractionDisabled != interactionDisabled)
                        {
                            npcInteractionDisables.SetValue(entity, interactionDisabled ? 1 : 0);
                            npcUpdateLayer.Invoke(entity, null);
                        }
                    }
                    var bully = npc as Bully;
                    if (bully != null) ApplyBullyReplicaComponents(bully, visible);
                }
                int animatorCount = reader.ReadByte();
                if (animatorCount > MaxNpcAnimators) throw new IOException("NPC animator count exceeded.");
                for (int a = 0; a < animatorCount; a++)
                {
                    if (!reader.ReadBoolean()) continue;
                    Animator animator = cache != null && a < cache.Animators.Length ? cache.Animators[a] : null;
                    if (animator != null && animator.runtimeAnimatorController == null) animator = null;
                    float speed = Wire.Number(reader);
                    if (animator != null) animator.speed = Mathf.Clamp(speed, 0, 20);
                    int layers = reader.ReadByte();
                    if (layers > MaxNpcLayers) throw new IOException("NPC animator layer limit exceeded.");
                    for (int layer = 0; layer < layers; layer++)
                    {
                        int hash = reader.ReadInt32(); float time = Wire.Number(reader), weight = Wire.Number(reader);
                        if (animator == null || layer >= animator.layerCount || !animator.HasState(layer, hash)) continue;
                        AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
                        if (current.fullPathHash != hash || Mathf.Abs(current.normalizedTime - time) > .2f)
                        { animator.Play(hash, layer, Mathf.Max(0, time)); npcAnimationCorrections++; }
                        animator.SetLayerWeight(layer, Mathf.Clamp01(weight));
                    }
                    int parameters = reader.ReadByte();
                    if (parameters > MaxNpcParameters) throw new IOException("NPC animator parameter limit exceeded.");
                    AnimatorControllerParameter[] localParameters = animator != null ? animator.parameters : new AnimatorControllerParameter[0];
                    for (int parameter = 0; parameter < parameters; parameter++)
                    {
                        int hash = reader.ReadInt32(); AnimatorControllerParameterType type = (AnimatorControllerParameterType)reader.ReadByte();
                        bool exists = false;
                        if (animator != null)
                            foreach (AnimatorControllerParameter local in localParameters)
                                if (local.nameHash == hash && local.type == type) { exists = true; break; }
                        if (type == AnimatorControllerParameterType.Float)
                        { float value = Wire.Number(reader); if (exists) animator.SetFloat(hash, value); }
                        else if (type == AnimatorControllerParameterType.Int)
                        { int value = reader.ReadInt32(); if (exists) animator.SetInteger(hash, value); }
                        else if (type == AnimatorControllerParameterType.Bool)
                        { bool value = reader.ReadBoolean(); if (exists) animator.SetBool(hash, value); }
                        else throw new IOException("Unknown NPC animator parameter type.");
                    }
                }
                int sprites = reader.ReadByte();
                if (sprites > MaxNpcSprites) throw new IOException("NPC sprite limit exceeded.");
                for (int s = 0; s < sprites; s++)
                {
                    bool active = reader.ReadBoolean();
                    string asset = Wire.Text(reader, 256); Sprite sprite;
                    if (cache == null || s >= cache.Sprites.Length || cache.Sprites[s] == null) continue;
                    // Active child objects encode native state (e.g. Chalkles' flying/chalk
                    // forms). Renderer.enabled remains local so chunk culling still works.
                    if (cache.Sprites[s].gameObject != npc.gameObject && cache.Sprites[s].gameObject.activeSelf != active)
                        cache.Sprites[s].gameObject.SetActive(active);
                    if (asset.Length == 0) cache.Sprites[s].sprite = null;
                    else if (npcSpriteAssets.TryGetValue(asset, out sprite)) cache.Sprites[s].sprite = sprite;
                }
                ReadNpcWind(reader,npc);
            }
        }
    }
}
