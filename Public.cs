// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading.Tasks;
using Plugin.BluetoothLE;
using System.Diagnostics;
namespace Plugin.XamarinNordicDFU
{
    class ObjectInfo : ObjectChecksum
    {
        public int maxSize;
    }
    class ObjectChecksum
    {
        public int offset;
        public int CRC32;
    }
    public enum ExtendedErrors
    {
        NRF_DFU_EXT_ERROR_NO_ERROR = 0x00, //No extended error code has been set. This error indicates an implementation problem.
        NRF_DFU_EXT_ERROR_INVALID_ERROR_CODE = 0x01,//Invalid error code. This error code should never be used outside of development.
        NRF_DFU_EXT_ERROR_WRONG_COMMAND_FORMAT = 0x02, //The format of the command was incorrect. This error code is not used in the current implementation, because NRF_DFU_RES_CODE_OP_CODE_NOT_SUPPORTED and NRF_DFU_RES_CODE_INVALID_PARAMETER cover all possible format errors.
        NRF_DFU_EXT_ERROR_UNKNOWN_COMMAND = 0x03, //The command was successfully parsed, but it is not supported or unknown.
        NRF_DFU_EXT_ERROR_INIT_COMMAND_INVALID = 0x04, //The init command is invalid. The init packet either has an invalid update type or it is missing required fields for the update type (for example, the init packet for a SoftDevice update is missing the SoftDevice size field).
        NRF_DFU_EXT_ERROR_FW_VERSION_FAILURE = 0x05, //The firmware version is too low.For an application, the version must be greater than the current application.For a bootloader, it must be greater than or equal to the current version.This requirement prevents downgrade attacks.
        NRF_DFU_EXT_ERROR_HW_VERSION_FAILURE = 0x06, //The hardware version of the device does not match the required hardware version for the update.
        NRF_DFU_EXT_ERROR_SD_VERSION_FAILURE = 0x07, //The array of supported SoftDevices for the update does not contain the FWID of the current SoftDevice.
        NRF_DFU_EXT_ERROR_SIGNATURE_MISSING = 0x08, //The init packet does not contain a signature.This error code is not used in the current implementation, because init packets without a signature are regarded as invalid.
        NRF_DFU_EXT_ERROR_WRONG_HASH_TYPE = 0x09, //The hash type that is specified by the init packet is not supported by the DFU bootloader.
        NRF_DFU_EXT_ERROR_HASH_FAILED = 0x0A, //The hash of the firmware image cannot be calculated.
        NRF_DFU_EXT_ERROR_WRONG_SIGNATURE_TYPE = 0x0B,//The type of the signature is unknown or not supported by the DFU bootloader.
        NRF_DFU_EXT_ERROR_VERIFICATION_FAILED = 0x0C, //The hash of the received firmware image does not match the hash in the init packet.
        NRF_DFU_EXT_ERROR_INSUFFICIENT_SPACE = 0x0D //The available space on the device is insufficient to hold the firmware.
    }
    public enum ResponseErrors
    {
        NRF_DFU_RES_CODE_INVALID = 0x00,//Invalid opcode.
        NRF_DFU_RES_CODE_SUCCESS = 0x01,//Operation successful.
        NRF_DFU_RES_CODE_OP_CODE_NOT_SUPPORTED = 0x02,//Opcode not supported.
        NRF_DFU_RES_CODE_INVALID_PARAMETER = 0x03,//Missing or invalid parameter value.
        NRF_DFU_RES_CODE_INSUFFICIENT_RESOURCES = 0x04,//Not enough memory for the data object.
        NRF_DFU_RES_CODE_INVALID_OBJECT = 0x05,//Data object does not match the firmware and hardware requirements, the signature is wrong, or parsing the command failed.
        NRF_DFU_RES_CODE_UNSUPPORTED_TYPE = 0x07,//Not a valid object type for a Create request.
        NRF_DFU_RES_CODE_OPERATION_NOT_PERMITTED = 0x08,//The state of the DFU process does not allow this operation.
        NRF_DFU_RES_CODE_OPERATION_FAILED = 0x0A,//Operation failed.
        NRF_DFU_RES_CODE_EXT_ERROR = 0x0B,//Extended error.The next byte of the response contains the error code of the extended error(see nrf_dfu_ext_error_code_t).
    }
    public enum GlobalErrors
    {
        FILE_STREAMS_NOT_SUPPLIED = 0x00
    }
    partial class DFU
    {
        

        /// <summary>
        /// Debug logs in Debug configuration enabled or disabled
        /// </summary>
        private bool LogLevelDebug = false;

        private DateTime DFUStartTime;
        public DFU(bool logLevelDebug = false)
        {
            LogLevelDebug = logLevelDebug;
        }
        public async Task Start(IDevice device, Stream FirmwarePacket, Stream InitPacket)
        {
            DFUStartTime = DateTime.Now;
            IDevice newDevice = null;
            try
            {
                if (FirmwarePacket == null || InitPacket == null)
                {
                    throw new Exception(GlobalErrors.FILE_STREAMS_NOT_SUPPLIED.ToString());
                }
                newDevice = await ButtonlessDFUWithoutBondsToSecureDFU(device);

                // Run firmware upgrade when device is switched to secure dfu mode
                await RunSecureDFU(newDevice, FirmwarePacket, InitPacket);
                DFUEvents.OnSuccess?.Invoke(DateTime.Now - DFUStartTime);
            }
            catch(Exception ex)
            {
                DFUEvents.OnError?.Invoke(ex.ToString());
                Debug.WriteLineIf(LogLevelDebug, ex.StackTrace);
                
                newDevice?.CancelConnection();
                device?.CancelConnection();
            }
        }
    }
}
