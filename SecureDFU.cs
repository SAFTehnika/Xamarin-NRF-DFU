// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.IO;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Plugin.BluetoothLE;

namespace Plugin.XamarinNordicDFU
{
    partial class DFU
    {
        /// <summary>
        /// Run actual filmware upgrade procedure (Secure DFU)
        /// </summary>
        /// <param name="device">Device which is switched to Secure DFU mode</param>
        /// <param name="FirmwareStream"></param>
        /// <param name="InitPacketStream"></param>
        /// <returns></returns>
        private async Task RunSecureDFU(IDevice device, Stream FirmwarePacket, Stream InitPacket)
        {
            IGattCharacteristic controlPoint;
            IGattCharacteristic packetPoint;

            await device.ConnectWait(new ConnectionConfig { AutoConnect = false }).Timeout(DeviceConnectionTimeout);

            // Important notice:
            // iOS doesn't provide API to request MTU and RequestMtu method just returns auto-negotiated value for CBCharacteristicWriteType.WithResponse
            // RequestMtu https://github.com/aritchie/bluetoothle/blob/ce41a59153c88654e64ff740c1e14460c64b55dd/Plugin.BluetoothLE/AbstractDevice.cs#L34
            // MtuSize https://github.com/aritchie/bluetoothle/blob/ce41a59153c88654e64ff740c1e14460c64b55dd/Plugin.BluetoothLE/Platforms/iOS/Device.cs#L12
            // On iOS 13 and above, MTU size is different for CBCharacteristicWriteType.WithResponse & CBCharacteristicWriteType.WithoutResponse.
            // Using the wrong MTU was actually the cause of the nasty bug, when sending data got stuck and ReadChecksum timeout occurred.
            await device.RequestMtu(256).Timeout(OperationTimeout);

            controlPoint = await device.GetKnownCharacteristics(DfuService, SecureDFUControlPointCharacteristic).Timeout(OperationTimeout);
            packetPoint = await device.GetKnownCharacteristics(DfuService, SecureDFUPacketCharacteristic).Timeout(OperationTimeout);
            await controlPoint.EnableNotifications(true);
            try
            {
                await SendInitPacket(device, InitPacket, controlPoint, packetPoint);
                await SendFirmware(device, FirmwarePacket, controlPoint, packetPoint);
            }
            catch (Exception ex)
            {
                Debug.WriteLineIf(LogLevelDebug, ex);
                throw;
            }
            finally
            {
                await Cleanup(controlPoint, device);
            }
        }

        /// <summary>
        /// Close connections, try to reenter standart mode, unsubscribe notifications
        /// </summary>
        /// <returns></returns>
        private async Task Cleanup(IGattCharacteristic controlPoint, IDevice device)
        {
            await controlPoint.DisableNotifications();
            device.CancelConnection();
        }

        /// <summary>
        /// Upload Init package (*.dat)
        /// </summary>
        /// <param name="device">Device in DFU mode</param>
        /// <param name="InitFilePath">Init package path (*.dat)</param>
        /// <param name="controlPoint">Secure DFU control point characteristic [Commands / Notifications]</param>
        /// <param name="packetPoint">Secure DFU packet point characteristic [Data of images]</param>
        /// <returns></returns>
        private async Task SendInitPacket(IDevice device, Stream InitPacket, IGattCharacteristic controlPoint, IGattCharacteristic packetPoint)
        {
            DFUEvents.OnLogMessage?.Invoke("Transfer of an init packet");
            Debug.WriteLineIf(LogLevelDebug, "Start of init packet send");

            var file = InitPacket;
            int imageSize = (int)file.Length;// Around ?~ 130bytes
            int MTU = Math.Min(device.MtuSize, DFUMaximumMTU);
            CRC32 crc = new CRC32();

            ObjectInfo info = await SelectCommand(controlPoint, SecureDFUObjectType.NRF_DFU_OBJ_TYPE_COMMAND);

            bool resumeSendingInitPacket = false;

            if (info.offset > 0 && info.offset <= imageSize)
            {
                // Check if remote sent content is valid
                byte[] buffer = new byte[info.offset];

                file.Seek(0, SeekOrigin.Begin);
                file.Read(buffer, 0, (int)info.offset);

                crc.Update(buffer);

                if (crc.Value == (uint)info.CRC32)
                {
                    if (info.offset == imageSize)
                    {
                        Debug.WriteLineIf(LogLevelDebug, "Init packet already sent and valid");
                        await ExecuteCommand(controlPoint);
                        Debug.WriteLineIf(LogLevelDebug, "End of init packet send");
                        return;
                    }
                    else
                    {
                        Debug.WriteLineIf(LogLevelDebug, String.Format("-> " + info.offset + " bytes of Init packet were sent before"));
                        resumeSendingInitPacket = true;
                        Debug.WriteLineIf(LogLevelDebug, String.Format("Resuming sending Init packet..."));
                    }
                }
                else
                {
                    crc.Reset();
                    info.offset = 0;
                }
            }
            await SetPRN(controlPoint, 0);

            for (int attempt = 1; attempt <= MaxRetries;)
            {
                if (!resumeSendingInitPacket)
                {
                    // Allocate new object
                    await CreateCommand(controlPoint, SecureDFUObjectType.NRF_DFU_OBJ_TYPE_COMMAND, imageSize);
                }
                await TransferData(packetPoint, crc, file, offsetStart: info.offset, MTU: MTU, offsetEnd: imageSize);

                ObjectChecksum check = await ReadChecksum(controlPoint);
                info.offset = check.offset;
                info.CRC32 = check.offset;
                Debug.WriteLineIf(LogLevelDebug, String.Format("Checksum received (Offset = {0}, CRC = {1})", check.offset, check.CRC32));

                uint localcrc = (uint)crc.Value;
                uint remotecrc = (uint)check.CRC32;
                if (localcrc == remotecrc)
                {
                    // Everything is OK, we can proceed
                    break;
                }
                else
                {
                    if (attempt < MaxRetries)
                    {
                        attempt++;
                        Debug.WriteLineIf(LogLevelDebug, String.Format("CRC does not match! Retrying...(" + attempt + "/" + MaxRetries + ")"));

                        // Restart
                        resumeSendingInitPacket = false;
                        info.offset = 0;
                        info.CRC32 = 0;
                        file.Seek(0, SeekOrigin.Begin);
                        crc.Reset();
                    }
                    else
                    {
                        Debug.WriteLineIf(LogLevelDebug, String.Format("CRC does not match!"));
                        device.CancelConnection();
                        return;
                    }
                }
            }
            await ExecuteCommand(controlPoint);
            Debug.WriteLineIf(LogLevelDebug, "End of init packet send");
        }

