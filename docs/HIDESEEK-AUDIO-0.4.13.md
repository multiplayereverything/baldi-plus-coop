# Multiplayer Hide & Seek intro — 0.4.13

Source: user's Game Sound Preview/NewAudio/BAL_HideSeek.wav, copied unchanged to assets/BAL_HideSeek.wav. SHA256 C3A443F5A67778FEEAA79C477C4706416EAA1F43C7074E2F144E9EC44AE27DDF. PCM16, mono, 44,100 Hz, 217,728 frames (about 4.94 seconds).

Both loader DLLs embed this clip. When multiplayer is connected, native AudioManager single/queued playback of the exact BAL_HideSeek clip substitutes a cloned SoundObject with the new samples. Native mixer, voice type, volume and subtitle key are preserved; subtitle duration follows the replacement length. Original SoundObjects and game assets remain untouched. Without a multiplayer connection playback uses the original. The tutorial clip and countdown are separate sounds and remain unchanged. No network format changes; protocol stays 8.

The focused runtime fixture checks the native HappyBaldi intro reference, replacement sample format, unmodified source metadata, actual PlayIntroAudio playback selection, queued playback, original playback with multiplayer disconnected and an unrelated sound. It checks audio selection/data, not a recorded listening session.
