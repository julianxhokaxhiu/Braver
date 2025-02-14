﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using Braver.Plugins;
using Microsoft.Xna.Framework.Audio;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Braver {

    public class Audio : IAudio {

        private Channel<MusicCommand> _channel;
        private Ficedula.FF7.Audio _sfxSource;
        private FGame _game;
        private byte _volume = 127;
        private float _masterVolume;
        private PluginInstances<ISfxSource> _sfxPlugins;

        public Audio(FGame game, string soundFolder, PluginInstances<ISfxSource> sfxPlugins) {
            _game = game;
            _sfxPlugins = sfxPlugins;
            _masterVolume = game.GameOptions.MusicVolume;
            _channel = Channel.CreateBounded<MusicCommand>(8);
            Task.Run(RunMusic);
            _sfxSource = new Ficedula.FF7.Audio(
                Path.Combine(soundFolder, "audio.dat"),
                Path.Combine(soundFolder, "audio.fmt")
            );
            //WaveOutUtils.SetWaveOutVolume(1f, IntPtr.Zero, this);
        }

        private enum CommandType {
            Play,
            Stop,
            Push,
            Pop,
            SetVolume,
        }
        private class MusicCommand {
            public CommandType Command { get; set; }
            public string Track { get; set; }
            public byte Param { get; set; }
        }

        private class MusicContext {
            public NAudio.Vorbis.VorbisWaveReader Vorbis { get; set; }
            public NAudio.Wave.WaveOut WaveOut { get; set; }
            public VolumeSampleProvider Volume { get; set; }
            public string Track { get; set; }
        }

        private class LoopProvider : ISampleProvider {

            private NAudio.Vorbis.VorbisWaveReader _source;
            private int _loopStart, _loopEnd;

            public WaveFormat WaveFormat => _source.WaveFormat;

            private long _samplesRead;
            private long _seekBeforeLoopStart, _samplesBeforeLoopStart;

            public LoopProvider(NAudio.Vorbis.VorbisWaveReader source, int loopStart, int loopEnd) {
                _source = source;
                _loopStart = loopStart * source.WaveFormat.Channels;
                _loopEnd = loopEnd * source.WaveFormat.Channels;
            }

            private float[] _discardBuffer = new float[4096];

            public int Read(float[] buffer, int offset, int count) {

                int read = _source.Read(buffer, offset, count);
                _samplesRead += read;
                if ((_samplesRead <= _loopStart) && (_samplesRead > _samplesBeforeLoopStart)) {
                    _samplesBeforeLoopStart = _samplesRead;
                    _seekBeforeLoopStart = _source.Position;
                } else if ((_samplesRead >= _loopEnd) && ((_loopEnd > 0) || read == 0)) {
                    _samplesRead = _samplesBeforeLoopStart;
                    _source.Position = _seekBeforeLoopStart;
                    int toDiscard = (int)(_loopStart - _samplesBeforeLoopStart);
                    while (toDiscard > 0) {
                        int discard = _source.Read(_discardBuffer, 0, Math.Min(_discardBuffer.Length, toDiscard));
                        toDiscard -= discard;
                        _samplesRead += discard;
                    }
                    read = _source.Read(buffer, offset, count);
                }

                if (read == 0) {
                    Trace.WriteLine("Manually restarting music playback");
                    _source.Position = 0;
                    read = _source.Read(buffer, offset, count);
                }

                return read;
            }
        }

        public void StopLoopingSfx(bool includeChannels) {
            foreach (var loop in _activeLoops) {                
                loop.Stop();
                loop.Dispose();
            }

            if (includeChannels) {
                foreach (var chInstance in _channels.Values) {
                    chInstance.Stop();
                    chInstance.Dispose(); //TODO - reasonable or not?!
                }
                _channels.Clear();
            }

            _activeLoops.Clear();

            _game.Net.Send(new Net.SfxChannelMessage {
                StopLoops = true,
                StopChannelLoops = includeChannels,
            });
        }

        public void Update() {
            var needsRestart = _activeLoops.Where(instance => instance.State != SoundState.Playing);
            foreach (var loop in needsRestart)
                loop.Play();
        }

        private async Task RunMusic() {
            var contexts = new Stack<MusicContext>();
            contexts.Push(new MusicContext());

            void DoStop() {
                var context = contexts.Peek();
                if (context.Vorbis != null) {
                    context.WaveOut.Dispose();
                    context.Vorbis.Dispose();
                    context.WaveOut = null;
                    context.Vorbis = null;
                }
            }
            void DoPlay(string track) {
                var current = contexts.Peek();
                DoStop();
                var source = _game.TryOpen("vgmstream", track + ".ogg");
                if (source == null) {
                    Trace.WriteLine($"Failed to find music track {track}.ogg");
                    return;
                }

                int loopStart = 0, loopEnd = 0;
                using (var reader = new NVorbis.VorbisReader(source, false)) {
                    foreach (var tag in reader.Tags.All)
                        Trace.WriteLine($"Vorbis tag: {tag.Key} = {string.Join(", ", tag.Value)}");
                    int.TryParse(reader.Tags.GetTagSingle("LOOPSTART").Trim(), out loopStart);
                    int.TryParse(reader.Tags.GetTagSingle("LOOPEND").Trim(), out loopEnd);
                }
                source.Position = 0;
                current.Vorbis = new NAudio.Vorbis.VorbisWaveReader(source, true);
                current.WaveOut = new NAudio.Wave.WaveOut();
                //current.WaveOut.Init(current.Vorbis);
                current.Volume = new VolumeSampleProvider(new LoopProvider(current.Vorbis, loopStart, loopEnd));
                current.WaveOut.Init(current.Volume);
                current.Volume.Volume = _masterVolume * _volume / 127f;
                current.WaveOut.Play();
                current.Track = track;
                Trace.WriteLine($"Playing track {track} at final volume {current.Volume.Volume}");
            }

            try {
                while (true) {
                    var command = await _channel.Reader.ReadAsync();
                    if (command == null) break;

                    switch (command.Command) {
                        case CommandType.SetVolume:
                            _volume = command.Param;
                            if (contexts.Peek().WaveOut != null) {
                                contexts.Peek().Volume.Volume = _masterVolume * _volume / 127f;
                                Trace.WriteLine($"Changing music output volume to {contexts.Peek().Volume.Volume}");
                            }
                            break;

                        case CommandType.Play:
                            if (command.Track != contexts.Peek().Track)
                                DoPlay(command.Track);
                            break;

                        case CommandType.Stop:
                            DoStop();
                            break;

                        case CommandType.Pop:
                            DoStop();
                            contexts.Pop();
                            if (contexts.Peek().Vorbis != null)
                                contexts.Peek().WaveOut.Resume();
                            break;

                        case CommandType.Push:
                            if (contexts.Peek().Vorbis != null)
                                contexts.Peek().WaveOut.Pause();
                            contexts.Push(new MusicContext());
                            DoPlay(command.Track);
                            break;
                    }
                }
            } catch (Exception ex) {
                System.Diagnostics.Trace.WriteLine($"Music thread crashed! {ex}");
                throw;
            }
        }

        public void SetMusicVolume(byte? volumeFrom, byte volumeTo, float duration) {
            if (duration <= 0) {
                _channel.Writer.TryWrite(new MusicCommand {
                    Command = CommandType.SetVolume,
                    Param = volumeTo,
                });
            } else {
                byte current = volumeFrom ?? _volume;
                Task.Run(async () => {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while(sw.Elapsed.TotalSeconds < duration) {
                        byte v = (byte)(current + (volumeTo - current) * (sw.Elapsed.TotalSeconds / duration));
                        _channel.Writer.TryWrite(new MusicCommand {
                            Command = CommandType.SetVolume,
                            Param = v,
                        });
                        await Task.Delay(10);
                    }
                    _channel.Writer.TryWrite(new MusicCommand {
                        Command = CommandType.SetVolume,
                        Param = volumeTo,
                    });
                });
            }
            _game.Net.Send(new Net.MusicVolumeMessage {
                VolumeFrom = volumeFrom,
                VolumeTo = volumeTo,
                Duration = duration
            });
        }
        public void SetMusicVolume(byte volume) => SetMusicVolume(null, volume, 0);

        public void PlayMusic(string name, bool pushContext = false) {
            _channel.Writer.TryWrite(new MusicCommand {
                Track = name,
                Command = pushContext ? CommandType.Push : CommandType.Play,
            });
            _game.Net.Send(new Net.MusicMessage { Track = name, IsPush = pushContext });
        }
        public void StopMusic(bool popContext = false) {
            _channel.Writer.TryWrite(new MusicCommand {
                Command = popContext ? CommandType.Pop : CommandType.Stop
            });
            _game.Net.Send(new Net.MusicMessage { Track = string.Empty, IsPop = popContext });
        }

        private class LoadedEffect {
            public SoundEffect Effect { get; set; }
            public bool ShouldLoop { get; set; }
        }

        private Dictionary<int, WeakReference<LoadedEffect>> _sfx = new();
        private HashSet<LoadedEffect> _recent0 = new(), _pinned = new(), _recent1;
        private HashSet<SoundEffectInstance> _activeLoops = new();
        private DateTime _lastPromote = DateTime.MinValue;
        private Dictionary<int, SoundEffectInstance> _channels = new();

        private SfxData DefaultSfxData(int which) {
            byte[] raw = _sfxSource.ExportPCM(which, out int freq, out int channels);
            return new SfxData {
                RawData = raw,
                Channels = channels,
                Frequency = freq,
            };
        }

        private LoadedEffect GetEffect(int which) {
            var data = _sfxPlugins.Call(sfx => sfx.Load(which)) ?? DefaultSfxData(which);
            var fx = new SoundEffect(data.RawData, data.Frequency, data.Channels == 1 ? AudioChannels.Mono : AudioChannels.Stereo);
            return new LoadedEffect {
                Effect = fx,
                ShouldLoop = _sfxSource.GetExtraData(which)[0] != 0, //TODO - seems like it *might* be right?!
            };
        }

        public void Precache(Sfx which, bool pin) {
            var effect = GetEffect((int)which);
            _sfx[(int)which] = new WeakReference<LoadedEffect>(effect);

            if (pin)
                _pinned.Add(effect);
        }

        public void StopChannel(int channel) {
            if (_channels.TryGetValue(channel, out var instance)) {
                instance.Stop();
                instance.Dispose();
                _channels.Remove(channel);
                _activeLoops.Remove(instance);
                _game.Net.Send(new Net.SfxChannelMessage {
                    DoStop = true,
                    Channel = channel,
                });
            }
        }

        public void GetChannelProperty(int channel, out float? pan, out float? volume) {
            if (_channels.TryGetValue(channel, out var instance)) {
                pan = instance.Pan; volume = instance.Volume;
            } else {
                pan = volume = null;
            }
        }

        public void ChannelProperty(int channel, float? pan, float? volume) {
            if (_channels.TryGetValue(channel, out var instance)) {
                if (pan != null)
                    instance.Pan = pan.Value;
                if (volume != null)
                    instance.Volume = volume.Value;
                _game.Net.Send(new Net.SfxChannelMessage {
                    Channel = channel,
                    Pan = pan,
                    Volume = volume,
                });
            }
        }


        public void PlaySfx(Sfx which, float volume, float pan, int? channel = null) => PlaySfx((int)which, volume, pan, channel);
        public void PlaySfx(int which, float volume, float pan, int? channel = null) {
            LoadedEffect effect;

            if (_sfx.TryGetValue(which, out var wr) && wr.TryGetTarget(out effect)) {
                //
            } else {
                effect = GetEffect(which);
                _sfx[which] = new WeakReference<LoadedEffect>(effect);
            }

            if (effect.ShouldLoop) {
                var instance = effect.Effect.CreateInstance();
                instance.Pan = pan;
                instance.Volume = volume;
                instance.Play();
                _activeLoops.Add(instance);
                if (channel != null)
                    _channels[channel.Value] = instance;
            } else if (channel != null) {
                var instance = effect.Effect.CreateInstance();
                instance.Pan = pan;
                instance.Volume = volume;
                instance.Play();
                _channels[channel.Value] = instance;
            } else {
                effect.Effect.Play(volume, 0, pan);
            }

            if (_lastPromote < DateTime.Now.AddMinutes(-1)) {
                _recent1 = _recent0;
                _recent0 = new();
            }
            _recent0.Add(effect);
            _game.Net.Send(new Net.SfxMessage { Which = which, Volume = volume, Pan = pan });
        }

        public void Quit() {
            _channel.Writer.TryWrite(null);
        }

        private class LoadedAudioItem : IAudioItem {

            private WaveOut _waveOut;
            private NAudio.Vorbis.VorbisWaveReader _vorbis;
            private VolumeSampleProvider _volume;
            private PanningSampleProvider _pan;
            private SmbPitchShiftingSampleProvider _pitch;
            private bool _shouldLoop;

            public bool IsPlaying => _waveOut.PlaybackState == PlaybackState.Playing;

            public LoadedAudioItem(Stream s) {
                _waveOut = new WaveOut();
                _waveOut.PlaybackStopped += _waveOut_PlaybackStopped;
                _vorbis = new NAudio.Vorbis.VorbisWaveReader(s, true);
                _volume = new VolumeSampleProvider(_vorbis);
                if (_vorbis.WaveFormat.Channels == 1)
                    _pan = new PanningSampleProvider(_volume);
                _pitch = new SmbPitchShiftingSampleProvider((ISampleProvider)_pan ?? _volume) {
                    PitchFactor = 1f
                };
                _waveOut.Init(_pitch);
            }

            private void _waveOut_PlaybackStopped(object sender, StoppedEventArgs e) {
                if (_shouldLoop) {
                    _vorbis.Position = 0;
                    _waveOut.Play();
                }
            }

            public void Dispose() {
                _waveOut.Stop();
                _waveOut.Dispose();
                _vorbis.Dispose();
            }

            public void Pause() {
                _waveOut.Pause();
            }

            public void Resume() {
                _waveOut.Resume();
            }

            public void Play(float volume, float pan, bool loop, float pitch) {
                _vorbis.Position = 0;
                _shouldLoop = loop;
                _volume.Volume = volume;
                if (_pan != null)
                    _pan.Pan = pan;
                _pitch.PitchFactor = pitch;
                Trace.WriteLine($"AudioItem play vol {volume} pan {pan} pitch {pitch}");
                _waveOut.Play();
            }

            public void Stop() {
                _shouldLoop = false;
                _waveOut.Stop();
            }
        }

        public IAudioItem LoadStream(string path, string file) {
            var item = TryLoadStream(path, file);
            if (item == null) throw new InvalidDataException($"Could not find audio stream {path}/{file}");
            return item;
        }
        public IAudioItem TryLoadStream(string path, string file) {
            //TODO networking
            var s = _game.TryOpen(path, file);
            if (s != null)
                return new LoadedAudioItem(s);
            else
                return null;
        }

        public void DecodeStream(Stream s, out byte[] rawData, out int channels, out int frequency) {
            using (var reader = new NVorbis.VorbisReader(s, false)) {
                channels = reader.Channels;
                frequency = reader.SampleRate;
                rawData = new byte[0];
                float[] buffer = new float[0x10000];
                short[] sBuffer = new short[0x10000];
                int read;
                while((read = reader.ReadSamples(buffer, 0, buffer.Length)) != 0) {
                    for (int i = 0; i < read; i++)
                        sBuffer[i] = (short)(buffer[i] * short.MaxValue);
                    Array.Resize(ref rawData, rawData.Length + read * 2);
                    Buffer.BlockCopy(sBuffer, 0, rawData, rawData.Length - read * 2, read * 2);
                }
            }
        }
    }

}
