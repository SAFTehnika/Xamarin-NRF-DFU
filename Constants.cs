// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
namespace Plugin.XamarinNordicDFU
{
    partial class DFU
    {
        /// <summary>
        /// iOS delays MTU negotiation, so it is around 512 which is not supported for dfu
        /// </summary>
        private const int DFUMaximumMTU = 247;
        private const int MaxRetries = 3;

        #region Services and Characteristics
        private readonly Guid DfuService =                              new Guid("0000FE59-0000-1000-8000-00805F9B34FB");
        private readonly Guid DfuButtonlessCharacteristicWithoutBonds = new Guid("8EC90003-F315-4F60-9FB8-838830DAEA50");
        private readonly Guid SecureDFUControlPointCharacteristic =     new Guid("8EC90001-F315-4F60-9FB8-838830DAEA50");
        private readonly Guid SecureDFUPacketCharacteristic =           new Guid("8EC90002-F315-4F60-9FB8-838830DAEA50");
        #endregion


        #region Partial Commands
        private const byte CCreateObject = 0x01;
        private const byte CSetPRN = 0x02;
        private const byte SelectCommandPrefix = 0x06;
        private const byte CChangeAdvertisedName = 0x02;
        #endregion

        private enum SecureDFUCreateCommandType
        {
            /// <summary>
            /// Init packet
            /// </summary>
            CommmandObject = 0x01,
            /// <summary>
            /// Firmware
            /// </summary>
            DataObject = 0x02
        }
        private enum SecureDFUSelectCommandType
        {
            /// <summary>
            /// Init packet
            /// </summary>
            CommmandObject = 0x01,
            /// <summary>
            /// Firmware
            /// </summary>
            DataObject = 0x02
        }

        #region Commands
        private readonly byte[] CEnterDFU =         new byte[] { 0x01 };
        private readonly byte[] CExecute =          new byte[] { 0x04 };
        private readonly byte[] CCalculateCRC =     new byte[] { 0x03 };
        //private readonly byte[] CSelectCommandInitPacket =          new byte[] { SelectCommandPrefix, 0x01 };
        //private readonly byte[] CSelectCommandFirmwarePacket =      new byte[] { SelectCommandPrefix, 0x02 };
        #endregion


        #region Status codes
        private readonly byte ButtonlessSwitchSuccessCode = 0x01;
        private readonly byte SecureDFUCommandSuccess = 0x01;
        #endregion

        #region Timeouts
        TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
        TimeSpan DeviceRestartTimeout = TimeSpan.FromSeconds(10);
        TimeSpan DeviceConnectionTimeout = TimeSpan.FromSeconds(10);
        #endregion
    }
}
