﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Ficedula.FF7 {

	internal class MSADPCMToPCM {

		/* MSADPCMToPCM - Public Domain MSADPCM Decoder
		 * https://github.com/flibitijibibo/MSADPCMToPCM
		 *
		 * Written by Ethan "flibitijibibo" Lee
		 * http://www.flibitijibibo.com/
		 *
		 * Released under public domain.
		 * No warranty implied; use at your own risk.
		 *
		 * For more on the MSADPCM format, see the MultimediaWiki:
		 * http://wiki.multimedia.cx/index.php?title=Microsoft_ADPCM
		 */

		/**
		 * A bunch of magical numbers that predict the sample data from the
		 * MSADPCM wavedata. Do not attempt to understand at all costs!
		 */
		private static readonly int[] AdaptionTable = {
			230, 230, 230, 230, 307, 409, 512, 614,
			768, 614, 512, 409, 307, 230, 230, 230
		};
		private static readonly int[] AdaptCoeff_1 = {
			256, 512, 0, 192, 240, 460, 392
		};
		private static readonly int[] AdaptCoeff_2 = {
			0, -256, 0, 64, 0, -208, -232
		};

		/**
		 * Splits the MSADPCM samples from each byte block.
		 * @param block An MSADPCM sample byte
		 * @param nibbleBlock we copy the parsed shorts into here
		 */
		private static void getNibbleBlock(byte block, byte[] nibbleBlock) {
			nibbleBlock[0] = (byte)(block >> 4); // Upper half
			nibbleBlock[1] = (byte)(block & 0xF); // Lower half
		}

		/**
		 * Calculates PCM samples based on previous samples and a nibble input.
		 * @param nibble A parsed MSADPCM sample we got from getNibbleBlock
		 * @param predictor The predictor we get from the MSADPCM block's preamble
		 * @param sample_1 The first sample we use to predict the next sample
		 * @param sample_2 The second sample we use to predict the next sample
		 * @param delta Used to calculate the final sample
		 * @return The calculated PCM sample
		 */
		private static short calculateSample(
			byte nibble,
			byte predictor,
			ref short sample_1,
			ref short sample_2,
			ref short delta
		) {
			// Get a signed number out of the nibble. We need to retain the
			// original nibble value for when we access AdaptionTable[].
			sbyte signedNibble = (sbyte)nibble;
			if ((signedNibble & 0x8) == 0x8) {
				signedNibble -= 0x10;
			}

			// Calculate new sample
			int sampleInt = (
				((sample_1 * AdaptCoeff_1[predictor]) +
					(sample_2 * AdaptCoeff_2[predictor])
				) / 256
			);
			sampleInt += signedNibble * delta;

			// Clamp result to 16-bit
			short sample;
			if (sampleInt < short.MinValue) {
				sample = short.MinValue;
			} else if (sampleInt > short.MaxValue) {
				sample = short.MaxValue;
			} else {
				sample = (short)sampleInt;
			}

			// Shuffle samples, get new delta
			sample_2 = sample_1;
			sample_1 = sample;
			delta = (short)(AdaptionTable[nibble] * delta / 256);

			// Saturate the delta to a lower bound of 16
			if (delta < 16)
				delta = 16;

			return sample;
		}

		/**
		 * Decodes MSADPCM data to signed 16-bit PCM data.
		 * @param Source A BinaryReader containing the headerless MSADPCM data
		 * @param numChannels The number of channels (WAVEFORMATEX nChannels)
		 * @param blockAlign The ADPCM block size (WAVEFORMATEX nBlockAlign)
		 * @return A byte array containing the raw 16-bit PCM wavedata
		 *
		 * NOTE: The original MSADPCMToPCM class returns as a short[] array!
		 */
		public static byte[] MSADPCM_TO_PCM(
			BinaryReader Source,
			short numChannels,
			short blockAlign
		) {
			// We write to output when reading the PCM data, then we convert
			// it back to a short array at the end.
			MemoryStream output = new MemoryStream();
			BinaryWriter pcmOut = new BinaryWriter(output);

			// We'll be using this to get each sample from the blocks.
			byte[] nibbleBlock = new byte[2];

			// Assuming the whole stream is what we want.
			long fileLength = Source.BaseStream.Length - blockAlign;

			// Mono or Stereo?
			if (numChannels == 1) {
				// Read to the end of the file.
				while (Source.BaseStream.Position <= fileLength) {
					// Read block preamble
					byte predictor = Source.ReadByte();
					short delta = Source.ReadInt16();
					short sample_1 = Source.ReadInt16();
					short sample_2 = Source.ReadInt16();

					// Send the initial samples straight to PCM out.
					pcmOut.Write(sample_2);
					pcmOut.Write(sample_1);

					// Go through the bytes in this MSADPCM block.
					for (int bytes = 0; bytes < (blockAlign - 7); bytes++) {
						// Each sample is one half of a nibbleBlock.
						getNibbleBlock(Source.ReadByte(), nibbleBlock);
						for (int i = 0; i < 2; i++) {
							pcmOut.Write(
								calculateSample(
									nibbleBlock[i],
									predictor,
									ref sample_1,
									ref sample_2,
									ref delta
								)
							);
						}
					}
				}
			} else if (numChannels == 2) {
				// Read to the end of the file.
				while (Source.BaseStream.Position <= fileLength) {
					// Read block preamble
					byte l_predictor = Source.ReadByte();
					byte r_predictor = Source.ReadByte();
					short l_delta = Source.ReadInt16();
					short r_delta = Source.ReadInt16();
					short l_sample_1 = Source.ReadInt16();
					short r_sample_1 = Source.ReadInt16();
					short l_sample_2 = Source.ReadInt16();
					short r_sample_2 = Source.ReadInt16();

					// Send the initial samples straight to PCM out.
					pcmOut.Write(l_sample_2);
					pcmOut.Write(r_sample_2);
					pcmOut.Write(l_sample_1);
					pcmOut.Write(r_sample_1);

					// Go through the bytes in this MSADPCM block.
					for (int bytes = 0; bytes < (blockAlign  - 14); bytes++) {
						// Each block carries one left/right sample.
						getNibbleBlock(Source.ReadByte(), nibbleBlock);

						// Left channel...
						pcmOut.Write(
							calculateSample(
								nibbleBlock[0],
								l_predictor,
								ref l_sample_1,
								ref l_sample_2,
								ref l_delta
							)
						);

						// Right channel...
						pcmOut.Write(
							calculateSample(
								nibbleBlock[1],
								r_predictor,
								ref r_sample_1,
								ref r_sample_2,
								ref r_delta
							)
						);
					}
				}
			} else {
				pcmOut.Close();
				output.Close();
				throw new Exception("MSADPCM WAVEDATA IS NOT MONO OR STEREO!");
			}

			// We're done writing PCM data...
			pcmOut.Close();
			output.Close();

			// Return the array.
			return output.ToArray();
		}
	}

	public class Audio : IDisposable {

        private struct SoundEntry {
            public int Size;
            public int Offset;
            public byte[] ExtraData; //16
            public byte[] WAVFORMATEX; //18
            public ushort SamplesPerBlock;
            public ushort NumCoef;
            public byte[] ADPCMCoefSets; //32?
        }

        private struct WAVEFORMATEX {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        private List<SoundEntry> _entries = new();
        private Stream _dat;

        public int EntryCount => _entries.Count;

        public Audio(string datFile, string fmtFile) {
            _dat = new FileStream(datFile, FileMode.Open, FileAccess.Read);
            using (var s = new FileStream(fmtFile, FileMode.Open, FileAccess.Read)) {
                //int count = s.ReadI32();
                //s.Position = 40;
                while(s.Position < s.Length) { 
                    int size = s.ReadI32();
                    if (size == 0) {
                        _entries.Add(new SoundEntry());
                        s.Seek(38, SeekOrigin.Current);
                        continue;
                    } else {
                        var entry = new SoundEntry {
                            Size = size,
                            Offset = s.ReadI32(),
                            ExtraData = s.ReadBytes(16),
                            WAVFORMATEX = s.ReadBytes(18),
                            SamplesPerBlock = s.ReadU16(),
                            NumCoef = s.ReadU16(),
                            
                        };
                        entry.ADPCMCoefSets = s.ReadBytes(entry.NumCoef * 4);
                        _entries.Add(entry);
                    }
                }
            }
        }

        public void Export(int soundID, Stream dest) {
            var entry = _entries[soundID];

            dest.WriteAscii("RIFF");
            dest.WriteI32(entry.Size + 36);
            dest.WriteAscii("WAVEfmt ");
            dest.WriteI32(18);
            dest.Write(entry.WAVFORMATEX, 0, entry.WAVFORMATEX.Length);
            dest.WriteAscii("data");
            dest.WriteI32(entry.Size);
            byte[] buffer = new byte[entry.Size];
			lock (_dat) {
				_dat.Position = entry.Offset;
				_dat.Read(buffer, 0, buffer.Length);
			}
            dest.Write(buffer, 0, buffer.Length);
        }

		public byte[] GetExtraData(int soundID) => _entries[soundID].ExtraData;
		public byte[] ExportPCM(int soundID, out int frequency, out int channels) {
			var entry = _entries[soundID];
			byte[] buffer = new byte[entry.Size];
			lock (_dat) {
				_dat.Position = entry.Offset;
				_dat.Read(buffer, 0, buffer.Length);
			}

			short nChannels = BitConverter.ToInt16(entry.WAVFORMATEX, 2),
				nBlockAlign = BitConverter.ToInt16(entry.WAVFORMATEX, 12);

			channels = nChannels;
			frequency = BitConverter.ToInt32(entry.WAVFORMATEX, 4);

			return MSADPCMToPCM.MSADPCM_TO_PCM(
				new BinaryReader(new MemoryStream(buffer)),
				nChannels, nBlockAlign
			);

		}

        public void Dispose() {
            _dat.Dispose();
        }
    }
}
