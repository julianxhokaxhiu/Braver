﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ficedula.FF7;

namespace Braver {
    public class VMM {

        private byte[][] _banks;
        private byte[] _scratch;

        public VMM() {
            ResetAll();
        }

        public void Save(Stream s) {
            foreach(int bank in Enumerable.Range(0, _banks.Length)) {
                s.WriteI32(bank);
                s.WriteI32(_banks[bank].Length);
                s.Write(new ReadOnlySpan<byte>(_banks[bank]));
            }
            s.WriteI32(-1);
            s.WriteI32(_scratch.Length);
            s.Write(new ReadOnlySpan<byte>(_scratch));
        }

        public void Load(Stream s) {
            ResetAll();
            while(s.Position < s.Length) {
                int bank = s.ReadI32();
                byte[] data = new byte[s.ReadI32()];
                s.Read(new Span<byte>(data));
                if (bank < 0)
                    _scratch = data;
                else
                    _banks[bank] = data;
            }
        }

        public void ResetAll() {
            _banks = Enumerable.Range(0, 5)
                .Select(_ => new byte[256])
                .ToArray();
            _scratch = new byte[256];
        }

        public void ResetScratch() {
            _scratch = new byte[256];
        }

        public void Write(int bank, int offset, ushort value) {
            switch (bank) {
                case 0:
                    throw new F7Exception("Can't write to literal bank 0");
                case 1:
                    _banks[0][offset] = (byte)value;
                    break;
                case 2:
                    _banks[0][offset] = (byte)value;
                    _banks[0][offset + 1] = (byte)(value >> 8);
                    break;
                case 3:
                    _banks[1][offset] = (byte)value;
                    break;
                case 4:
                    _banks[1][offset] = (byte)value;
                    _banks[1][offset + 1] = (byte)(value >> 8);
                    break;
                case 0xB:
                    _banks[2][offset] = (byte)value;
                    break;
                case 0xC:
                    _banks[2][offset] = (byte)value;
                    _banks[2][offset + 1] = (byte)(value >> 8);
                    break;
                case 0xD:
                    _banks[3][offset] = (byte)value;
                    break;
                case 0xE:
                    _banks[3][offset] = (byte)value;
                    _banks[3][offset + 1] = (byte)(value >> 8);
                    break;
                case 0xF:
                    _banks[4][offset] = (byte)value;
                    break;
                case 7:
                    _banks[4][offset] = (byte)value;
                    _banks[4][offset + 1] = (byte)(value >> 8);
                    break;

                case 5:
                    _scratch[offset] = (byte)value;
                    break;
                case 6:
                    _scratch[offset] = (byte)value;
                    _scratch[offset + 1] = (byte)(value >> 8);
                    break;

                default:
                    throw new F7Exception($"Unknown memory bank {bank}/{offset}");
            }
        }
        public void Write(int bank, int offset, byte value) {
            switch (bank) {
                case 0:
                    throw new F7Exception("Can't write to literal bank 0");
                case 1:
                    _banks[0][offset] = value;
                    break;
                case 2:
                    _banks[0][offset] = value;
                    break;
                case 3:
                    _banks[1][offset] = value;
                    break;
                case 4:
                    _banks[1][offset] = value;
                    break;
                case 0xB:
                    _banks[2][offset] = value;
                    break;
                case 0xC:
                    _banks[2][offset] = value;
                    break;
                case 0xD:
                    _banks[3][offset] = value;
                    break;
                case 0xE:
                    _banks[3][offset] = value;
                    break;
                case 0xF:
                    _banks[4][offset] = value;
                    break;
                case 7:
                    _banks[4][offset] = value;
                    break;

                case 5:
                    _scratch[offset] = value;
                    break;
                case 6:
                    _scratch[offset] = value;
                    break;

                default:
                    throw new F7Exception($"Unknown memory bank {bank}/{offset}");
            }
        }

        public int Read(int bank, int offset, bool signed = false) {

            int Read8(byte[] b) {
                if (signed)
                    return (sbyte)b[offset];
                else
                    return b[offset];
            }
            int Read16(byte[] b) {
                if (signed)
                    return (short)(ushort)(b[offset] | (b[offset + 1] << 8));
                else
                    return b[offset] | (b[offset + 1] << 8);
            }

            switch (bank) {
                case 0:
                    return offset;
                case 1:
                    return Read8(_banks[0]);
                case 2:
                    return Read16(_banks[0]);
                case 3:
                    return Read8(_banks[1]);
                case 4:
                    return Read16(_banks[1]);
                case 0xB:
                    return Read8(_banks[2]);
                case 0xC:
                    return Read16(_banks[2]);
                case 0xD:
                    return Read8(_banks[3]);
                case 0xE:
                    return Read16(_banks[3]);
                case 0xF:
                    return Read8(_banks[4]);
                case 7:
                    return Read16(_banks[4]);

                case 5:
                    return Read8(_scratch);
                case 6:
                    return Read16(_scratch);

                default:
                    throw new F7Exception($"Unknown memory bank {bank}/{offset}");
            }
        }
    }
}
