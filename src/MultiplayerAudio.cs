using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BaldiTogether
{
    public sealed partial class TogetherRuntime
    {
        AudioClip hideSeekClip;
        readonly Dictionary<SoundObject,SoundObject> hideSeekSounds=new Dictionary<SoundObject,SoundObject>();
        bool hideSeekAudioFailed;
        bool audioLabStarted;

        System.Collections.IEnumerator TourHideSeekAudio()
        {
            portText="27032";Connect(true);AutoStartRun();
            while(!worldReady)yield return null;
            var happy=Object.FindObjectOfType<HappyBaldi>();
            var original=(SoundObject)AccessTools.Field(typeof(HappyBaldi),"audIntro").GetValue(happy);
            var originalClip=original.soundClip;
            CheckNativeUi("native-intro-matched",originalClip.name=="BAL_HideSeek");
            var replacement=MultiplayerSound(original);
            CheckNativeUi("replacement-format",replacement!=original&&replacement.soundClip.samples==217728&&replacement.soundClip.channels==1&&replacement.soundClip.frequency==44100);
            CheckNativeUi("native-source-unchanged",original.soundClip==originalClip&&replacement.soundKey==original.soundKey&&replacement.soundType==original.soundType);
            var manager=(AudioManager)AccessTools.Field(typeof(HappyBaldi),"audMan").GetValue(happy);
            happy.PlayIntroAudio();
            CheckNativeUi("native-intro-uses-new-clip",(AudioClip)AccessTools.Field(typeof(AudioManager),"_oneShotClip").GetValue(manager)==replacement.soundClip);
            bool paused=AudioListener.pause;
            try
            {
                AudioListener.pause=true;manager.FlushQueue(true);manager.QueueAudio(original);
                CheckNativeUi("queue-uses-new-clip",((SoundObject[])AccessTools.Field(typeof(AudioManager),"soundQueue").GetValue(manager)).Contains(replacement));
                manager.FlushQueue(true);
            }
            finally{AudioListener.pause=paused;}
            var network=Network;
            try
            {
                Network=null;happy.PlayIntroAudio();
                CheckNativeUi("single-player-original",MultiplayerSound(original)==original&&(AudioClip)AccessTools.Field(typeof(AudioManager),"_oneShotClip").GetValue(manager)==originalClip);
            }
            finally{Network=network;}
            var other=Resources.FindObjectsOfTypeAll<SoundObject>().First(s=>s.soundClip!=null&&s.soundClip.name!="BAL_HideSeek"&&s!=replacement);
            CheckNativeUi("other-sounds-unchanged",MultiplayerSound(other)==other);
            string json="{\"passed\":"+nativeUiChecks.Count(p=>p.Value)+",\"failed\":"+nativeUiChecks.Count(p=>!p.Value)+",\"checks\":{"+string.Join(",",nativeUiChecks.Select(p=>"\""+p.Key+"\":"+p.Value.ToString().ToLowerInvariant()).ToArray())+"}}";
            File.WriteAllText(Path.Combine(Root,"MultiplayerMod/artifacts/tests/hideseek-audio-results.json"),json);Log("LAB Hide & Seek audio "+json);
        }

        public SoundObject MultiplayerSound(SoundObject original)
        {
            if(!Active||original==null||original.soundClip==null||original.soundClip.name!="BAL_HideSeek"||hideSeekAudioFailed)return original;
            try
            {
                SoundObject replacement;
                if(hideSeekSounds.TryGetValue(original,out replacement))return replacement;
                if(hideSeekClip==null)hideSeekClip=LoadHideSeekWave();
                replacement=Object.Instantiate(original);
                replacement.name=original.name+" (multiplayer)";
                replacement.soundClip=hideSeekClip;replacement.subDuration=hideSeekClip.length;
                hideSeekSounds.Add(original,replacement);
                return replacement;
            }
            catch(Exception ex){hideSeekAudioFailed=true;Log("Multiplayer Hide & Seek audio: "+ex.Message);return original;}
        }

        AudioClip LoadHideSeekWave()
        {
            var clip=LoadEmbeddedWave("BaldiTogether.BAL_HideSeek.wav","BAL_HideSeek_Multiplayer");
            Log("Loaded multiplayer Hide & Seek audio: "+clip.length+" seconds.");return clip;
        }

        // Decodes an embedded PCM16 RIFF WAV into an owned AudioClip.
        internal static AudioClip LoadEmbeddedWave(string resource,string clipName)
        {
            using(var stream=typeof(TogetherRuntime).Assembly.GetManifestResourceStream(resource))
            {
            if(stream==null)throw new IOException("Embedded audio is missing: "+resource);
            using(var reader=new BinaryReader(stream))
            {
                if(new string(reader.ReadChars(4))!="RIFF")throw new IOException("Expected RIFF WAV.");
                reader.ReadUInt32();if(new string(reader.ReadChars(4))!="WAVE")throw new IOException("Expected WAVE.");
                int channels=0,rate=0,bits=0,format=0;byte[] pcm=null;
                while(stream.Position+8<=stream.Length)
                {
                    string chunk=new string(reader.ReadChars(4));uint size=reader.ReadUInt32();long next=stream.Position+size+(size%2);
                    if(next>stream.Length)throw new IOException("Truncated WAV.");
                    if(chunk=="fmt "){format=reader.ReadUInt16();channels=reader.ReadUInt16();rate=reader.ReadInt32();reader.ReadInt32();reader.ReadUInt16();bits=reader.ReadUInt16();}
                    else if(chunk=="data")pcm=reader.ReadBytes(checked((int)size));
                    stream.Position=next;
                }
                if(format!=1||bits!=16||channels<1||rate<1||pcm==null||pcm.Length%(channels*2)!=0)throw new IOException("Expected PCM16 audio.");
                var samples=new float[pcm.Length/2];for(int i=0;i<samples.Length;i++)samples[i]=(short)(pcm[i*2]|pcm[i*2+1]<<8)/32768f;
                var clip=AudioClip.Create(clipName,samples.Length/channels,channels,rate,false);
                if(!clip.SetData(samples,0)){Object.Destroy(clip);throw new IOException("Could not load samples.");}
                return clip;
            }
            }
        }

        void DisposeMultiplayerAudio()
        {
            foreach(var sound in hideSeekSounds.Values)if(sound!=null)Object.Destroy(sound);
            hideSeekSounds.Clear();if(hideSeekClip!=null)Object.Destroy(hideSeekClip);
        }
    }

    [HarmonyPatch(typeof(AudioManager),"PlaySingle",new[]{typeof(SoundObject),typeof(float)})]
    static class MultiplayerSingleSoundPatch
    {static void Prefix(ref SoundObject file){var runtime=ModHooks.Current;if(runtime!=null)file=runtime.MultiplayerSound(file);}}

    [HarmonyPatch(typeof(AudioManager),"QueueAudio",new[]{typeof(SoundObject),typeof(bool)})]
    static class MultiplayerQueuedSoundPatch
    {static void Prefix(ref SoundObject file){var runtime=ModHooks.Current;if(runtime!=null)file=runtime.MultiplayerSound(file);}}
}