        /// <summary>
        /// Upload Firmware image (*.bin)
        /// </summary>
        /// <param name="device">Device in DFU mode</param>
        /// <param name="FirmwareFilePath">Fimware path (.bin)</param>
        /// <param name="controlPoint">Secure DFU control point characteristic [Commands / Notifications]</param>
        /// <param name="packetPoint">Secure DFU packet point characteristic [Data of images]</param>
        /// <returns></returns>
        private async Task SendFirmware(IDevice device, Stream FirmwarePacket, IGattCharacteristic controlPoint, IGattCharacteristic packetPoint)
        {
            DFUEvents.OnLogMessage?.Invoke("Transfer of a firmware image");

            var file = FirmwarePacket;
            long firmwareSize = file.Length;
            int MTU = Math.Min(device.MtuSize, DFUMaximumMTU);

            // A workaround to get correct MTU size on iOS for CBCharacteristicWriteType.WithoutResponse
            if (Xamarin.Forms.Device.RuntimePlatform == Xamarin.Forms.Device.iOS)
            {
                Debug.WriteLineIf(LogLevelDebug, "MTU iOS workaround");
                try
                {
                    PropertyInfo peripheralPropInfo = packetPoint.GetType().GetProperty("Peripheral", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    object peripheral = peripheralPropInfo.GetValue(packetPoint, null); // CoreBluetooth.CBPeripheral

                    MethodInfo mtuMethodInfo = peripheral.GetType().GetMethod("GetMaximumWriteValueLength");
                    object[] parameters = new object[] { 1 }; // 1 = CBCharacteristicWriteType.WithoutResponse
                    object result = mtuMethodInfo.Invoke(peripheral, parameters);
                    Debug.WriteLineIf(LogLevelDebug, $"peripheral.GetMaximumWriteValueLength(CBCharacteristicWriteType.WithoutResponse)={result}");

                    // The actual MTU size is 3 bytes more since that's the overhead for ATT (1 byte for opcode + 2 bytes for handle ID).
                    int intResult = Convert.ToInt32(result); // nuint => int
                    intResult += 3;

                    MTU = Math.Min(intResult, DFUMaximumMTU);
                }
                catch (Exception ex)
                {
                    Debug.WriteLineIf(LogLevelDebug, "MTU iOS workaround Ex: " + ex);
                    DFUEvents.OnLogMessage?.Invoke("MTU iOS workaround Ex: " + ex.Message);
                }
            }

            Debug.WriteLineIf(LogLevelDebug, $"device.MtuSize = {device.MtuSize}, DFUMaximumMTU = {DFUMaximumMTU}, MTU = {MTU}");

            await SetPRN(controlPoint, 0); // Packet receipt notification
            /*
            // PRN is not currently in use, but can be helpful as a dirty workaround for limiting data transfer rate on some mobile devices:
            // https://github.com/NordicSemiconductor/IOS-Pods-DFU-Library/issues/308#issuecomment-495106243
            // https://github.com/NordicSemiconductor/Android-DFU-Library/issues/111
            // https://github.com/Pilloxa/react-native-nordic-dfu/issues/113
            using var notificationSubscription = controlPoint.WhenNotificationReceived().Subscribe(result =>
            {
                Debug.WriteLineIf(LogLevelDebug, $"Notification {result.Data.ToHexString()}");
                if (result.Data.Length == 11)
                {
                    var checksum = new ObjectChecksum();
                    SetChecksum(checksum, result.Data);
                    Debug.WriteLineIf(LogLevelDebug, $"{DateTime.Now:HH:mm:ss.fff} ::: PRN response crc: {checksum.CRC32}, offset: {checksum.offset}");
                }
            });
            /* */

            ObjectInfo info = await SelectCommand(controlPoint, SecureDFUObjectType.NRF_DFU_OBJ_TYPE_DATA);
            int objectSize = info.maxSize;// Use maximum available object size

            Debug.WriteLineIf(LogLevelDebug, String.Format("Data object info received (Max size = {0}, Offset = {1}, CRC = {2})", objectSize, info.offset, info.CRC32));

            CRC32 crc = new CRC32();

            // Try to allocate first object
            int startAllocatedSize = (int)(firmwareSize - info.offset);
            startAllocatedSize = Math.Min(startAllocatedSize, objectSize);
            if (startAllocatedSize > 0)
            {
                await CreateCommand(controlPoint, SecureDFUObjectType.NRF_DFU_OBJ_TYPE_DATA, startAllocatedSize);
                await Task.Delay(400); // nRF Connect on iOS performs wait(400) after first Create request
            }

            var LastOffsetFailed = 0;
            var LastOffsetFailCount = 0;
            // Run till all objects are transferred, object sizes must be page aligned
            while (true)
            {
                int endOffset = GetCurrentObjectEnd(info.offset, objectSize, firmwareSize);
                int objectOffset = info.offset;

                // Send single current object
                for (int startoffset = objectOffset; ;)
                {
                    if (startoffset < objectOffset)
                    {
                        if (objectOffset % objectSize == 0 || objectOffset == firmwareSize)
                        {
                            break;
                        }
                    }
                    int bytesWritten = await TransferData(packetPoint, crc, file, offsetStart: objectOffset, offsetEnd: endOffset, MTU: MTU);
                    objectOffset += bytesWritten;
                    DFUEvents.OnFimwareProgressChanged?.Invoke(objectOffset / (float)firmwareSize, DateTime.Now - DFUStartTime);

                    Debug.WriteLineIf(LogLevelDebug, String.Format("{0} ::: Written bytes {1}, progress: {2}, elapsed: {3}", DateTime.Now.ToString("HH:mm:ss.ffffff"), bytesWritten, objectOffset / (float)firmwareSize, DateTime.Now - DFUStartTime));
                }
                // if PRN with correct offset not received, force to calculate CRC and offset
                if (info.offset != objectOffset)
                {
                    Debug.WriteLineIf(LogLevelDebug, String.Format("{0} ::: Force chekcsum calc", DateTime.Now.ToString("HH:mm:ss.ffffff")));
                    ObjectChecksum check = await ReadChecksum(controlPoint);
                    info.CRC32 = check.CRC32;
                    info.offset = check.offset;
                }

                uint localcrc = (uint)crc.Value;
                uint remotecrc = (uint)info.CRC32;
                if (localcrc == remotecrc)
                {
                    await ExecuteCommand(controlPoint, skipRegistring: true);
                    if (firmwareSize == info.offset)
                    {
                        // Firmware upload finished
                        break;
                    }

                    // Allocate next object
                    int allocateSize = objectSize;
                    if (info.offset + objectSize > firmwareSize)
                    {
                        allocateSize = (int)(firmwareSize - info.offset);
                    }
                    await CreateCommand(controlPoint, SecureDFUObjectType.NRF_DFU_OBJ_TYPE_DATA, allocateSize);
                }
                else
                {
                    Debug.WriteLineIf(LogLevelDebug, $"localcrc != remotecrc {localcrc} {remotecrc}");

                    await Task.Delay(1000);
                    // Get current object start offset
                    int allocateSize = objectSize;
                    int currentObject = info.offset / objectSize;
                    int currentStartOffset = currentObject * objectSize;
                    if (currentStartOffset + objectSize > firmwareSize)
                    {
                        allocateSize = (int)(firmwareSize - currentStartOffset);
                    }
                    info.offset = currentStartOffset;

                    // Calculate crc of already sent data
                    file.Seek(0, SeekOrigin.Begin);
                    byte[] crcBuffer = new byte[currentStartOffset];
                    crc.Reset();
                    if (currentStartOffset > 0)
                    {
                        file.Read(crcBuffer, 0, crcBuffer.Length);
                        crc.Update(crcBuffer);
                    }
                    if (LastOffsetFailed != currentStartOffset)
                    {
                        LastOffsetFailed = currentStartOffset;
                        LastOffsetFailCount = 0;
                    }
                    LastOffsetFailCount++;
                    if (LastOffsetFailCount >= MaxRetries)
                    {
                        throw new Exception("Too much retries for one object");
                    }
                    // Allocate memory from current object start position
                    await CreateCommand(controlPoint, SecureDFUObjectType.NRF_DFU_OBJ_TYPE_DATA, allocateSize);
                }
            }
        }
    }
}
