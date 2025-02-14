﻿// This program and the accompanying materials are made available under the terms of the
//  Eclipse Public License v2.0 which accompanies this distribution, and is available at
//  https://www.eclipse.org/legal/epl-v20.html
//  
//  SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ficedula.FF7 {
    public class FFException : Exception {
        public FFException(string msg) : base(msg) { }
    }

    public static class Util {
        public static T? ValueOrNull<T>(T value, T nullValue) where T : struct {
            return value.Equals(nullValue) ? null : value;
        }

        public static uint BSwap(uint i) {
            return (i & 0xff00ff00) | ((i & 0xff) << 16) | ((i >> 16) & 0xff);
        }

        public static int IndexOf<T>(this IReadOnlyList<T> list, T value) where T : class {
            foreach (int i in Enumerable.Range(0, list.Count))
                if (list[i] == value)
                    return i;
            return -1;
        }

    }
}
