﻿using Microsoft.Xna.Framework.Audio;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace F7 {
    public class Audio {

        private string _ff7Dir;
        private Channel<string> _channel;
        private Ficedula.FF7.Audio _sfxSource;

        public Audio(string ff7dir) {
            _ff7Dir = ff7dir;
            _channel = Channel.CreateBounded<string>(8);
            Task.Run(RunMusic);
            _sfxSource = new Ficedula.FF7.Audio(
                System.IO.Path.Combine(ff7dir, "sound", "audio.dat"),
                System.IO.Path.Combine(ff7dir, "sound", "audio.fmt")
            );
        }

        private async Task RunMusic() {
            NAudio.Vorbis.VorbisWaveReader vorbis = null;
            NAudio.Wave.WaveOut waveOut = null;

            while (true) {
                string file = await _channel.Reader.ReadAsync();
                switch (file) {
                    case null:
                        return;
                    case "":
                        if (vorbis != null) {
                            waveOut.Dispose();
                            vorbis.Dispose();
                            waveOut = null;
                            vorbis = null;
                        }
                        break;
                    default:
                        vorbis = new NAudio.Vorbis.VorbisWaveReader(System.IO.Path.Combine(_ff7Dir, "music_ogg", file));
                        waveOut = new NAudio.Wave.WaveOut();
                        waveOut.Init(vorbis);
                        waveOut.Play();
                        break;
                }
            }
        }

        public void PlayMusic(string name) {
            _channel.Writer.TryWrite(name);
        }
        public void StopMusic() {
            _channel.Writer.TryWrite(string.Empty);
        }

        private Dictionary<int, WeakReference<SoundEffect>> _sfx = new();
        private HashSet<SoundEffect> _recent0 = new(), _recent1 = new();
        private DateTime _lastPromote = DateTime.MinValue;

        public void PlaySfx(Sfx which, float volume, float pan) => PlaySfx((int)which, volume, pan);
        public void PlaySfx(int which, float volume, float pan) { 
            SoundEffect fx;
            if (_sfx.TryGetValue(which, out var wr) && wr.TryGetTarget(out fx)) {
                //
            } else {
                byte[] raw = _sfxSource.ExportPCM(which, out int freq, out int channels);
                fx = new SoundEffect(raw, freq, channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
                _sfx[which] = new WeakReference<SoundEffect>(fx);
            }
            _recent0.Add(fx);
            fx.Play(volume, 0, pan);
            if (_lastPromote < DateTime.Now.AddMinutes(-1)) {
                _recent1 = _recent0;
                _recent0 = new();
            }
        }


        public void Quit() {
            _channel.Writer.TryWrite(null);
        }
    }

    public enum Sfx {
        Cursor = 0,
        SaveReady = 1,
        Invalid = 2,
        Cancel = 3,
    }
}
