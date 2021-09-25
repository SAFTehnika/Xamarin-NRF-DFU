// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Plugin.BluetoothLE;

namespace Plugin.XamarinNordicDFU
{
    partial class DFU
    {
        /// <summary>
        /// Assert that commmand executed successfully and raise error if not
        /// </summary>
        /// <param name="result"></param>
        private void AssertSuccess(CharacteristicGattResult result)
        {
            /*
            * The response received from the DFU device contains:
            * +---------+--------+----------------------------------------------------+
            * | byte no | value  | description                                        |
            * +---------+--------+----------------------------------------------------+
            * | 0       | 0x60   | Response code                                      |
            * | 1       | 0x06   | The Op Code of a request that this response is for |
            * | 2       | STATUS | Status code                                        |
            * | ...     | ...    | ...                                                |
            * +---------+--------+----------------------------------------------------+
            */
            if (result.Data[2] != (byte)DFUOperationResultCode.NRF_DFU_RES_CODE_SUCCESS)
            {
                InvokeError(result);
                throw new Exception($"Received non-success response: {result.Data.ToHexString()}");
            }
        }

        /// <summary>
        /// Exit DFU mode
        /// </summary>
        /// <param name="controlPoint"></param>
        /// <returns></returns>
        private async Task ExitSecureDFU(IGattCharacteristic controlPoint)
        {
            try
            {
                await controlPoint.Write(new byte[]
                {
                    (byte)SecureDFUOpCode.NRF_DFU_OP_ABORT
                });
            }
            catch (BleException)
            {
                // "Failed to write characteristic" will occur,
                // because the bootloader resets on that message
                // and dosn't send write response.
            }
        }

        /// <summary>
        /// Invoke error from BLE response
        /// </summary>
        /// <param name="response"></param>
        private void InvokeError(CharacteristicGattResult result)
        {
            var error = (DFUOperationResultCode)result.Data[2];
            if (error == DFUOperationResultCode.NRF_DFU_RES_CODE_EXT_ERROR)
            {
                var extError = (DFUOperationExtendedErrorCode)result.Data[3];
                DFUEvents.OnExtendedError?.Invoke(extError);
            }
            else
            {
                DFUEvents.OnResponseError?.Invoke(error);
            }
        }

