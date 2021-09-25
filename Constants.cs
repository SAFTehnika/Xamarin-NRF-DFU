// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;

namespace Plugin.XamarinNordicDFU
{
    partial class DFU
    {
        private const int DFUMaximumMTU = 247;
        private const int MaxRetries = 3;

        private TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
        private TimeSpan DeviceRestartTimeout = TimeSpan.FromSeconds(10);
        private TimeSpan DeviceConnectionTimeout = TimeSpan.FromSeconds(10);

        // Services and Characteristics
        private readonly Guid DfuService =                              new Guid("0000FE59-0000-1000-8000-00805F9B34FB");
        private readonly Guid DfuButtonlessCharacteristicWithoutBonds = new Guid("8EC90003-F315-4F60-9FB8-838830DAEA50");
        private readonly Guid SecureDFUControlPointCharacteristic =     new Guid("8EC90001-F315-4F60-9FB8-838830DAEA50");
        private readonly Guid SecureDFUPacketCharacteristic =           new Guid("8EC90002-F315-4F60-9FB8-838830DAEA50");

        /// <summary>
        /// Enumeration of Bootloader DFU Operation codes.
        /// nRF5_SDK => ble_dfu_buttonless_op_code_t
        /// </summary>
        private enum ButtonlessDFUOpCode : byte
        {
            DFU_OP_RESERVED                 = 0x00, /**< Reserved for future use. */
            DFU_OP_ENTER_BOOTLOADER         = 0x01, /**< Enter bootloader. */
            DFU_OP_SET_ADV_NAME             = 0x02, /**< Set advertisement name to use in DFU mode. */
            DFU_OP_RESPONSE_CODE            = 0x20  /**< Response code. */
        }

        /// <summary>
        /// Enumeration of Bootloader DFU response codes.
        /// nRF5_SDK => ble_dfu_buttonless_rsp_code_t
        /// </summary>
        private enum ButtonlessDFUResponseCode : byte
        {
            DFU_RSP_INVALID                 = 0x00, /**< Invalid op code. */
            DFU_RSP_SUCCESS                 = 0x01, /**< Success. */
            DFU_RSP_OP_CODE_NOT_SUPPORTED   = 0x02, /**< Op code not supported. */
            DFU_RSP_OPERATION_FAILED        = 0x04, /**< Operation failed. */
            DFU_RSP_ADV_NAME_INVALID        = 0x05, /**< Requested advertisement name is too short or too long. */
            DFU_RSP_BUSY                    = 0x06, /**< Ongoing async operation. */
            DFU_RSP_NOT_BONDED              = 0x07, /**< Buttonless unavailable due to device not bonded. */
        }

        /// <summary>
        /// DFU object types.
        /// nRF5_SDK => nrf_dfu_obj_type_t
        /// </summary>
        private enum SecureDFUObjectType : byte
        {
            NRF_DFU_OBJ_TYPE_INVALID        = 0x00, //!< Invalid object type.
            NRF_DFU_OBJ_TYPE_COMMAND        = 0x01, //!< Command object.
            NRF_DFU_OBJ_TYPE_DATA           = 0x02, //!< Data object.
        }

        /// <summary>
        /// DFU protocol operation.
        /// nRF5_SDK => nrf_dfu_op_t
        /// </summary>
        private enum SecureDFUOpCode : byte
        {
            NRF_DFU_OP_PROTOCOL_VERSION     = 0x00, //!< Retrieve protocol version.
            NRF_DFU_OP_OBJECT_CREATE        = 0x01, //!< Create selected object.
            NRF_DFU_OP_RECEIPT_NOTIF_SET    = 0x02, //!< Set receipt notification.
            NRF_DFU_OP_CRC_GET              = 0x03, //!< Request CRC of selected object.
            NRF_DFU_OP_OBJECT_EXECUTE       = 0x04, //!< Execute selected object.
            NRF_DFU_OP_OBJECT_SELECT        = 0x06, //!< Select object.
            NRF_DFU_OP_MTU_GET              = 0x07, //!< Retrieve MTU size.
            NRF_DFU_OP_OBJECT_WRITE         = 0x08, //!< Write selected object.
            NRF_DFU_OP_PING                 = 0x09, //!< Ping.
            NRF_DFU_OP_HARDWARE_VERSION     = 0x0A, //!< Retrieve hardware version.
            NRF_DFU_OP_FIRMWARE_VERSION     = 0x0B, //!< Retrieve firmware version.
            NRF_DFU_OP_ABORT                = 0x0C, //!< Abort the DFU procedure.
            NRF_DFU_OP_RESPONSE             = 0x60, //!< Response.
            NRF_DFU_OP_INVALID              = 0xFF,
        }
    }
}
