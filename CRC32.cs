// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Text;

namespace Plugin.XamarinNordicDFU
{
    public class CRC32
    {
        public uint Value { get; private set; }
        private const uint polinomial = 0xEDB88320U;
        public CRC32()
        {
            this.Reset();
        }
        public void Update(byte[] p_data, uint size)
        {
            Value = ~(Value);
            for (uint i = 0; i < size; i++)
            {
                Value = Value ^ p_data[i];
                for (uint j = 8; j > 0; j--)
                {
                    Value = (Value >> 1) ^ (polinomial & ((Value & 1) > 0 ? 0xFFFFFFFF : 0));
                }
            }
            Value = ~Value;
        }
        public void Update(byte[] data)
        {
            this.Update(data, (uint)data.Length);
        }
        public void Reset()
        {
            Value = 0x0;
        }
    }
}