        /// <summary>
        /// Sets the UINT16 value in data converted to LSB at data offset
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        private void SetUint16LSB(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        /// <summary>
        /// Sets the UINT32 value in data converted to LSB at data offset
        /// </summary>
        /// <param name="data"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        private void SetUInt32LSB(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        /// <summary>
        /// Set Packet Receipt Notification (PRN) value.
        /// Sets the number of packets to be sent before receiving a Packet Receipt Notification. The default is 0.
        /// </summary>
        /// <param name="device"></param>
        /// <param name="prn"></param>
        /// <returns></returns>
        private async Task SetPRN(IGattCharacteristic controlPoint, int prn)
        {
            // PRN value should be LSB
            byte[] data = new byte[] {
                (byte)SecureDFUOpCode.NRF_DFU_OP_RECEIPT_NOTIF_SET,
                0,
                0
            };
            SetUint16LSB(data, 1, prn);

            var notif = GetTimedNotification(controlPoint);
            await controlPoint.Write(data).Timeout(OperationTimeout);

            var result = await notif.Task;

            Debug.WriteLineIf(LogLevelDebug, $"Received PRN set response: {result.Data.ToHexString()}");

            AssertSuccess(result);
        }

        /// <summary>
        /// Creates an object with the given type and selects it. Removes an old object of the same type (if such an object exists).
        /// </summary>
        /// <param name="device"></param>
        /// <param name="objType"></param>
        /// <param name="size"></param>
        /// <returns></returns>
        private async Task CreateCommand(IGattCharacteristic controlPoint, SecureDFUObjectType objType, int size)
        {
            byte[] data = new byte[]{
                (byte)SecureDFUOpCode.NRF_DFU_OP_OBJECT_CREATE,
                (byte)objType,
                0,
                0,
                0,
                0
            };
            SetUInt32LSB(data, 2, size);

            var notif = GetTimedNotification(controlPoint);
            await controlPoint.Write(data).Timeout(OperationTimeout);
            var result = await notif.Task;

            AssertSuccess(result);

            Debug.WriteLineIf(LogLevelDebug, $"Command object created {result.Data.ToHexString()}");
        }

        /// <summary>
        /// Select command before starting to upload something
        /// </summary>
        /// <param name="device"></param>
        /// <param name="objType"></param>
        /// <returns></returns>
        private async Task<ObjectInfo> SelectCommand(IGattCharacteristic controlPoint, SecureDFUObjectType objType)
        {
            ObjectInfo info = new ObjectInfo();

            var notif = GetTimedNotification(controlPoint);

            await controlPoint.Write(new byte[]
            {
                (byte)SecureDFUOpCode.NRF_DFU_OP_OBJECT_SELECT,
                (byte)objType
            })
            .Timeout(OperationTimeout);

            var result = await notif.Task;
            byte[] response = result.Data;

            AssertSuccess(result);

            info.maxSize = (int)BitConverter.ToUInt32(response, 3);
            info.offset = (int)BitConverter.ToUInt32(response, 3 + 4);
            info.CRC32 = (int)BitConverter.ToUInt32(response, 3 + 8);

            return info;
        }

        /// <summary>
        /// Sets checksum from (Response PRN) or (Response Calculate CRC) responses
        /// </summary>
        /// <param name="checksum"></param>
        /// <param name="data"></param>
        private void SetChecksum(ObjectChecksum checksum, byte[] data)
        {
            checksum.offset = (int)BitConverter.ToUInt32(data, 3);
            checksum.CRC32 = (int)BitConverter.ToUInt32(data, 3 + 4);
        }

        /// <summary>
        /// Request and read DFU checksum from control point after data have been sent
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        private async Task<ObjectChecksum> ReadChecksum(IGattCharacteristic controlPoint)
        {
            Debug.WriteLineIf(LogLevelDebug, "Begin read checksum");
            ObjectChecksum checksum = new ObjectChecksum();

            CharacteristicGattResult result = null;
            for (var retry = 0; retry < MaxRetries; retry++)
            {
                try
                {
                    // Request checksum
                    var notif = GetTimedNotification(controlPoint);
                    var re = await controlPoint.Write(new byte[]
                    {
                        (byte)SecureDFUOpCode.NRF_DFU_OP_CRC_GET
                    })
                    .Timeout(OperationTimeout);

                    result = await notif.Task;
                    break;
                }
                catch (Exception ex)
                {
                    Debug.WriteLineIf(LogLevelDebug, ex);
                    await Task.Delay(100);
                }
                finally
                {
                    if (retry >= MaxRetries)
                    {
                        throw new TimeoutException();
                    }
                }
            }

            Debug.WriteLineIf(LogLevelDebug, $"Check Sum Response {result.Data.ToHexString()}");

            AssertSuccess(result);

            SetChecksum(checksum, result.Data);

            Debug.WriteLineIf(LogLevelDebug, "End read checksum");
            return checksum;
        }

        /// <summary>
        /// Write data to data point characteristic
        /// </summary>
        /// <param name="crc32"></param>
        /// <param name="file"></param>
        /// <param name="device"></param>
        /// <param name="MTU"></param>
        /// <param name="transferBytes"></param>
        /// <param name="remoteOffset"></param>
        /// <returns></returns>
        private async Task<int> TransferData(IGattCharacteristic packetPoint, CRC32 crc, Stream file, /*int prn = -1,*/ int offsetStart = 0, int offsetEnd = -1, int MTU = 20)
        {
            const int RESERVED_BYTES = 3;
            int bytesWritten = 0;
            try
            {
                int MAX_READ_BYTES = MTU - RESERVED_BYTES;
                Debug.WriteLineIf(LogLevelDebug, $"Start of data transfer flen: {file.Length}");
                file.Seek(offsetStart, SeekOrigin.Begin);

                byte[] data = new byte[MAX_READ_BYTES];

                for (int packetsSent = 0, offset = offsetStart; ;)
                {
                    int bytesToRead = MAX_READ_BYTES;
                    if (offsetEnd > -1)
                    {
                        int endOffsetDiff = offsetEnd - offset;
                        if (endOffsetDiff <= 0)
                        {
                            break;
                        }
                        bytesToRead = Math.Min(bytesToRead, endOffsetDiff);
                    }
                    Debug.WriteLineIf(LogLevelDebug, $"Reading {bytesToRead} bytes from file stream");
                    int size = file.Read(data, 0, bytesToRead);
                    if (size <= 0)
                    {
                        break;
                    }
                    offset += size;
                    byte[] pending;

                    if (size < MAX_READ_BYTES)
                    {
                        pending = new byte[size];
                        Array.Copy(data, pending, size);
                    }
                    else
                    {
                        pending = data;
                    }

                    // Specific for iOS, if no timeout, then sending happens to be blocked
                    await Task.Delay(10);

                    await packetPoint.WriteWithoutResponse(pending).Timeout(OperationTimeout);

                    packetsSent++;
                    bytesWritten += pending.Length;

                    crc.Update(pending);
                    Debug.WriteLineIf(LogLevelDebug, $"### CRC32 Step value, crc: {crc.Value} offset: {offset} size: {pending.Length} packetLocalNo: {packetsSent}");
                }
                Debug.WriteLineIf(LogLevelDebug, "End of data transfer");
            }
            catch (Exception ex)
            {
                Debug.WriteLineIf(LogLevelDebug, "Error while transfering data");
                Debug.Write(ex);
            }
            return bytesWritten;
        }

        /// <summary>
        /// Run "Execute" command when all data is transfered
        /// </summary>
        /// <param name="device"></param>
        /// <returns></returns>
        private async Task ExecuteCommand(IGattCharacteristic controlPoint, bool skipRegistring = false)
        {
            var notif = GetTimedNotification(controlPoint);
            await controlPoint.Write(new byte[]
            {
                (byte)SecureDFUOpCode.NRF_DFU_OP_OBJECT_EXECUTE
            })
            .Timeout(OperationTimeout);

            var result = await notif.Task;

            Debug.WriteLineIf(LogLevelDebug, $"Execute command result {result.Data.ToHexString()}");
            AssertSuccess(result);
        }

        /// <summary>
        /// Gets current object end offset
        /// </summary>
        /// <param name="offset"></param>
        /// <param name="objectSize"></param>
        /// <param name="firmwareSize"></param>
        /// <returns></returns>
        private int GetCurrentObjectEnd(int offset, int objectSize, long firmwareSize)
        {
            int offsetEnd;
            int objectNo = offset / objectSize;

            offsetEnd = objectSize * objectNo + objectSize;
            if (offsetEnd < firmwareSize)
            {
                return offsetEnd;
            }
            return (int)firmwareSize;
        }

        /// <summary>
        /// Get async notification with timeout
        /// </summary>
        /// <param name="characteristic"></param>
        /// <returns></returns>
        private TaskCompletionSource<CharacteristicGattResult> GetTimedNotification(IGattCharacteristic characteristic)
        {
            var tcs = new TaskCompletionSource<CharacteristicGattResult>();

            bool notificationReceived = false;
            IDisposable disposable = null;
            object locked = new object();
            Task task = Task.Delay(OperationTimeout).ContinueWith(t =>
            {
                lock (locked)
                {
                    if (!notificationReceived)
                    {
                        disposable?.Dispose();
                        tcs.TrySetException(new TimeoutException());
                    }
                }
            });

            disposable = characteristic.WhenNotificationReceived().Subscribe(result =>
            {
                lock (locked)
                {
                    notificationReceived = true;
                }
                disposable?.Dispose();
                tcs.TrySetResult(result);
            });
            return tcs;
        }
    }

    public static class ByteExtensions
    {
        public static string ToHexString(this byte[] buffer)
        {
            return (buffer != null) ? BitConverter.ToString(buffer) : "Empty";
        }
    }
}
