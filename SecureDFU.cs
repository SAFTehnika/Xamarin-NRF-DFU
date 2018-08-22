// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Plugin.BluetoothLE;
using System.Diagnostics;
using System.Reactive.Linq;
using System.IO;
namespace Plugin.XamarinNordicDFU
{
    partial class DFU
    {
        /// <summary>
        /// Run actual filmware upgrade procedure (Secure DFU)
        /// </summary>
        /// <param name="device">Device which is switched to Secure DFU mode</param>
        /// <param name="FirmwarePath"></param>
        /// <param name="InitFilePath"></param>
        /// <returns></returns>
        private async Task RunSecureDFU(IDevice device, Stream FirmwarePacket, Stream InitPacket)
        {
            IGattCharacteristic controlPoint = null;
            IGattCharacteristic packetPoint = null;

            //await RefreshGattAsync(device);

            device.Connect();
            await device.ConnectWait().Timeout(DeviceConnectionTimeout);

            // Request MTU only once
            await device.RequestMtu(256).Timeout(OperationTimeout);

            controlPoint = await device.GetKnownCharacteristics(DfuService, SecureDFUControlPointCharacteristic).Timeout(OperationTimeout);
            packetPoint = await device.GetKnownCharacteristics(DfuService, SecureDFUPacketCharacteristic).Timeout(OperationTimeout);
            await controlPoint.EnableNotifications(true);
            try {
                await SendInitPacket(device, InitPacket, controlPoint, packetPoint);
                await SendFirmware(device, FirmwarePacket, controlPoint, packetPoint);
            }
            catch (Exception)
            {
                await Cleanup(controlPoint, device);
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
            Debug.WriteLineIf(LogLevelDebug, "Start of init packet send");
            //FileStream file = new FileStream(InitFilePath, FileMode.Open, FileAccess.Read);
            var file = InitPacket;
            int imageSize = (int)file.Length;// Around ?~ 130bytes 
            var MTU = Math.Min(device.MtuSize, DFUMaximumMTU);
            CRC32 crc = new CRC32();

            ObjectInfo info = await SelectCommand(controlPoint, SecureDFUSelectCommandType.CommmandObject);

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
                    await CreateCommand(controlPoint, SecureDFUCreateCommandType.CommmandObject, imageSize);
                }
                await TransferData(packetPoint, crc, file, offsetStart: info.offset, MTU:MTU, offsetEnd: imageSize);

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
            //FileStream file = new FileStream(FirmwareFilePath, FileMode.Open, FileAccess.Read);
            var file = FirmwarePacket;
            long firmwareSize = file.Length;
            var MTU = Math.Min(device.MtuSize, DFUMaximumMTU);
            const int prn = 0;

            ObjectInfo info = await SelectCommand(controlPoint, SecureDFUSelectCommandType.DataObject);
            int objectSize = info.maxSize;// Use maximum available object size
            
            Debug.WriteLineIf(LogLevelDebug, String.Format("Data object info received (Max size = {0}, Offset = {1}, CRC = {2})", objectSize, info.offset, info.CRC32));
            
            CRC32 crc = new CRC32();
            
            await SetPRN(controlPoint, prn);
            
            // Try to allocate first object
            int startAllocatedSize = (int)(firmwareSize - info.offset);
            startAllocatedSize = Math.Min(startAllocatedSize, objectSize);
            if (startAllocatedSize > 0)
            {
                await CreateCommand(controlPoint, SecureDFUCreateCommandType.DataObject, startAllocatedSize);
            }
            
            IDisposable dispose = null;
            var LastOffsetFailed = 0;
            var LastOffsetFailCount = 0;
            // Run till all objects are transferred, object sizes must be page aligned
            while (true)
            {
                byte[] lastData;
                dispose = controlPoint.WhenNotificationReceived().Subscribe(
                    result =>
                    {
                        lastData = result.Data;
                        Debug.WriteLineIf(LogLevelDebug, String.Format("Notification {0}", BitConverter.ToString(result.Data)));
                        if(result.Data.Length == 11)
                        {
                            ObjectChecksum checks = new ObjectChecksum();
                            SetChecksum(checks, result.Data);
                            info.offset = checks.offset;
                            info.CRC32 = checks.CRC32;
                            Debug.WriteLineIf(LogLevelDebug, String.Format("{0} ::: PRN response check: {1}, offset: {2}", DateTime.Now.ToString("HH:mm:ss.ffffff"), checks.CRC32, checks.offset));
                        }
                    }
                );

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
                if (info.offset != objectOffset) {
                    Debug.WriteLineIf(LogLevelDebug, String.Format("{0} ::: Force chekcsum calc", DateTime.Now.ToString("HH:mm:ss.ffffff")));
                    ObjectChecksum check = await ReadChecksum(controlPoint);
                    info.CRC32 = check.CRC32;
                    info.offset = check.offset;
                }
                dispose?.Dispose();

                uint localcrc = (uint)crc.Value;
                uint remotecrc = (uint)info.CRC32;
                if (localcrc == remotecrc)
                {
                    await ExecuteCommand(controlPoint, skipRegistring:true);
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
                    await CreateCommand(controlPoint, SecureDFUCreateCommandType.DataObject, allocateSize);
                }
                else
                {
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
                        file.Read(crcBuffer,0, crcBuffer.Length);

                        crc.Update(crcBuffer);
                    }
                    if(LastOffsetFailed != currentStartOffset)
                    {
                        LastOffsetFailed = currentStartOffset;
                        LastOffsetFailCount = 0;
                    }
                    LastOffsetFailCount++;
                    if(LastOffsetFailCount == MaxRetries)
                    {
                        throw new Exception("Too much retries for one object");
                    }
                    // Allocate memory from current object start position
                    await CreateCommand(controlPoint, SecureDFUCreateCommandType.DataObject, allocateSize);
                }
            }
        }
    }
}
