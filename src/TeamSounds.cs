using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    // Short multiplayer cues generated with Stable Audio 3 small-sfx and embedded in the loader DLLs.
    // Room and capture cues play through the game's own sound-effect source; pings play in 3D at the marker.
    public sealed partial class TogetherRuntime
    {
        static readonly string[] TeamSoundNames = { "ping", "join", "leave", "caught", "switch", "found" };
        readonly Dictionary<string, SoundObject> teamSounds = new Dictionary<string, SoundObject>();
        bool teamSoundsFailed;
        int teamSoundsPlayed;
        // Lab evidence of which cues fired, newest last.
        readonly List<string> teamSoundLog = new List<string>();
        void RecordTeamSound(string name) { teamSoundsPlayed++; teamSoundLog.Add(name); if (teamSoundLog.Count > 64) teamSoundLog.RemoveAt(0); }

        SoundObject TeamSound(string name)
        {
            if (teamSoundsFailed) return null;
            SoundObject sound;
            if (teamSounds.TryGetValue(name, out sound)) return sound;
            try
            {
                var clip = LoadEmbeddedWave("BaldiTogether.Sfx." + name + ".wav", "BaldiTogether_" + name);
                sound = ScriptableObject.CreateInstance<SoundObject>();
                sound.name = "Baldi Together " + name; sound.soundClip = clip; sound.soundType = SoundType.Effect;
                sound.soundKey = ""; sound.subtitle = false; sound.subDuration = clip.length; sound.addToMemory = false;
                teamSounds.Add(name, sound);
                return sound;
            }
            catch (Exception ex) { teamSoundsFailed = true; Log("Team sounds unavailable: " + ex.Message); return null; }
        }

        void PlayTeamSound(string name)
        {
            var music = Singleton<MusicManager>.Instance;
            var sound = TeamSound(name);
            if (music == null || sound == null) return;
            music.PlaySoundEffect(sound); RecordTeamSound(name);
        }

        // 3D one-shot on the marker so teammates can hear where the ping is.
        void PlayTeamSoundAt(string name, GameObject anchor)
        {
            var sound = TeamSound(name);
            if (sound == null || anchor == null) return;
            var source = anchor.AddComponent<AudioSource>();
            source.playOnAwake = false; source.spatialBlend = 1; source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 10; source.maxDistance = 160; source.dopplerLevel = 0;
            source.outputAudioMixerGroup = EffectsMixerGroup();
            source.PlayOneShot(sound.soundClip); RecordTeamSound(name);
        }

        // The game routes every SoundType.Effect through this static group, which carries the SFX volume setting.
        static AudioMixerGroup EffectsMixerGroup() { return AudioSourceManager.sfxMixerGroup; }

        void DisposeTeamSounds()
        {
            foreach (var sound in teamSounds.Values)
                if (sound != null) { if (sound.soundClip != null) Object.Destroy(sound.soundClip); Object.Destroy(sound); }
            teamSounds.Clear();
        }
    }
}
