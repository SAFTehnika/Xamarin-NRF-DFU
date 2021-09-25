// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using Plugin.BluetoothLE;

namespace Plugin.XamarinNordicDFU
{
    partial class DFU
    {
        /// <summary>
        /// Switch device from the main application to Secure DFU bootloader (DFU mode).
        /// ! IMPORTANT, assumes that each device has different name !
        /// Naming example: $"DFU_{DateTime.Now:HHmmss}"
        /// </summary>
        /// <param name="device">BluetoothLE device handle.</param>
        /// <param name="dfuAdvertName">DFU advertisement name.</param>
        /// <returns>Device which is in DFU mode.</returns>
        private async Task<IDevice> ButtonlessDFUWithoutBondsToSecureDFU(IDevice device, string dfuAdvertName)
        {
            DFUEvents.OnLogMessage?.Invoke("Start of buttonless switching to DFU mode");

            IGattCharacteristic buttonlessCharacteristic = null;
            TaskCompletionSource<CharacteristicGattResult> notif = null;

            device = await device.ConnectWait(new ConnectionConfig { AutoConnect = false }).Timeout(DeviceConnectionTimeout);

            buttonlessCharacteristic = await device.GetKnownCharacteristics(DfuService, DfuButtonlessCharacteristicWithoutBonds).Timeout(OperationTimeout);
            await buttonlessCharacteristic.EnableNotifications(true).Timeout(OperationTimeout);

            // Set DFU advertisement name, it must not be longer than 20 symbols.
            var newName = new string(dfuAdvertName.Take(20).ToArray());
            byte[] name = Encoding.ASCII.GetBytes(newName);
            int newNameLen = name.Length;
            byte[] newNameCommand = new byte[newNameLen + 1 + 1];
            newNameCommand[0] = (byte)ButtonlessDFUOpCode.DFU_OP_SET_ADV_NAME;
            newNameCommand[1] = (byte)newNameLen;
            name.CopyTo(newNameCommand, 2);

            DFUEvents.OnLogMessage?.Invoke($"Set advertisement name \"{dfuAdvertName}\"");
            Debug.WriteLineIf(LogLevelDebug, $"Opcode 0x02: Set advertisement name \"{dfuAdvertName}\"");
            var nameChangeNotif = GetTimedNotification(buttonlessCharacteristic);
            await buttonlessCharacteristic.Write(newNameCommand).Timeout(OperationTimeout);
            var nameChangeResult = await nameChangeNotif.Task;
            Debug.WriteLineIf(LogLevelDebug, $"Response: {nameChangeResult.Data.ToHexString()}");

            // Jump from the main application to Secure DFU bootloader (Secure DFU mode)
            DFUEvents.OnLogMessage?.Invoke("Enter DFU mode");
            Debug.WriteLineIf(LogLevelDebug, "Opcode 0x01: Enter DFU mode");
            notif = GetTimedNotification(buttonlessCharacteristic);
            await buttonlessCharacteristic.Write(new byte[] { (byte)ButtonlessDFUOpCode.DFU_OP_ENTER_BOOTLOADER }).Timeout(OperationTimeout);
            var result = await notif.Task;
            Debug.WriteLineIf(LogLevelDebug, $"Response: {result.Data.ToHexString()}");

            /*
            * The response received from the DFU device contains:
            * +---------+--------+----------------------------------------------------+
            * | byte no | value  | description                                        |
            * +---------+--------+----------------------------------------------------+
            * | 0       | 0x20   | Response code                                      |
            * | 1       | 0x01   | The Op Code of a request that this response is for |
            * | 2       | STATUS | Status code                                        |
            * +---------+--------+----------------------------------------------------+
            */
            int status = result.Data[2];
            if (status == (byte)ButtonlessDFUResponseCode.DFU_RSP_SUCCESS)
            {
                DFUEvents.OnLogMessage?.Invoke("Secure DFU bootloader is starting");
            }

            await buttonlessCharacteristic.DisableNotifications();
            await Task.Delay(1000); // One more iOS issue...
            device.CancelConnection();

            if (status != (byte)ButtonlessDFUResponseCode.DFU_RSP_SUCCESS)
            {
                throw new Exception($"Failed to enter DFU mode, non-success result code: {status:X2}");
            }

            IDevice newDevice = await ScanDFUDevice(device, newName);

            return newDevice;
        }

        private async Task<IDevice> ScanDFUDevice(IDevice device, string newName)
        {
            IDevice newDevice = null;
            IDisposable scanner = null;
            bool DeviceFound = false;
            object locked = new object();
            var tcs = new TaskCompletionSource<bool>();

            Task task = Task.Delay(DeviceRestartTimeout).ContinueWith(t =>
            {
                lock (locked)
                {
                    if (!DeviceFound)
                    {
                        DFUEvents.OnLogMessage?.Invoke("TimeoutException");
                        scanner?.Dispose();
                        tcs.TrySetException(new TimeoutException());
                    }
                }
            });

            DFUEvents.OnLogMessage?.Invoke($"Searching for the device in DFU mode");

            // Note 1: The device enters DFU mode and starts advertising on BLE MAC address + 1.
            // This is done to prevent the client from using cached BLE services and characteristics data for the device.
            // https://infocenter.nordicsemi.com/topic/com.nordic.infocenter.sdk5.v15.3.0/service_dfu.html

            // Note 2: iOS given identifiers are not MACs, they are UUIDs that are "unique" per mac address within a single app,
            // if a single bit changes in the mac address, you get a new UUID.
            // If you reinstall the app, you'll get a new UUID, but it'll stay the same for that given mac address during the lifetime of your app.
            // https://github.com/NordicSemiconductor/IOS-Pods-DFU-Library/issues/178#issuecomment-398341062

            // Note 3: Due to the nature of UUIDs on iOS, searching by name is the only possible option.
            // "scanResult.AdvertisementData.LocalName" is not cached by central (unlike "scanResult.Device.Name").
            // This is very important, because we are unable to find the device in DFU mode if we get a wrong cached name.
            // Despite the use of LocalName, in rare cases a device in DFU mode still cannot be found, but
            // toggling Bluetooth off/on or rebooting a smartphone solves this strange iOS behaviour.
            // https://stackoverflow.com/questions/25938274/incorrect-ble-peripheral-name-with-ios

            scanner = CrossBleAdapter.Current.Scan().Subscribe(scanResult =>
            {
                Debug.WriteLineIf(LogLevelDebug, String.Format("Scanned device: Name: |{0}:{1}| UUID: {2} preuid: {3}, compareRes: {4}", device.Name, scanResult.AdvertisementData.LocalName, scanResult.Device.Uuid, device.Uuid, device.Uuid.CompareTo(scanResult.Device.Uuid)));
                if (newName == scanResult.AdvertisementData.LocalName)
                {
                    Debug.WriteLineIf(LogLevelDebug, $"Device found: Name: {scanResult.AdvertisementData.LocalName} UUID: {scanResult.Device.Uuid}");
                    DFUEvents.OnLogMessage?.Invoke($"Device found: Name: {scanResult.AdvertisementData.LocalName} UUID: {scanResult.Device.Uuid}");
                    lock (locked)
                    {
                        DeviceFound = true;
                    }
                    scanner?.Dispose();
                    newDevice = scanResult.Device;
                    tcs.TrySetResult(true);
                }
            });
            await tcs.Task;
            return newDevice;
        }
    }
}
